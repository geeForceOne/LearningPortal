using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// ReusePercent: how much of the exam may come from the question bank; 0 means all new questions.
// Instructions: optional guidance for the AI when it writes this exam's questions.
// CodePercent: the share of code questions; only used when the topic is a programming topic.
public sealed record ExamSettings(
    string Name, int QuestionCount, ExamType Type, Difficulty Difficulty,
    int ReusePercent = ExamService.DefaultReusePercent, string? Instructions = null,
    int CodePercent = ExamService.DefaultCodePercent);

// How many questions of each kind to write or take: by type (multiple choice / written) and,
// independently, how many of them work with code. Theory is the rest.
public readonly record struct QuestionMix(int MultipleChoice, int Written, int Code)
{
    public int Total => MultipleChoice + Written;
    public int Theory => Total - Code;
}

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

    // Programming topics: a new exam asks for this share of code questions and theory for the rest.
    public const int DefaultCodePercent = 40;

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
            CodePercent = settings.CodePercent,
        };
        db.Exams.Add(exam);
        await db.SaveChangesAsync(ct);
        return exam.Id;
    }

    // How many questions the exam still needs, by type and by code/theory. Used for the
    // pre-generation estimate and by FillAsync.
    public async Task<QuestionMix> MissingAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);
        return Missing(exam);
    }

    // Tops the exam up to its question count: bank questions first (matching type, difficulty and
    // code/theory, least-used first), then AI generation for whatever is still missing.
    public async Task<FillResult> FillAsync(string userId, int examId, IProgress<string>? progress, CancellationToken ct)
    {
        int fromBank, topicId;
        QuestionMix need;
        Difficulty difficulty;
        string? instructions;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            difficulty = exam.Difficulty;
            instructions = exam.Instructions;
            need = Missing(exam);
            var programming = exam.Topic!.IsProgramming;

            progress?.Report("Checking the question bank...");
            var inExam = exam.Questions.Select(eq => eq.QuestionId).ToHashSet();
            var bank = await db.Questions
                .Where(q => q.TopicId == topicId && q.UserId == userId && q.Difficulty == difficulty && (programming || !q.IsCode))
                .Select(q => new { q.Id, q.Type, q.IsCode, Uses = db.ExamQuestions.Count(eq => eq.QuestionId == q.Id) })
                .ToListAsync(ct);

            // Each question uses up one place of its type and one of its kind, so a reused
            // question never pushes the exam past either share.
            var mcCap = BankShare(need.MultipleChoice, exam.ReusePercent);
            var writtenCap = BankShare(need.Written, exam.ReusePercent);
            var codeCap = BankShare(need.Code, exam.ReusePercent);
            var theoryCap = BankShare(need.Theory, exam.ReusePercent);
            var take = new List<int>();
            int takenMc = 0, takenWritten = 0, takenCode = 0;
            foreach (var q in bank.Where(q => !inExam.Contains(q.Id)).OrderBy(q => q.Uses).ThenBy(_ => Random.Shared.Next()))
            {
                var isMc = q.Type == QuestionType.MultipleChoice;
                if ((isMc ? mcCap : writtenCap) == 0 || (q.IsCode ? codeCap : theoryCap) == 0)
                    continue;
                take.Add(q.Id);
                if (isMc) { mcCap--; takenMc++; } else { writtenCap--; takenWritten++; }
                if (q.IsCode) { codeCap--; takenCode++; } else theoryCap--;
            }

            var order = exam.Questions.Count == 0 ? 0 : exam.Questions.Max(eq => eq.Order) + 1;
            foreach (var id in take)
                db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = id, Order = order++ });

            fromBank = take.Count;
            need = new QuestionMix(need.MultipleChoice - takenMc, need.Written - takenWritten, need.Code - takenCode);
            exam.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (need.Total == 0)
            return new FillResult(fromBank, 0, 0);

        var generated = await generator.GenerateAsync(userId, topicId, need, difficulty, instructions, progress, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var order = await db.ExamQuestions.Where(eq => eq.ExamId == examId).MaxAsync(eq => (int?)eq.Order, CancellationToken.None) ?? -1;
            foreach (var q in generated)
                db.ExamQuestions.Add(new ExamQuestion { ExamId = examId, QuestionId = q.Id, Order = ++order });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return new FillResult(fromBank, generated.Count, need.Total - generated.Count);
    }

    // Applies new settings. Questions that no longer match the type or difficulty are dropped
    // from the exam (they stay in the bank), extras beyond the new count are trimmed; call
    // FillAsync afterwards to top it up. The code/theory share is only enforced on existing
    // questions when it was changed, so saving a new name doesn't swap (and pay for) questions.
    public async Task UpdateSettingsAsync(string userId, int examId, ExamSettings settings, CancellationToken ct = default)
    {
        Validate(settings);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadForEditAsync(db, userId, examId, ct);
        var trimKind = exam.Topic!.IsProgramming && exam.CodePercent != settings.CodePercent;

        exam.Name = settings.Name.Trim();
        exam.QuestionCount = settings.QuestionCount;
        exam.Type = settings.Type;
        exam.Difficulty = settings.Difficulty;
        exam.ReusePercent = settings.ReusePercent;
        exam.Instructions = NullIfBlank(settings.Instructions);
        exam.CodePercent = settings.CodePercent;
        exam.UpdatedAt = DateTime.UtcNow;

        var target = Targets(exam);
        int keptMc = 0, keptWritten = 0, keptCode = 0, keptTheory = 0;
        foreach (var eq in exam.Questions.OrderBy(eq => eq.Order).ToList())
        {
            var q = eq.Question!;
            var isMc = q.Type == QuestionType.MultipleChoice;
            var keep = q.Difficulty == exam.Difficulty
                       && (isMc ? keptMc < target.MultipleChoice : keptWritten < target.Written)
                       && (!trimKind || (q.IsCode ? keptCode < target.Code : keptTheory < target.Theory));
            if (!keep)
            {
                db.ExamQuestions.Remove(eq);
                continue;
            }
            if (isMc) keptMc++; else keptWritten++;
            if (q.IsCode) keptCode++; else keptTheory++;
        }

        await db.SaveChangesAsync(ct);
    }

    // Swaps every question for newly generated ones. The old questions stay in the bank.
    public async Task<FillResult> RegenerateAllAsync(string userId, int examId, IProgress<string>? progress, CancellationToken ct)
    {
        int topicId;
        Difficulty difficulty;
        string? instructions;
        QuestionMix mix;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            difficulty = exam.Difficulty;
            instructions = exam.Instructions;
            mix = Targets(exam);
        }

        var generated = await generator.GenerateAsync(userId, topicId, mix, difficulty, instructions, progress, ct);
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

        return new FillResult(0, generated.Count, mix.Total - generated.Count);
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

    // Has the AI write one new question of the same type (and code or theory) and puts it in the old one's place.
    public async Task ReplaceQuestionAsync(string userId, int examId, int questionId, IProgress<string>? progress, CancellationToken ct)
    {
        Question old;
        int topicId;
        string? instructions;
        bool programming;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exam = await LoadForEditAsync(db, userId, examId, ct);
            topicId = exam.TopicId;
            instructions = exam.Instructions;
            programming = exam.Topic!.IsProgramming;
            old = exam.Questions.FirstOrDefault(x => x.QuestionId == questionId)?.Question ?? throw new NotFoundException();
        }

        var isMc = old.Type == QuestionType.MultipleChoice;
        var mix = new QuestionMix(isMc ? 1 : 0, isMc ? 0 : 1, old.IsCode && programming ? 1 : 0);
        var generated = await generator.GenerateAsync(userId, topicId, mix, old.Difficulty, instructions, progress, ct);
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
        // The AI's own tag stays on its questions; an own question follows what it now holds.
        if (q.IsUserAuthored)
            q.IsCode = LooksLikeCode(draft);
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
            IsCode = LooksLikeCode(draft),
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

    // How many of an exam's questions work with code; always 0 outside programming topics.
    public static int CodeCount(int count, int codePercent, bool programming) =>
        programming ? (count * Math.Clamp(codePercent, 0, 100) + 50) / 100 : 0;

    // How many of the needed questions may come from the bank at this reuse percentage.
    public static int BankShare(int needed, int reusePercent) => (needed * Math.Clamp(reusePercent, 0, 100) + 50) / 100;

    // Where a new exam's questions would come from, given the bank's contents per type, difficulty
    // and code/theory. Mirrors FillAsync, so the form's hint and cost estimate match what happens.
    public static (int FromBank, int Generated) Plan(
        ExamSettings s, IReadOnlyDictionary<(QuestionType Type, Difficulty Difficulty, bool IsCode), int> bank, bool programming)
    {
        var (mc, written) = Split(s.QuestionCount, s.Type);
        var need = new QuestionMix(mc, written, CodeCount(s.QuestionCount, s.CodePercent, programming));
        var typeCap = new Dictionary<QuestionType, int>
        {
            [QuestionType.MultipleChoice] = BankShare(need.MultipleChoice, s.ReusePercent),
            [QuestionType.Written] = BankShare(need.Written, s.ReusePercent),
        };
        var kindCap = new Dictionary<bool, int>
        {
            [true] = BankShare(need.Code, s.ReusePercent),
            [false] = BankShare(need.Theory, s.ReusePercent),
        };

        var fromBank = 0;
        foreach (var isCode in new[] { true, false })
        foreach (var type in new[] { QuestionType.MultipleChoice, QuestionType.Written })
        {
            var take = Math.Min(bank.GetValueOrDefault((type, s.Difficulty, isCode)), Math.Min(typeCap[type], kindCap[isCode]));
            typeCap[type] -= take;
            kindCap[isCode] -= take;
            fromBank += take;
        }
        return (fromBank, need.Total - fromBank);
    }

    private static QuestionMix Targets(Exam exam)
    {
        var (mc, written) = Split(exam.QuestionCount, exam.Type);
        return new QuestionMix(mc, written, CodeCount(exam.QuestionCount, exam.CodePercent, exam.Topic!.IsProgramming));
    }

    private static QuestionMix Missing(Exam exam)
    {
        var target = Targets(exam);
        var questions = exam.Questions.Select(eq => eq.Question!).ToList();
        var total = exam.QuestionCount - questions.Count;
        if (total <= 0)
            return default;

        // Own questions can tip the mix; fill whichever kind is short, never beyond the total.
        var (needMc, needWritten) = Balance(
            target.MultipleChoice - questions.Count(q => q.Type == QuestionType.MultipleChoice),
            target.Written - questions.Count(q => q.Type == QuestionType.Written),
            total, preferFirst: exam.Type != ExamType.Written);
        var (needCode, _) = Balance(
            target.Code - questions.Count(q => q.IsCode),
            target.Theory - questions.Count(q => !q.IsCode),
            total, preferFirst: false);
        return new QuestionMix(needMc, needWritten, needCode);
    }

    // Two shortfalls squeezed or stretched to add up to exactly total.
    private static (int First, int Second) Balance(int first, int second, int total, bool preferFirst)
    {
        first = Math.Max(0, first);
        second = Math.Max(0, second);
        while (first + second > total)
        {
            if (first >= second) first--; else second--;
        }
        if (first + second < total)
        {
            if (preferFirst) first = total - second;
            else second = total - first;
        }
        return (first, second);
    }

    private static async Task<Exam> LoadForEditAsync(AppDbContext db, string userId, int examId, CancellationToken ct) =>
        await db.Exams
            .Include(e => e.Topic)
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

    // A question the user wrote counts as a code question when it or an option holds a code block.
    private static bool LooksLikeCode(QuestionDraft d) =>
        d.Prompt.Contains("```") || d.Options.Any(o => o.Text.Contains("```"));

    private static List<QuestionOption> BuildOptions(QuestionDraft d) =>
        d.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text))
            .Select((o, i) => new QuestionOption { Order = i, Text = o.Text.Trim(), IsCorrect = o.IsCorrect })
            .ToList();
}
