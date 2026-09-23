using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// What Settings shows. Deliberately carries no key material: only whether each key is set,
// missing, or stored but unreadable.
public sealed record SettingsView(
    AiProvider Provider,
    SecretStatus ClaudeKey,
    string ClaudeModel,
    SecretStatus OpenAiKey,
    string OpenAiModel)
{
    public bool IsConfigured => Provider switch
    {
        AiProvider.Claude => ClaudeKey == SecretStatus.Set,
        AiProvider.OpenAi => OpenAiKey == SecretStatus.Set,
        _ => false,
    };
}

public sealed record SettingsUpdate(
    AiProvider Provider,
    string? ClaudeModel,
    string? OpenAiModel,
    // null leaves the stored key unchanged; empty string removes it.
    string? NewClaudeKey,
    string? NewOpenAiKey);

public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory, SecretProtector protector)
{
    public async Task<SettingsView> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? new UserSettings();
        return new SettingsView(
            s.Provider,
            protector.Read(s.ClaudeApiKeyProtected).Status,
            string.IsNullOrWhiteSpace(s.ClaudeModel) ? AiModels.DefaultClaude : s.ClaudeModel,
            protector.Read(s.OpenAiApiKeyProtected).Status,
            string.IsNullOrWhiteSpace(s.OpenAiModel) ? AiModels.DefaultOpenAi : s.OpenAiModel);
    }

    // The user's chosen model when it's in the priced list; null for a custom model ID.
    public async Task<AiModelInfo?> GetPricedModelAsync(string userId, CancellationToken ct = default)
    {
        var view = await GetAsync(userId, ct);
        return AiModels.Find(view.Provider, view.Provider == AiProvider.Claude ? view.ClaudeModel : view.OpenAiModel);
    }

    public async Task SaveAsync(string userId, SettingsUpdate update, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }

        s.Provider = update.Provider;
        s.ClaudeModel = NullIfBlank(update.ClaudeModel);
        s.OpenAiModel = NullIfBlank(update.OpenAiModel);

        if (update.NewClaudeKey is not null)
            s.ClaudeApiKeyProtected = update.NewClaudeKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;
        if (update.NewOpenAiKey is not null)
            s.OpenAiApiKeyProtected = update.NewOpenAiKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;

        await db.SaveChangesAsync(ct);
    }

    // The decrypted connection for server-side AI calls. Throws a user-facing error when the
    // selected provider has no usable key.
    public async Task<AiConnection> GetConnectionAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct)
            ?? throw new AiNotConfiguredException();

        var (stored, model, fallbackModel) = s.Provider == AiProvider.Claude
            ? (s.ClaudeApiKeyProtected, s.ClaudeModel, AiModels.DefaultClaude)
            : (s.OpenAiApiKeyProtected, s.OpenAiModel, AiModels.DefaultOpenAi);

        var key = protector.Read(stored);
        return key.Status switch
        {
            SecretStatus.Set => new AiConnection(s.Provider, key.Value!, string.IsNullOrWhiteSpace(model) ? fallbackModel : model),
            SecretStatus.Unreadable => throw new AiException("Your saved API key can't be read anymore. Enter it again in Settings."),
            _ => throw new AiNotConfiguredException(),
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
