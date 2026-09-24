using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// ReusePercent: how much of the exam may come from the question bank; 0 means all new questions.
// Instructions: optional guidance for the AI when it writes this exam's questions.
public sealed record ExamSettings(
    string Name, int QuestionCount, ExamType Type, Difficulty Difficulty,
    int ReusePercent = ExamService.DefaultReusePercent, string? Instructions = null);

public sealed record ExamDetail(Exam Exam, Topic Topic, IReadOnlyList<Question> Questions);

// A question as the user typed it in the editor; also used for manual edits of AI questions.
public sealed record QuestionDraft(
    QuestionType Type,
    string Prompt,
    IReadOnlyList<(string Text, bool IsCorrect)> Options,
    string? ReferenceAnswer,
    string Explanation);

public sealed record FillResult(int FromBank, int Generated, int Missing);

public sealed class ExamService(
    IDbContextFactory<AppDbContext> dbFactory,
    QuestionGenerationService generator)
{
    public const int MaxQuestions = 100;
    public const int MaxInstructionsLength = 500;

    // A new exam takes at most this share from the bank and has the AI write the rest, so
    // practice stays mostly fresh while the bank still saves some cost.
    public const int DefaultReusePercent = 20;

    public async Task<ExamDetail> GetAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await db.Exams.AsNoTracking()
            .Include(e => e.Topic)
            .Include(e => e.Questions).ThenInclude(eq => eq.Question!).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(e => e.Id == examId && e.UserId == userId, ct) ?? throw new NotFoundException();

        var questions = exam.Questions.OrderBy(eq => eq.Order).Select(eq => eq.Question!).ToList();
        foreach (var q in questions)
            q.Options = q.Options.OrderBy(o => o.Order).ToList();
        return new ExamDetail(exam, exam.Topic!, questions);
    }

    public async Task<int> CreateAsync(string userId, int topicId, ExamSettings settings, CancellationToken ct = default)
    {
        Validate(settings);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();

        var exam = new Exam
        {
            UserId = userId,
            TopicId = topicId,
            Name = settings.Name.Trim(),
            QuestionCount = settings.QuestionCount,
            Type = settings.Type,
            Difficulty = settings.Difficulty,
            ReusePercent = settings.ReusePercent,
            Instructions = NullIfBlank(settings.Instructions),
        };
        db.Exams.Add(exam);
        await db.SaveChangesAsync(ct);
        return exam.Id;
    }

    // How many questions the exam still needs, split by kind. Used for the pre-generation
    // estimate and by FillAsync.
    public async Task<(int MultipleChoice, int Written)> MissingAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);
        return Missing(exam);
    }

    // Tops the exam up to its question count: bank questions first (matching type and
    // difficulty, least-used first), then AI generation for whatever is still missing.
    public async Task<FillResult> FillAsync(string userId, int examId, IProgress<string>? progress, CancellationToken ct)
    {
        int fromBank;
        int mcNeeded, writtenNeeded, topicId;
        Difficulty difficulty;
        string? instructions;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            difficulty = exam.Difficulty;
            instructions = exam.Instructions;
            (mcNeeded, writtenNeeded) = Missing(exam);

            progress?.Report("Checking the question bank...");
            var inExam = exam.Questions.Select(eq => eq.QuestionId).ToHashSet();
            var bank = await db.Questions
                .Where(q => q.TopicId == topicId && q.UserId == userId && q.Difficulty == difficulty)
                .Select(q => new { q.Id, q.Type, Uses = db.ExamQuestions.Count(eq => eq.QuestionId == q.Id) })
                .ToListAsync(ct);

            var candidates = bank.Where(q => !inExam.Contains(q.Id))
                .OrderBy(q => q.Uses).ThenBy(_ => Random.Shared.Next())
                .ToList();
            var takeMc = candidates.Where(q => q.Type == QuestionType.MultipleChoice)
                .Take(BankShare(mcNeeded, exam.ReusePercent)).Select(q => q.Id).ToList();
            var takeWritten = candidates.Where(q => q.Type == QuestionType.Written)
                .Take(BankShare(writtenNeeded, exam.ReusePercent)).Select(q => q.Id).ToList();

            var order = exam.Questions.Count == 0 ? 0 : exam.Questions.Max(eq => eq.Order) + 1;
            foreach (var id in takeMc.Concat(takeWritten))
                db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = id, Order = order++ });

            fromBank = takeMc.Count + takeWritten.Count;
            mcNeeded -= takeMc.Count;
            writtenNeeded -= takeWritten.Count;
            exam.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (mcNeeded + writtenNeeded == 0)
            return new FillResult(fromBank, 0, 0);

        var generated = await generator.GenerateAsync(userId, topicId, mcNeeded, writtenNeeded, difficulty, instructions, progress, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var order = await db.ExamQuestions.Where(eq => eq.ExamId == examId).MaxAsync(eq => (int?)eq.Order, CancellationToken.None) ?? -1;
            foreach (var q in generated)
                db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = q.Id, Order = ++order });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return new FillResult(fromBank, generated.Count, mcNeeded + writtenNeeded - generated.Count);
    }

    // Applies new settings. Questions that no longer match the type or difficulty are dropped
    // from the exam (they stay in the bank), extras beyond the new count are trimmed; call
    // FillAsync afterwards to top it up.
    public async Task UpdateSettingsAsync(string userId, int examId, ExamSettings settings, CancellationToken ct = default)
    {
        Validate(settings);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);

        exam.Name = settings.Name.Trim();
        exam.QuestionCount = settings.QuestionCount;
        exam.Type = settings.Type;
        exam.Difficulty = settings.Difficulty;
        exam.ReusePercent = settings.ReusePercent;
        exam.Instructions = NullIfBlank(settings.Instructions);
        exam.UpdatedAt = DateTime.UtcNow;

        var (mcTarget, writtenTarget) = Split(exam.QuestionCount, exam.Type);
        var keptMc = 0;
        var keptWritten = 0;
        foreach (var eq in exam.Questions.OrderBy(eq => eq.Order).ToList())
        {
            var q = eq.Question!;
            var keep = q.Difficulty == exam.Difficulty && (q.Type == QuestionType.MultipleChoice
                ? keptMc++ < mcTarget
                : keptWritten++ < writtenTarget);
            if (!keep)
                db.ExamQuestions.Remove(eq);
        }

        await db.SaveChangesAsync(ct);
    }

    // Swaps every question for newly generated ones. The old questions stay in the bank.
    public async Task<FillResult> RegenerateAllAsync(string userId, int examId, IProgress<string>? progress, CancellationToken ct)
    {
        int topicId;
        Difficulty difficulty;
        string? instructions;
        int mc, written;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            difficulty = exam.Difficulty;
            instructions = exam.Instructions;
            (mc, written) = Split(exam.QuestionCount, exam.Type);
        }

        var generated = await generator.GenerateAsync(userId, topicId, mc, written, difficulty, instructions, progress, ct);
        if (generated.Count == 0)
            throw new AiException("The AI didn't produce any usable questions. The exam wasn't changed.");

        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.ExamQuestions.Where(eq => eq.ExamId == examId).ExecuteDeleteAsync(CancellationToken.None);
            var order = 0;
            foreach (var q in generated)
                db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = q.Id, Order = order++ });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return new FillResult(0, generated.Count, mc + written - generated.Count);
    }

    public async Task RemoveQuestionAsync(string userId, int examId, int questionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);
        var eq = exam.Questions.FirstOrDefault(x => x.QuestionId == questionId) ?? throw new NotFoundException();
        var remaining = exam.Questions.Count - 1;
        db.ExamQuestions.Remove(eq);
        exam.QuestionCount = Math.Max(1, remaining);
        exam.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Has the AI write one new question of the same kind and puts it in the old one's place.
    public async Task ReplaceQuestionAsync(string userId, int examId, int questionId, IProgress<string>? progress, CancellationToken ct)
    {
        Question old;
        int topicId;
        string? instructions;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            instructions = exam.Instructions;
            old = exam.Questions.FirstOrDefault(x => x.QuestionId == questionId)?.Question ?? throw new NotFoundException();
        }

        var isMc = old.Type == QuestionType.MultipleChoice;
        var generated = await generator.GenerateAsync(userId, topicId, isMc ? 1 : 0, isMc ? 0 : 1, old.Difficulty, instructions, progress, ct);
        var replacement = generated.FirstOrDefault()
            ?? throw new AiException("The AI didn't produce a usable replacement. Try again.");

        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var eq = await db.ExamQuestions.FirstAsync(x => x.ExamId == examId && x.QuestionId == questionId, CancellationToken.None);
            var order = eq.Order;
            db.ExamQuestions.Remove(eq);
            db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = replacement.Id, Order = order });
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // Edits a bank question in place. The change shows up in every exam that uses it.
    public async Task UpdateQuestionAsync(string userId, int questionId, QuestionDraft draft, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = await db.Questions.Include(x => x.Options)
            .FirstOrDefaultAsync(x => x.Id == questionId && x.UserId == userId, ct) ?? throw new NotFoundException();

        q.Prompt = draft.Prompt.Trim();
        q.Explanation = draft.Explanation.Trim();
        if (q.Type == QuestionType.Written)
        {
            q.ReferenceAnswer = draft.ReferenceAnswer?.Trim();
        }
        else
        {
            db.QuestionOptions.RemoveRange(q.Options);
            q.Options = BuildOptions(draft);
            q.AllowsMultiple = q.Options.Count(o => o.IsCorrect) > 1;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task AddOwnQuestionAsync(string userId, int examId, QuestionDraft draft, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);

        var options = draft.Type == QuestionType.MultipleChoice ? BuildOptions(draft) : [];
        var q = new Question
        {
            TopicId = exam.TopicId,
            UserId = userId,
            Type = draft.Type,
            Difficulty = exam.Difficulty,
            Prompt = draft.Prompt.Trim(),
            ReferenceAnswer = draft.Type == QuestionType.Written ? draft.ReferenceAnswer?.Trim() : null,
            Explanation = draft.Explanation.Trim(),
            Options = options,
            AllowsMultiple = options.Count(o => o.IsCorrect) > 1,
            IsUserAuthored = true,
            SourceLabel = "Written by you",
        };
        db.Questions.Add(q);
        await db.SaveChangesAsync(ct);

        // Read before adding: EF fixes the new row up into exam.Questions as soon as it's added.
        var order = exam.Questions.Count == 0 ? 0 : exam.Questions.Max(x => x.Order) + 1;
        var newCount = exam.Questions.Count + 1;
        db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = q.Id, Order = order });
        exam.QuestionCount = Math.Max(exam.QuestionCount, newCount);
        exam.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == examId && e.UserId == userId, ct) ?? throw new NotFoundException();
        db.Exams.Remove(exam);
        await db.SaveChangesAsync(ct);
    }

    public static (int MultipleChoice, int Written) Split(int count, ExamType type) => type switch
    {
        ExamType.MultipleChoice => (count, 0),
        ExamType.Written => (0, count),
        _ => ((count + 1) / 2, count / 2),
    };

    // How many of the needed questions may come from the bank at this reuse percentage.
    public static int BankShare(int needed, int reusePercent) => (needed * Math.Clamp(reusePercent, 0, 100) + 50) / 100;

    // Where a new exam's questions would come from, given the bank's contents per kind and difficulty.
    // Mirrors FillAsync, so the form's hint and cost estimate match what actually happens.
    public static (int FromBank, int Generated) Plan(ExamSettings s, IReadOnlyDictionary<(QuestionType Type, Difficulty Difficulty), int> bank)
    {
        var (mc, written) = Split(s.QuestionCount, s.Type);
        var fromBank = Math.Min(BankShare(mc, s.ReusePercent), bank.GetValueOrDefault((QuestionType.MultipleChoice, s.Difficulty)))
                       + Math.Min(BankShare(written, s.ReusePercent), bank.GetValueOrDefault((QuestionType.Written, s.Difficulty)));
        return (fromBank, mc + written - fromBank);
    }

    private static (int MultipleChoice, int Written) Missing(Exam exam)
    {
        var (mc, written) = Split(exam.QuestionCount, exam.Type);
        var haveMc = exam.Questions.Count(eq => eq.Question!.Type == QuestionType.MultipleChoice);
        var haveWritten = exam.Questions.Count(eq => eq.Question!.Type == QuestionType.Written);
        var total = exam.QuestionCount - exam.Questions.Count;
        if (total <= 0)
            return (0, 0);

        // Own questions can tip the mix; fill whichever kind is short, never beyond the total.
        var needMc = Math.Max(0, mc - haveMc);
        var needWritten = Math.Max(0, written - haveWritten);
        while (needMc + needWritten > total)
        {
            if (needMc >= needWritten) needMc--; else needWritten--;
        }
        if (needMc + needWritten < total)
        {
            if (exam.Type == ExamType.Written) needWritten = total - needMc;
            else needMc = total - needWritten;
        }
        return (needMc, needWritten);
    }

    private static async Task<Exam> LoadForEditAsync(AppDbContext db, string userId, int examId, CancellationToken ct) =>
        await db.Exams
            .Include(e => e.Questions).ThenInclude(eq => eq.Question)
            .FirstOrDefaultAsync(e => e.Id == examId && e.UserId == userId, ct) ?? throw new NotFoundException();

    private static void Validate(ExamSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Name))
            throw new ArgumentException("Give the exam a name.");
        if (s.QuestionCount is < 1 or > MaxQuestions)
            throw new ArgumentException($"An exam needs between 1 and {MaxQuestions} questions.");
        if (s.ReusePercent is < 0 or > 100)
            throw new ArgumentException("Question reuse must be between 0% and 100%.");
        if (s.Instructions?.Trim().Length > MaxInstructionsLength)
            throw new ArgumentException($"Keep the instructions under {MaxInstructionsLength} characters.");
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void ValidateDraft(QuestionDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Prompt))
            throw new ArgumentException("Write the question.");
        if (string.IsNullOrWhiteSpace(d.Explanation))
            throw new ArgumentException("Add an explanation; it's what you learn from after answering.");
        if (d.Type == QuestionType.Written && string.IsNullOrWhiteSpace(d.ReferenceAnswer))
            throw new ArgumentException("Add a reference answer so written answers can be graded.");
        if (d.Type == QuestionType.MultipleChoice)
        {
            var options = d.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToList();
            if (options.Count < 2)
                throw new ArgumentException("Give at least two options.");
            if (!options.Any(o => o.IsCorrect))
                throw new ArgumentException("Mark at least one option as correct.");
            if (options.All(o => o.IsCorrect))
                throw new ArgumentException("At least one option has to be wrong.");
        }
    }

    private static List<QuestionOption> BuildOptions(QuestionDraft d) =>
        d.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text))
            .Select((o, i) => new QuestionOption { Order = i, Text = o.Text.Trim(), IsCorrect = o.IsCorrect })
            .ToList();
}
