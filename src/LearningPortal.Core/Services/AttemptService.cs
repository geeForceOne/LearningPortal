using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record AttemptView(Attempt Attempt, Exam Exam, Topic Topic, IReadOnlyList<AttemptAnswer> Answers)
{
    public int AnsweredCount => Answers.Count(a => a.IsAnswered);
    public bool IsFinished => Attempt.CompletedAt is not null;
}

public sealed class AttemptService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings)
{
    // Written answers are short-form: roughly 10-20 sentences at most.
    public const int MaxWrittenAnswerLength = 2_500;

    // Longer gaps between answers are treated as the user having stepped away.
    private const int MaxSecondsPerAnswer = 15 * 60;

    // Resumes the newest unfinished attempt of this exam, or starts a new one with the questions
    // and multiple-choice options shuffled.
    public async Task<int> StartOrResumeAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.Attempts
            .Where(a => a.ExamId == examId && a.UserId == userId && a.CompletedAt == null)
            .OrderByDescending(a => a.StartedAt)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (open is not null)
            return open.Value;

        return await StartNewAsync(db, userId, examId, ct);
    }

    public async Task<int> StartNewAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Starting over discards the unfinished attempt; only finished attempts count as results.
        await db.Attempts.Where(a => a.ExamId == examId && a.UserId == userId && a.CompletedAt == null)
            .ExecuteDeleteAsync(ct);
        return await StartNewAsync(db, userId, examId, ct);
    }

    private static async Task<int> StartNewAsync(AppDbContext db, string userId, int examId, CancellationToken ct)
    {
        var exam = await db.Exams
            .Include(e => e.Questions).ThenInclude(eq => eq.Question!).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(e => e.Id == examId && e.UserId == userId, ct) ?? throw new NotFoundException();
        if (exam.Questions.Count == 0)
            throw new InvalidOperationException("This exam has no questions yet.");

        var attempt = new Attempt { ExamId = examId, UserId = userId };
        var position = 0;
        foreach (var eq in exam.Questions.OrderBy(_ => Random.Shared.Next()))
        {
            attempt.Answers.Add(new AttemptAnswer
            {
                QuestionId = eq.QuestionId,
                Position = position++,
                OptionOrder = AttemptAnswer.JoinIds(eq.Question!.Options.Select(o => o.Id).OrderBy(_ => Random.Shared.Next())),
            });
        }
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return attempt.Id;
    }

    public sealed record AttemptSummary(int Id, DateTime StartedAt, DateTime? CompletedAt, double? ScorePercent, int ActiveSeconds, int Answered, int Total);

    public async Task<IReadOnlyList<AttemptSummary>> ListForExamAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Attempts.AsNoTracking()
            .Where(a => a.ExamId == examId && a.UserId == userId)
            .OrderByDescending(a => a.StartedAt)
            .Select(a => new AttemptSummary(a.Id, a.StartedAt, a.CompletedAt, a.ScorePercent, a.ActiveSeconds,
                a.Answers.Count(x => x.AnsweredAt != null), a.Answers.Count))
            .ToListAsync(ct);
    }

    public async Task<AttemptView> GetAsync(string userId, int attemptId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var attempt = await db.Attempts.AsNoTracking()
            .Include(a => a.Exam!).ThenInclude(e => e.Topic)
            .Include(a => a.Answers).ThenInclude(x => x.Question!).ThenInclude(q => q.Options)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, ct) ?? throw new NotFoundException();

        return new AttemptView(attempt, attempt.Exam!, attempt.Exam!.Topic!, attempt.Answers.OrderBy(a => a.Position).ToList());
    }

    public async Task<AttemptAnswer> AnswerChoiceAsync(
        string userId, int attemptId, int answerId, IReadOnlyCollection<int> selectedOptionIds, int elapsedSeconds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (attempt, answer) = await LoadAnswerAsync(db, userId, attemptId, answerId, ct);
        if (selectedOptionIds.Count == 0)
            throw new ArgumentException("Pick an answer first.");

        var options = answer.Question!.Options;
        var valid = selectedOptionIds.Where(id => options.Any(o => o.Id == id)).Distinct().ToList();
        answer.SelectedOptionIds = AttemptAnswer.JoinIds(valid);
        answer.Score = ScoreChoice(options, valid, answer.Question.AllowsMultiple);
        answer.AnsweredAt = DateTime.UtcNow;
        AddTime(attempt, elapsedSeconds);
        await db.SaveChangesAsync(ct);
        return answer;
    }

    // Saves the written answer first, then grades it. If grading fails the answer stays saved
    // (Score null) and can be graded again later with GradeAsync.
    public async Task<AttemptAnswer> AnswerWrittenAsync(
        string userId, int attemptId, int answerId, string text, int elapsedSeconds, CancellationToken ct)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Write an answer first.");
        if (trimmed.Length > MaxWrittenAnswerLength)
            throw new ArgumentException($"Keep the answer under {MaxWrittenAnswerLength} characters (about 20 sentences).");

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var (attempt, answer) = await LoadAnswerAsync(db, userId, attemptId, answerId, ct);
            answer.WrittenAnswer = trimmed;
            answer.Score = null;
            answer.Feedback = null;
            answer.AnsweredAt = DateTime.UtcNow;
            AddTime(attempt, elapsedSeconds);
            await db.SaveChangesAsync(ct);
        }

        return await GradeAsync(userId, attemptId, answerId, ct);
    }

    public async Task<AttemptAnswer> GradeAsync(string userId, int attemptId, int answerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (attempt, answer) = await LoadAnswerAsync(db, userId, attemptId, answerId, ct, allowFinished: true);
        var question = answer.Question!;
        if (question.Type != QuestionType.Written || string.IsNullOrWhiteSpace(answer.WrittenAnswer))
            return answer;

        var language = await db.Topics.Where(t => t.Id == question.TopicId).Select(t => t.Language).FirstAsync(ct);
        // Only the question's own source section goes along, never the whole topic.
        var source = question.SourceSectionId is { } sid
            ? await db.MaterialSections.Where(s => s.Id == sid).Select(s => s.Text).FirstOrDefaultAsync(ct)
            : null;

        var client = AiClientFactory.Create(await settings.GetConnectionAsync(userId, ct));
        var json = await client.CompleteJsonAsync(new AiRequest
        {
            System = Prompts.GradingSystem(language),
            Prompt = Prompts.GradingPrompt(question, source, answer.WrittenAnswer),
            SchemaName = "written_grade",
            Schema = Prompts.GradingSchema,
            MaxTokens = 8_000,
        }, ct);

        try
        {
            using var doc = JsonDocument.Parse(json);
            answer.Score = Math.Clamp(doc.RootElement.GetProperty("score").GetInt32(), 0, 100);
            answer.Feedback = doc.RootElement.GetProperty("feedback").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AiException("The AI's grade couldn't be read. Try grading again.");
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return answer;
    }

    public async Task RevealAsync(string userId, int attemptId, int answerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (_, answer) = await LoadAnswerAsync(db, userId, attemptId, answerId, ct, allowFinished: true);
        answer.Revealed = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task AddActiveTimeAsync(string userId, int attemptId, int seconds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId && a.CompletedAt == null, ct);
        if (attempt is null)
            return;
        AddTime(attempt, seconds);
        await db.SaveChangesAsync(ct);
    }

    // Closes the attempt. Unanswered questions count as zero. Written answers still waiting
    // for a grade are graded first; if that fails the attempt stays open.
    public async Task FinishAsync(string userId, int attemptId, int elapsedSeconds, IProgress<string>? progress, CancellationToken ct)
    {
        List<int> ungraded;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ungraded = await db.AttemptAnswers
                .Where(a => a.AttemptId == attemptId && a.Attempt!.UserId == userId && a.AnsweredAt != null && a.Score == null)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }

        foreach (var id in ungraded)
        {
            progress?.Report("Grading your remaining answers...");
            await GradeAsync(userId, attemptId, id, ct);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var attempt = await db.Attempts.Include(a => a.Answers)
                .FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, ct) ?? throw new NotFoundException();
            if (attempt.CompletedAt is not null)
                return;

            AddTime(attempt, elapsedSeconds);
            attempt.ScorePercent = attempt.Answers.Count == 0 ? 0 : Math.Round(attempt.Answers.Average(a => a.Score ?? 0), 1);
            attempt.CompletedAt = DateTime.UtcNow;
            // Every answer is visible on the results page.
            foreach (var a in attempt.Answers)
                a.Revealed = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteAsync(string userId, int attemptId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Attempts.Where(a => a.Id == attemptId && a.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // Single answer: all or nothing. "Choose all that apply": credit for each correct pick,
    // minus each wrong pick, as a share of the correct options.
    public static int ScoreChoice(IReadOnlyCollection<QuestionOption> options, IReadOnlyCollection<int> selected, bool allowsMultiple)
    {
        var correct = options.Where(o => o.IsCorrect).Select(o => o.Id).ToHashSet();
        if (!allowsMultiple)
            return selected.Count == 1 && correct.Contains(selected.First()) ? 100 : 0;

        var hits = selected.Count(correct.Contains);
        var misses = selected.Count(id => !correct.Contains(id));
        var share = (hits - misses) / (double)correct.Count;
        return (int)Math.Round(Math.Clamp(share, 0, 1) * 100);
    }

    private static void AddTime(Attempt attempt, int seconds) =>
        attempt.ActiveSeconds += Math.Clamp(seconds, 0, MaxSecondsPerAnswer);

    private static async Task<(Attempt Attempt, AttemptAnswer Answer)> LoadAnswerAsync(
        AppDbContext db, string userId, int attemptId, int answerId, CancellationToken ct, bool allowFinished = false)
    {
        var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, ct) ?? throw new NotFoundException();
        if (attempt.CompletedAt is not null && !allowFinished)
            throw new InvalidOperationException("This attempt is already finished.");

        var answer = await db.AttemptAnswers
            .Include(a => a.Question!).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(a => a.Id == answerId && a.AttemptId == attemptId, ct) ?? throw new NotFoundException();
        return (attempt, answer);
    }
}
