using System.Security.Cryptography;
using System.Text;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record HomepageStats(int Accounts, int ActiveThisWeek, int ActiveNow);

// Instance-wide numbers for a gethomepage.dev "customapi" widget. Homepage polls without anyone
// signed in, so the endpoint takes one token for the whole instance (created by an admin) instead
// of a session. Only the token's SHA-256 hash is stored: a copy of the database can't be used to
// call the endpoint, and losing the Data Protection keys doesn't break it.
public sealed class HomepageStatsService(IDbContextFactory<AppDbContext> dbFactory)
{
    // LastActiveAt is written at most every UserActivityService.ActiveResolution (5 minutes), so
    // "just now" has to be wider than that.
    public static readonly TimeSpan ActiveNowWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ActiveWeekWindow = TimeSpan.FromDays(7);

    // When the current token was created, or null when there is none.
    public async Task<DateTime?> GetTokenCreatedAtAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SiteSettings.AsNoTracking().Where(s => s.Id == 1 && s.HomepageTokenHash != null)
            .Select(s => s.HomepageTokenCreatedAt).FirstOrDefaultAsync(ct);
    }

    // Makes a new token, replacing any old one, and returns it. It's shown once and never stored.
    public async Task<string> CreateTokenAsync(CancellationToken ct = default)
    {
        var token = "rcl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await SettingsAsync(db, ct);
        s.HomepageTokenHash = Hash(token);
        s.HomepageTokenCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task RevokeTokenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await SettingsAsync(db, ct);
        s.HomepageTokenHash = null;
        s.HomepageTokenCreatedAt = null;
        await db.SaveChangesAsync(ct);
    }

    // The stats, or null when the token doesn't match (or none was created).
    public async Task<HomepageStats?> GetStatsAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.SiteSettings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => s.HomepageTokenHash).FirstOrDefaultAsync(ct);
        if (stored is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(token.Trim())), Encoding.ASCII.GetBytes(stored)))
            return null;

        var now = DateTime.UtcNow;
        var weekFrom = now - ActiveWeekWindow;
        var nowFrom = now - ActiveNowWindow;
        return new HomepageStats(
            await db.Users.CountAsync(ct),
            await db.Users.CountAsync(u => u.LastActiveAt >= weekFrom, ct),
            await db.Users.CountAsync(u => u.LastActiveAt >= nowFrom, ct));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<SiteSettings> SettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
        {
            s = new SiteSettings();
            db.SiteSettings.Add(s);
        }
        return s;
    }
}
