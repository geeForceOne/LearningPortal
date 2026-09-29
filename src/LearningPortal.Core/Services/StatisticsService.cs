using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record ScorePoint(int AttemptId, DateTime CompletedAt, double Score, int ActiveSeconds);

public sealed record ExamProgress(int ExamId, string ExamName, int TopicId, string TopicName, IReadOnlyList<ScorePoint> Points)
{
    public double Latest => Points[^1].Score;
    public double Best => Points.Max(p => p.Score);
    // Change from the first attempt to the latest one, in percentage points.
    public double Change => Points.Count > 1 ? Points[^1].Score - Points[0].Score : 0;
}

public sealed record WeakQuestion(
    int QuestionId, int TopicId, string TopicName, string Prompt, QuestionType Type, Difficulty Difficulty,
    int Answered, int Missed, double AverageScore, string? SourceLabel);

// The concept map: each material's sections with how well their questions go. AverageScore is null
// while nothing from the section has been answered in a finished attempt.
public sealed record SectionScore(
    int SectionId, int Index, string Heading, int TokenEstimate, int QuestionCount, int Answered, int Missed, double? AverageScore);

public sealed record MaterialMap(int MaterialId, string Title, IReadOnlyList<SectionScore> Sections);

public sealed record BreakdownCell(QuestionType Type, Difficulty Difficulty, int Answered, double AverageScore);

public sealed record StatisticsReport(
    int CompletedAttempts,
    int TotalActiveSeconds,
    double? OverallAverage,
    IReadOnlyList<TopicSummary> Topics,
    IReadOnlyList<ExamProgress> Exams,
    IReadOnlyList<WeakQuestion> WeakQuestions,
    IReadOnlyList<BreakdownCell> Breakdown);

public sealed class StatisticsService(IDbContextFactory<AppDbContext> dbFactory, TopicService topics)
{
    private sealed record GradedAnswer(
        int QuestionId, int Score, string Prompt, QuestionType Type, Difficulty Difficulty,
        int TopicId, string TopicName, string? SourceLabel);

