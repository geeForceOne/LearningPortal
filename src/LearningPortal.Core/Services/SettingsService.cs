using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// What Settings shows. Deliberately carries no key material: only whether each key is set,
// missing, or stored but unreadable. Every choice has its model resolved (never blank).
public sealed record SettingsView(
    SecretStatus ClaudeKey,
    SecretStatus OpenAiKey,
    bool AdvancedModels,
    AiChoice Simple,
    AiChoice Analysis,
    AiChoice Generation)
{
    public SecretStatus KeyStatus(AiProvider provider) => provider == AiProvider.Claude ? ClaudeKey : OpenAiKey;

    public bool HasKey(AiProvider provider) => KeyStatus(provider) == SecretStatus.Set;

    // The model a task uses unless the user picks another one for that action.
    public AiChoice Default(AiTask task) =>
        !AdvancedModels ? Simple : task == AiTask.Analysis ? Analysis : Generation;

    public bool IsConfigured => HasKey(Default(AiTask.Analysis).Provider) && HasKey(Default(AiTask.Generation).Provider);
}

// A blank model in a choice means the provider's default.
public sealed record SettingsUpdate(
    bool AdvancedModels,
    AiChoice Simple,
    AiChoice Analysis,
    AiChoice Generation,
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
            protector.Read(s.ClaudeApiKeyProtected).Status,
            protector.Read(s.OpenAiApiKeyProtected).Status,
            s.AdvancedModels,
            Choice(s.Provider, s.Model),
            Choice(s.AnalysisProvider, s.AnalysisModel),
            Choice(s.GenerationProvider, s.GenerationModel));
    }

    // The model a task would use, when it's in the priced list; null for a custom model ID.
    public async Task<AiModelInfo?> GetPricedModelAsync(string userId, AiTask task, CancellationToken ct = default) =>
        AiModels.Find((await GetAsync(userId, ct)).Default(task));

    public async Task SaveAsync(string userId, SettingsUpdate update, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }

        s.AdvancedModels = update.AdvancedModels;
        (s.Provider, s.Model) = (update.Simple.Provider, StoredModel(update.Simple));
        (s.AnalysisProvider, s.AnalysisModel) = (update.Analysis.Provider, StoredModel(update.Analysis));
        (s.GenerationProvider, s.GenerationModel) = (update.Generation.Provider, StoredModel(update.Generation));

        if (update.NewClaudeKey is not null)
            s.ClaudeApiKeyProtected = update.NewClaudeKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;
        if (update.NewOpenAiKey is not null)
            s.OpenAiApiKeyProtected = update.NewOpenAiKey.Trim() is { Length: > 0 } k ? protector.Protect(k) : null;

        await db.SaveChangesAsync(ct);
    }

    // The decrypted connection for server-side AI calls. In advanced mode, choice (picked on the
    // upload or generation screen) overrides the task's default; in simple mode it's ignored.
    // Throws a user-facing error when the provider has no usable key.
    public async Task<AiConnection> GetConnectionAsync(string userId, AiTask task, AiChoice? choice = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct)
            ?? throw new AiNotConfiguredException();

        var picked = s.AdvancedModels && choice is not null
            ? Choice(choice.Provider, choice.Model)
            : !s.AdvancedModels ? Choice(s.Provider, s.Model)
            : task == AiTask.Analysis ? Choice(s.AnalysisProvider, s.AnalysisModel)
            : Choice(s.GenerationProvider, s.GenerationModel);

        var key = protector.Read(picked.Provider == AiProvider.Claude ? s.ClaudeApiKeyProtected : s.OpenAiApiKeyProtected);
        return key.Status switch
        {
            SecretStatus.Set => new AiConnection(picked.Provider, key.Value!, picked.Model),
            SecretStatus.Unreadable => throw new AiException(
                $"Your saved {AiModels.ProviderName(picked.Provider)} API key can't be read anymore. Enter it again in Settings."),
            _ when s.ClaudeApiKeyProtected is null && s.OpenAiApiKeyProtected is null => throw new AiNotConfiguredException(),
            _ => throw new AiException(
                $"There's no {AiModels.ProviderName(picked.Provider)} API key. Add it in Settings, or pick a model from a provider you have a key for."),
        };
    }

    private static AiChoice Choice(AiProvider provider, string? model) =>
        new(provider, string.IsNullOrWhiteSpace(model) ? AiModels.DefaultFor(provider) : model.Trim());

    // The provider's default is stored as null, so it follows future default changes.
    private static string? StoredModel(AiChoice c) =>
        string.IsNullOrWhiteSpace(c.Model) || c.Model.Trim() == AiModels.DefaultFor(c.Provider) ? null : c.Model.Trim();
}
