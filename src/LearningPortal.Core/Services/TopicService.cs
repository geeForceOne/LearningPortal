using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed class NotFoundException(string message = "That item doesn't exist or isn't yours.") : Exception(message);

public sealed record TopicSummary(
    int Id,
    string Name,
    string? Description,
    string Language,
    int MaterialCount,
    int TokenTotal,
    int ExamCount,
    int QuestionCount,
    int AttemptCount,
    double? AverageScore,
    DateTime? LastPracticed,
    DateTime UpdatedAt);

public sealed record MaterialSummary(
    int Id,
    string Title,
    MaterialKind Kind,
    string? OriginalFileName,
    long SizeBytes,
    int TokenEstimate,
    int SectionCount,
    AnalysisStatus AnalysisStatus,
    string? AnalysisError,
    string? Outline,
    DateTime CreatedAt);

public sealed record ExamSummary(
    int Id,
    string Name,
    int QuestionCount,
    int TargetCount,
    ExamType Type,
    Difficulty Difficulty,
    int AttemptCount,
    double? LastScore,
    double? BestScore,
    DateTime? LastAttemptAt,
    int? UnfinishedAttemptId);

public sealed record TopicDetail(
    TopicSummary Summary,
    IReadOnlyList<MaterialSummary> Materials,
    IReadOnlyList<ExamSummary> Exams,
    int BankSize,
    bool ExceedsBudget);

public sealed class TopicService(IDbContextFactory<AppDbContext> dbFactory, LearningPortalOptions options)
{
    public async Task<IReadOnlyList<TopicSummary>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topics = await db.Topics.AsNoTracking()
            .Where(t => t.UserId == userId)
            .Select(t => new
            {
                t.Id, t.Name, t.Description, t.Language, t.UpdatedAt,
                MaterialCount = t.Materials.Count,
                TokenTotal = t.Materials.Sum(m => (int?)m.TokenEstimate) ?? 0,
                ExamCount = t.Exams.Count,
                QuestionCount = t.Questions.Count,
            })
            .ToListAsync(ct);

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.CompletedAt != null)
            .Select(a => new { a.Exam!.TopicId, a.ScorePercent, a.CompletedAt })
            .ToListAsync(ct);

        return topics
            .Select(t =>
            {
                var mine = attempts.Where(a => a.TopicId == t.Id).ToList();
                return new TopicSummary(
                    t.Id, t.Name, t.Description, t.Language, t.MaterialCount, t.TokenTotal, t.ExamCount, t.QuestionCount,
                    mine.Count,
                    mine.Count > 0 ? mine.Average(a => a.ScorePercent ?? 0) : null,
                    mine.Max(a => a.CompletedAt),
                    t.UpdatedAt);
            })
            .OrderByDescending(t => t.LastPracticed ?? t.UpdatedAt)
            .ToList();
    }

    public async Task<TopicDetail> GetAsync(string userId, int topicId, CancellationToken ct = default)
    {
        var summary = (await ListAsync(userId, ct)).FirstOrDefault(t => t.Id == topicId) ?? throw new NotFoundException();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.UserId == userId && m.TopicId == topicId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MaterialSummary(
                m.Id, m.Title, m.Kind, m.OriginalFileName, m.SizeBytes, m.TokenEstimate, m.Sections.Count,
                m.AnalysisStatus, m.AnalysisError, m.Outline, m.CreatedAt))
            .ToListAsync(ct);

        var exams = await db.Exams.AsNoTracking()
            .Where(e => e.UserId == userId && e.TopicId == topicId)
            .Select(e => new
            {
                e.Id, e.Name, e.QuestionCount, e.Type, e.Difficulty, e.CreatedAt,
                Actual = e.Questions.Count,
                Done = e.Attempts.Where(a => a.CompletedAt != null)
                    .Select(a => new { a.ScorePercent, a.CompletedAt }).ToList(),
                Open = e.Attempts.Where(a => a.CompletedAt == null).OrderByDescending(a => a.StartedAt)
                    .Select(a => (int?)a.Id).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var examSummaries = exams
            .OrderByDescending(e => e.CreatedAt)
            .Select(e =>
            {
                var last = e.Done.OrderByDescending(a => a.CompletedAt).FirstOrDefault();
                return new ExamSummary(
                    e.Id, e.Name, e.Actual, e.QuestionCount, e.Type, e.Difficulty, e.Done.Count,
                    last?.ScorePercent,
                    e.Done.Count > 0 ? e.Done.Max(a => a.ScorePercent) : null,
                    last?.CompletedAt,
                    e.Open);
            })
            .ToList();

        return new TopicDetail(summary, materials, examSummaries, summary.QuestionCount, summary.TokenTotal > options.TopicTokenBudget);
    }

    // How many bank questions exist per kind and difficulty, so exam settings can show how many
    // will be reused and how many the AI still has to write.
    public async Task<Dictionary<(QuestionType Type, Difficulty Difficulty), int>> BankCountsAsync(
        string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.Questions.AsNoTracking()
            .Where(q => q.UserId == userId && q.TopicId == topicId)
            .GroupBy(q => new { q.Type, q.Difficulty })
            .Select(g => new { g.Key.Type, g.Key.Difficulty, Count = g.Count() })
            .ToListAsync(ct);
        return counts.ToDictionary(c => (c.Type, c.Difficulty), c => c.Count);
    }

    public async Task<int> CreateAsync(string userId, string name, string? description, string language, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = new Topic
        {
            UserId = userId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? "English" : language.Trim(),
        };
        db.Topics.Add(topic);
        await db.SaveChangesAsync(ct);
        return topic.Id;
    }

    public async Task UpdateAsync(string userId, int topicId, string name, string? description, string language, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = await db.Topics.FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct) ?? throw new NotFoundException();
        topic.Name = name.Trim();
        topic.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        topic.Language = string.IsNullOrWhiteSpace(language) ? "English" : language.Trim();
        topic.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = await db.Topics.Include(t => t.Materials)
            .FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct) ?? throw new NotFoundException();

        var files = topic.Materials.Where(m => m.StoredFilePath is not null).Select(m => m.StoredFilePath!).ToList();
        db.Topics.Remove(topic);
        await db.SaveChangesAsync(ct);

        foreach (var file in files)
            MaterialService.TryDeleteFile(options, file);
    }
}