    // A topic's questions answered wrong or only partly right, weakest first: the highest share of
    // misses, then the most misses, then the lowest average score.
    public async Task<IReadOnlyList<WeakQuestion>> GetWeakQuestionsAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return RankWeak(await GradedAnswersAsync(db, userId, topicId, ct)).ToList();
    }

    public async Task<IReadOnlyList<MaterialMap>> GetSectionMapAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sections = await db.MaterialSections.AsNoTracking()
            .Where(s => s.Material!.UserId == userId && s.Material.TopicId == topicId)
            .Select(s => new { s.Id, s.MaterialId, MaterialTitle = s.Material!.Title, MaterialCreated = s.Material.CreatedAt, s.Index, s.Heading, s.TokenEstimate })
            .ToListAsync(ct);

        var questionCounts = await db.Questions.AsNoTracking()
            .Where(q => q.UserId == userId && q.TopicId == topicId && q.SourceSectionId != null)
            .GroupBy(q => q.SourceSectionId!.Value)
            .Select(g => new { SectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SectionId, x => x.Count, ct);

        var scores = (await db.AttemptAnswers.AsNoTracking()
                .Where(a => a.Attempt!.UserId == userId && a.Attempt.CompletedAt != null && a.Score != null
                    && a.Question!.TopicId == topicId && a.Question.SourceSectionId != null)
                .Select(a => new { SectionId = a.Question!.SourceSectionId!.Value, Score = a.Score!.Value })
                .ToListAsync(ct))
            .GroupBy(a => a.SectionId)
            .ToDictionary(g => g.Key, g => (Answered: g.Count(),
                Missed: g.Count(a => VerdictRules.FromScore(a.Score) != Verdict.Correct), Average: g.Average(a => a.Score)));

        return sections
            .GroupBy(s => (s.MaterialId, s.MaterialTitle, s.MaterialCreated))
            .OrderBy(g => g.Key.MaterialCreated)
            .Select(g => new MaterialMap(g.Key.MaterialId, g.Key.MaterialTitle, g.OrderBy(s => s.Index).Select(s =>
            {
                var hasScore = scores.TryGetValue(s.Id, out var sc);
                return new SectionScore(s.Id, s.Index, s.Heading, s.TokenEstimate, questionCounts.GetValueOrDefault(s.Id),
                    hasScore ? sc.Answered : 0, hasScore ? sc.Missed : 0, hasScore ? sc.Average : null);
            }).ToList()))
            .ToList();
    }

    // A section's questions for practice: the most often missed first, then the ones never answered,
    // then the rest by lowest average score.
    public async Task<IReadOnlyList<int>> RankSectionQuestionsAsync(string userId, int topicId, int sectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var questionIds = await db.Questions.AsNoTracking()
            .Where(q => q.UserId == userId && q.TopicId == topicId && q.SourceSectionId == sectionId)
            .Select(q => q.Id)
            .ToListAsync(ct);
        var graded = (await db.AttemptAnswers.AsNoTracking()
                .Where(a => a.Attempt!.UserId == userId && a.Attempt.CompletedAt != null && a.Score != null && questionIds.Contains(a.QuestionId))
                .Select(a => new { a.QuestionId, Score = a.Score!.Value })
                .ToListAsync(ct))
            .GroupBy(a => a.QuestionId)
            .ToDictionary(g => g.Key, g => (MissedShare: g.Count(a => VerdictRules.FromScore(a.Score) != Verdict.Correct) / (double)g.Count(), Average: g.Average(a => a.Score)));

        return questionIds
            .OrderByDescending(id => graded.TryGetValue(id, out var g) ? g.MissedShare : 0.5)
            .ThenBy(id => graded.TryGetValue(id, out var g) ? g.Average : 50)
            .ThenBy(_ => Random.Shared.Next())
            .ToList();
    }

    private static Task<List<GradedAnswer>> GradedAnswersAsync(AppDbContext db, string userId, int? topicId, CancellationToken ct) =>
        db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.UserId == userId && a.Attempt.CompletedAt != null && a.Score != null
                && (topicId == null || a.Question!.TopicId == topicId))
            .Select(a => new GradedAnswer(
                a.QuestionId, a.Score!.Value, a.Question!.Prompt, a.Question.Type, a.Question.Difficulty,
                a.Question.TopicId, a.Question.Topic!.Name, a.Question.SourceLabel))
            .ToListAsync(ct);

    private static IEnumerable<WeakQuestion> RankWeak(IEnumerable<GradedAnswer> answers) => answers
        .GroupBy(a => a.QuestionId)
        .Select(g =>
        {
            var f = g.First();
            return new WeakQuestion(f.QuestionId, f.TopicId, f.TopicName, f.Prompt, f.Type, f.Difficulty,
                g.Count(), g.Count(a => VerdictRules.FromScore(a.Score) != Verdict.Correct), g.Average(a => a.Score), f.SourceLabel);
        })
        .Where(w => w.Missed > 0)
        .OrderByDescending(w => w.Missed / (double)w.Answered)
        .ThenByDescending(w => w.Missed)
        .ThenBy(w => w.AverageScore);

    public async Task<StatisticsReport> GetAsync(string userId, int? topicId = null, CancellationToken ct = default)
    {
        var topicList = (await topics.ListAsync(userId, ct))
            .Where(t => topicId is null || t.Id == topicId)
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.CompletedAt != null && (topicId == null || a.Exam!.TopicId == topicId))
            .Select(a => new
            {
                a.Id, a.ExamId, ExamName = a.Exam!.Name, a.Exam.TopicId, TopicName = a.Exam.Topic!.Name,
                CompletedAt = a.CompletedAt!.Value, Score = a.ScorePercent ?? 0, a.ActiveSeconds,
            })
            .ToListAsync(ct);

        var exams = attempts
            .GroupBy(a => a.ExamId)
            .Select(g =>
            {
                var first = g.First();
                return new ExamProgress(first.ExamId, first.ExamName, first.TopicId, first.TopicName,
                    g.OrderBy(a => a.CompletedAt).Select(a => new ScorePoint(a.Id, a.CompletedAt, a.Score, a.ActiveSeconds)).ToList());
            })
            .OrderByDescending(e => e.Points[^1].CompletedAt)
            .ToList();

        var answers = await GradedAnswersAsync(db, userId, topicId, ct);
        var weak = RankWeak(answers).Take(15).ToList();

        var breakdown = answers
            .GroupBy(a => (a.Type, a.Difficulty))
            .Select(g => new BreakdownCell(g.Key.Type, g.Key.Difficulty, g.Count(), g.Average(a => a.Score)))
            .OrderBy(c => c.Type).ThenBy(c => c.Difficulty)
            .ToList();

        return new StatisticsReport(
            attempts.Count,
            attempts.Sum(a => a.ActiveSeconds),
            attempts.Count > 0 ? attempts.Average(a => a.Score) : null,
            topicList,
            exams,
            weak,
            breakdown);
    }
}
