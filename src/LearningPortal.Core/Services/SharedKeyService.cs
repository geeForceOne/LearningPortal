using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public enum GrantState
{
    Active,
    // Past its expiry date.
    Expired,
    // The balance is spent (until the admin tops it up, or next month for a monthly one).
    UsedUp,
}

// One user's grant as the UI shows it, in dollars. BalanceSpent and MonthSpent already account
// for a month that has rolled over since the last call.
public sealed record SharedKeyGrantView(
    string UserId, DateOnly? ExpiresOn, decimal? Balance, bool Monthly,
    decimal BalanceSpent, decimal MonthSpent, decimal TotalSpent, GrantState State)
{
    // From this share of the balance down, the user is told it's running low.
    public const decimal LowShare = 0.10m;

    public bool IsUnlimited => ExpiresOn is null && Balance is null;

    public decimal? Remaining => Balance is { } balance ? Math.Max(0, balance - BalanceSpent) : null;

    public bool IsLow => State == GrantState.Active && Balance is { } balance && Remaining <= balance * LowShare;

    // Days until the last day the key works; negative once expired.
    public int? DaysLeft => ExpiresOn is { } date ? date.DayNumber - SharedKeyService.Today.DayNumber : null;

    // When a monthly balance is full again.
    public static DateOnly NextReset
    {
        get
        {
            var today = SharedKeyService.Today;
            return new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        }
    }
}

// A null balance means no balance limit; Monthly only matters with one.
public sealed record SharedKeyGrantUpdate(DateOnly? ExpiresOn, decimal? Balance, bool Monthly);

// What Admin settings shows: like a user's own keys, no key material.
public sealed record SharedKeyAdminView(SecretStatus ClaudeKey, SecretStatus OpenAiKey, IReadOnlySet<string> AllowedModels)
{
    public bool HasAnyKey => ClaudeKey == SecretStatus.Set || OpenAiKey == SecretStatus.Set;
}

// What a user with a grant can do with the shared key: which providers have a key, and which
// models the admin allows. Custom model IDs are never allowed, since they have no known price.
public sealed record SharedKeyStatus(SharedKeyGrantView Grant, bool ClaudeKey, bool OpenAiKey, IReadOnlySet<string> AllowedModels)
{
    public bool HasKey(AiProvider provider) =>
        (provider == AiProvider.Claude ? ClaudeKey : OpenAiKey) && AiModels.For(provider).Any(m => AllowedModels.Contains(m.Id));

    public bool Allows(AiChoice choice) =>
        HasKey(choice.Provider) && AllowedModels.Contains(choice.Model) && AiModels.Find(choice) is not null;

    // The choice itself when it's allowed, otherwise the first allowed model, from the same
    // provider if it has one. Null when the shared key has nothing to offer.
    public AiChoice? Usable(AiChoice choice)
    {
        if (Allows(choice))
            return choice;
        var other = choice.Provider == AiProvider.Claude ? AiProvider.OpenAi : AiProvider.Claude;
        foreach (var provider in new[] { choice.Provider, other })
        {
            if (HasKey(provider) && AiModels.For(provider).FirstOrDefault(m => AllowedModels.Contains(m.Id)) is { } model)
                return new AiChoice(provider, model.Id);
        }
        return null;
    }
}

// The admin's shared API keys: one key per provider for the whole instance, a list of models it
// may be used with, and a grant per user who may use it. Each grant has its own limits, and its
// spend is estimated from the tokens every call reports. Admins don't get grants; they use their
// own keys.
public sealed class SharedKeyService(IDbContextFactory<AppDbContext> dbFactory, SecretProtector protector)
{
    private const decimal Micros = 1_000_000m;

    // Limits count in UTC, so the month turns over at the same moment for everyone.
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static int CurrentMonth => Today.Year * 100 + Today.Month;

