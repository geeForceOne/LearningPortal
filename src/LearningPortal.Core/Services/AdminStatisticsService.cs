using LearningPortal.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// One person's usage, for the admin. Counts and dates only: the admin never sees what anyone's
// topics or questions are about.
public sealed record UserUsage(
    string UserId, string ShownName, string? Email, DateTime CreatedAt, DateTime? LastLoginAt, DateTime? LastActiveAt,
    int Topics, int Exams, int BankQuestions, int Attempts);

// Admin-only statistics across all accounts. The one place in Core that deliberately isn't scoped to
// a single user, so callers must check the admin role first.
public sealed class AdminStatisticsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyList<UserUsage>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.UserName, u.CreatedAt, u.LastLoginAt, u.LastActiveAt })
            .ToListAsync(ct);
        var topics = await CountByUserAsync(db.Topics.Select(x => x.UserId), ct);
        var exams = await CountByUserAsync(db.Exams.Select(x => x.UserId), ct);
        var questions = await CountByUserAsync(db.Questions.Select(x => x.UserId), ct);
        var attempts = await CountByUserAsync(db.Attempts.Select(x => x.UserId), ct);

        return users.Select(u => new UserUsage(
                u.Id, Models.AppUser.ShownNameFor(u.DisplayName, u.Email, u.UserName), u.Email, u.CreatedAt,
                u.LastLoginAt, u.LastActiveAt,
                topics.GetValueOrDefault(u.Id), exams.GetValueOrDefault(u.Id),
                questions.GetValueOrDefault(u.Id), attempts.GetValueOrDefault(u.Id)))
            .OrderByDescending(u => u.LastActiveAt ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<Dictionary<string, int>> CountByUserAsync(IQueryable<string> userIds, CancellationToken ct) =>
        await userIds.GroupBy(id => id).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
