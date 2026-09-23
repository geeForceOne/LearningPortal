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

        var answers = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.UserId == userId && a.Attempt.CompletedAt != null && a.Score != null
                && (topicId == null || a.Question!.TopicId == topicId))
            .Select(a => new
            {
                a.QuestionId, Score = a.Score!.Value, a.Question!.Prompt, a.Question.Type, a.Question.Difficulty,
                a.Question.TopicId, TopicName = a.Question.Topic!.Name, a.Question.SourceLabel,
            })
            .ToListAsync(ct);

        var weak = answers
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
            .ThenBy(w => w.AverageScore)
            .Take(15)
            .ToList();

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