    public async Task<SharedKeyAdminView> GetAdminViewAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct) ?? new SiteSettings();
        return new SharedKeyAdminView(
            protector.Read(s.SharedClaudeKeyProtected).Status,
            protector.Read(s.SharedOpenAiKeyProtected).Status,
            Allowed(s.SharedAllowedModels));
    }

    // For each key, null leaves the stored key unchanged and an empty string removes it.
    public async Task SaveAsync(string? newClaudeKey, string? newOpenAiKey, IEnumerable<string> allowedModels, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
        {
            s = new SiteSettings();
            db.SiteSettings.Add(s);
        }
        if (newClaudeKey is not null)
            s.SharedClaudeKeyProtected = newClaudeKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;
        if (newOpenAiKey is not null)
            s.SharedOpenAiKeyProtected = newOpenAiKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;
        s.SharedAllowedModels = string.Join(',', allowedModels.Where(IsListed).Distinct());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, SharedKeyGrantView>> ListGrantsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.SharedKeyGrants.AsNoTracking().ToListAsync(ct)).ToDictionary(g => g.UserId, ToView);
    }

    // Creates the grant or changes its limits; what was spent so far stays. A new grant is put
    // to use right away for someone with no keys of their own; anyone else switches in Settings.
    public async Task SaveGrantAsync(string userId, SharedKeyGrantUpdate update, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var grant = await db.SharedKeyGrants.FirstOrDefaultAsync(g => g.UserId == userId, ct);
        if (grant is null)
        {
            grant = new SharedKeyGrant { UserId = userId, Month = CurrentMonth };
            db.SharedKeyGrants.Add(grant);

            var settings = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (settings is null)
            {
                settings = new UserSettings { UserId = userId };
                db.UserSettings.Add(settings);
            }
            settings.UseSharedKey = settings.ClaudeApiKeyProtected is null && settings.OpenAiApiKeyProtected is null;
        }
        grant.ExpiresOn = update.ExpiresOn;
        grant.BalanceMicros = update.Balance is { } balance ? (long)Math.Round(balance * Micros) : null;
        grant.Monthly = update.Balance is not null && update.Monthly;
        await db.SaveChangesAsync(ct);
    }

    // Makes the full balance available again. The month and total figures stay as they are.
    public async Task ResetBalanceAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SharedKeyGrants.Where(g => g.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.BalanceSpentMicros, 0L), ct);
    }

    // The user is back on their own keys from their next AI call on.
    public async Task RevokeAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SharedKeyGrants.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // Null when the user has no grant.
    public async Task<SharedKeyStatus?> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var grant = await db.SharedKeyGrants.AsNoTracking().FirstOrDefaultAsync(g => g.UserId == userId, ct);
        if (grant is null)
            return null;
        var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct) ?? new SiteSettings();
        return new SharedKeyStatus(
            ToView(grant),
            protector.Read(s.SharedClaudeKeyProtected).Status == SecretStatus.Set,
            protector.Read(s.SharedOpenAiKeyProtected).Status == SecretStatus.Set,
            Allowed(s.SharedAllowedModels));
    }

    // The connection for a call on the shared key, metered for this user. Throws a user-facing
    // error when the grant can't be used right now.
    public async Task<AiConnection> ConnectAsync(string userId, AiChoice choice, CancellationToken ct = default)
    {
        await EnsureUsableAsync(userId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        var key = protector.Read(choice.Provider == AiProvider.Claude ? s?.SharedClaudeKeyProtected : s?.SharedOpenAiKeyProtected);
        if (key.Status != SecretStatus.Set)
            throw new AiException(NotSetUp);
        return new AiConnection(choice.Provider, key.Value!, choice.Model, new Meter(this, userId, choice));
    }

    public const string NotSetUp =
        "The key from your admin isn't set up for this right now. Ask your admin, or switch to your own keys in Settings.";

    private async Task EnsureUsableAsync(string userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var grant = await db.SharedKeyGrants.AsNoTracking().FirstOrDefaultAsync(g => g.UserId == userId, ct)
            ?? throw new AiException("Your admin no longer shares a key with you. Try again to use your own keys, or add one in Settings.");
        var view = ToView(grant);
        switch (view.State)
        {
            case GrantState.Expired:
                throw new AiException(
                    $"The key from your admin expired on {Day(view.ExpiresOn!.Value)}. Ask your admin to extend it, or switch to your own keys in Settings.");
            case GrantState.UsedUp when view.Monthly:
                throw new AiException(
                    $"This month's balance on the key from your admin is used up. It's full again on {Day(SharedKeyGrantView.NextReset)}. Until then, ask your admin or switch to your own keys in Settings.");
            case GrantState.UsedUp:
                throw new AiException(
                    "The balance on the key from your admin is used up. Ask your admin for more, or switch to your own keys in Settings.");
        }
    }

    // Both updates are single statements, so calls running at the same time (a background job
    // and a graded answer, say) can't lose each other's spend.
    private async Task RecordAsync(string userId, AiChoice choice, AiUsage usage)
    {
        // A model without a price can't get here (custom IDs aren't allowed on the shared key);
        // if one ever does, it's counted at the provider's highest price rather than as free.
        var model = AiModels.Find(choice) ?? AiModels.For(choice.Provider).MaxBy(m => m.OutputPrice)!;
        var cost = (long)Math.Ceiling(model.Cost(usage) * Micros);
        var month = CurrentMonth;

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.SharedKeyGrants.Where(g => g.UserId == userId && g.Month != month)
            .ExecuteUpdateAsync(u => u
                .SetProperty(g => g.Month, month)
                .SetProperty(g => g.MonthSpentMicros, 0L)
                .SetProperty(g => g.BalanceSpentMicros, g => g.Monthly ? 0L : g.BalanceSpentMicros));
        await db.SharedKeyGrants.Where(g => g.UserId == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(g => g.BalanceSpentMicros, g => g.BalanceSpentMicros + cost)
                .SetProperty(g => g.MonthSpentMicros, g => g.MonthSpentMicros + cost)
                .SetProperty(g => g.TotalSpentMicros, g => g.TotalSpentMicros + cost));
    }

    private static SharedKeyGrantView ToView(SharedKeyGrant g)
    {
        var sameMonth = g.Month == CurrentMonth;
        var balanceSpent = g.Monthly && !sameMonth ? 0 : g.BalanceSpentMicros;
        var state = g.ExpiresOn is { } date && date < Today ? GrantState.Expired
            : g.BalanceMicros is { } balance && balanceSpent >= balance ? GrantState.UsedUp
            : GrantState.Active;
        return new SharedKeyGrantView(
            g.UserId, g.ExpiresOn, g.BalanceMicros / Micros, g.Monthly,
            balanceSpent / Micros, (sameMonth ? g.MonthSpentMicros : 0) / Micros, g.TotalSpentMicros / Micros, state);
    }

    private static string Day(DateOnly day) => day.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsListed(string modelId) => AiModels.Claude.Concat(AiModels.OpenAi).Any(m => m.Id == modelId);

    // Never saved means every listed model; models added to the list later start out not allowed.
    private static IReadOnlySet<string> Allowed(string? stored) =>
        stored is null
            ? AiModels.Claude.Concat(AiModels.OpenAi).Select(m => m.Id).ToHashSet()
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(IsListed).ToHashSet();

    private sealed class Meter(SharedKeyService service, string userId, AiChoice choice) : IAiMeter
    {
        public Task CheckAsync(CancellationToken ct) => service.EnsureUsableAsync(userId, ct);

        public Task RecordAsync(AiUsage usage) => service.RecordAsync(userId, choice, usage);
    }
}
