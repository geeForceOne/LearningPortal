using System.Text.Json;
using LearningPortal.Core.Models;

namespace LearningPortal.Core.Ai;

// A user-facing failure: the message is shown as-is, so it says what went wrong and what to do.
public class AiException(string message) : Exception(message);

public sealed class AiNotConfiguredException()
    : AiException("AI isn't set up yet. Add your AI provider and API key in Settings.");

// Which provider, key and model a user's requests go to. Built by SettingsService from the
// user's decrypted settings and never sent to the browser.
public sealed record AiConnection(AiProvider Provider, string ApiKey, string Model);

public sealed record AiRequest
{
    // Stable instructions. Kept free of per-request details so the prefix can be cached.
    public required string System { get; init; }

    // Large, reusable input (the topic's material). Placed before the prompt and marked for
    // caching, so follow-up calls on the same topic re-read it cheaply.
    public string? Context { get; init; }

    public required string Prompt { get; init; }

    public required string SchemaName { get; init; }

    // JSON Schema for the response. Written to satisfy both providers' strict modes: every
    // property required, additionalProperties false, no nullable types.
    public required JsonElement Schema { get; init; }

    public int MaxTokens { get; init; } = 16_000;

    // Groups requests that share a prefix (e.g. "topic-12") for providers that route caches by key.
    public string? CacheKey { get; init; }
}

public interface IAiClient
{
    // Returns the model's JSON response text, already checked to be complete.
    Task<string> CompleteJsonAsync(AiRequest request, CancellationToken ct);
}

public static class AiClientFactory
{
    public static IAiClient Create(AiConnection connection) => connection.Provider switch
    {
        AiProvider.Claude => new ClaudeAiClient(connection.ApiKey, connection.Model),
        AiProvider.OpenAi => new OpenAiClient(connection.ApiKey, connection.Model),
        _ => throw new AiException($"Unknown AI provider {connection.Provider}."),
    };
}

// One entry in the Settings model picker. Prices are USD per million tokens.
public sealed record AiModelInfo(string Id, string Name, string Description, decimal InputPrice, decimal OutputPrice)
{
    public decimal InputCost(int tokens) => tokens * InputPrice / 1_000_000m;
}

public static class AiModels
{
    // Most capable general-purpose defaults for each provider; users can change them in Settings.
    public const string DefaultClaude = "claude-opus-5";
    public const string DefaultOpenAi = "gpt-6-astra";

    // Neither provider's API reports prices, so these are copied from their pricing pages.
    // Update the list and this date together.
    public const string PricesAsOf = "September 2026";
    public const string ClaudePricingUrl = "https://platform.claude.com/docs/en/about-claude/pricing";
    public const string OpenAiPricingUrl = "https://developers.openai.com/api/docs/pricing";

    public static AiModelInfo? Find(AiProvider provider, string modelId) =>
        (provider == AiProvider.Claude ? Claude : OpenAi).FirstOrDefault(m => m.Id == modelId);

    public static readonly IReadOnlyList<AiModelInfo> Claude =
    [
        new("claude-fable-5-1", "Claude Fable 5.1", "Anthropic's most capable model. Best for very dense or technical material, at a premium price.", 10m, 50m),
        new("claude-opus-5-5", "Claude Opus 5.5", "Newest Opus: top quality at a lower price than Opus 5.", 4m, 20m),
        new("claude-opus-5", "Claude Opus 5", "Highly capable all-rounder for writing and grading questions.", 5m, 25m),
        new("claude-sonnet-5", "Claude Sonnet 5", "Strong quality at a much lower price. A good everyday choice.", 2m, 10m),
        new("claude-haiku-4-5", "Claude Haiku 4.5", "Fastest and cheapest. Fine for easy recall questions; weaker on hard ones.", 1m, 5m),
    ];

    public static readonly IReadOnlyList<AiModelInfo> OpenAi =
    [
        new("gpt-6-astra", "GPT-6 Astra", "OpenAI's most capable model, at a premium price.", 10m, 50m),
        new("gpt-6-sol", "GPT-6 Sol", "Balanced quality and price. A good everyday choice.", 2m, 10m),
        new("gpt-6-luna", "GPT-6 Luna", "Cheapest and fastest. Fine for easy recall questions; weaker on hard ones.", 0.10m, 0.50m),
    ];
}
