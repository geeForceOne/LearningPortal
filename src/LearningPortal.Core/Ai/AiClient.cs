using System.Text.Json;
using LearningPortal.Core.Models;

namespace LearningPortal.Core.Ai;

// A user-facing failure: the message is shown as-is, so it says what went wrong and what to do.
public class AiException(string message) : Exception(message);

public sealed class AiNotConfiguredException()
    : AiException("AI isn't set up yet. Add your AI provider and API key in Settings.");

// Which provider, key and model a user's requests go to. Built by SettingsService from the
// user's decrypted settings and never sent to the browser. Meter is set when the key is the
// admin's shared one, whose use is limited and tracked per user.
public sealed record AiConnection(AiProvider Provider, string ApiKey, string Model, IAiMeter? Meter = null);

// The tokens one call was billed for. InputTokens are the ordinary ones only: tokens written to
// or read from the provider's prompt cache are counted separately, since they cost differently.
// WebSearches is how many web searches the call ran, billed per search on top of the tokens.
public sealed record AiUsage(long InputTokens, long CacheWriteTokens, long CacheReadTokens, long OutputTokens, int WebSearches = 0);

// Guards and tracks calls made with a key that has limits. The clients call CheckAsync before
// each request and RecordAsync as soon as the provider answers, also when the answer is then
// rejected (cut off, refused): it was billed all the same.
public interface IAiMeter
{
    // Throws an AiException saying why when the key may not be used (anymore).
    Task CheckAsync(CancellationToken ct);

    Task RecordAsync(AiUsage usage);
}

// A provider and model the user picked, without the key. Safe to show and pass around in the UI.
public sealed record AiChoice(AiProvider Provider, string Model);

// What an AI call is for. In advanced mode each task has its own default model; in simple mode
// one model does everything.
public enum AiTask
{
    // Outlining uploaded material.
    Analysis,
    // Writing and replacing questions, and grading written answers.
    Generation,
}

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

    // A PDF the AI reads itself, pages as images included: for a scanned exam paper that has no
    // text to extract. Sent before the prompt.
    public AiPdf? Pdf { get; init; }

    // Groups requests that share a prefix (e.g. "topic-12") for providers that route caches by key.
    public string? CacheKey { get; init; }
}

public sealed record AiPdf(string FileName, byte[] Content);

// A request that lets the AI search the web and answers in plain text with the pages it used.
// Both providers' web search tools are used without a response schema, so the answer comes back
// as text; a second, ordinary call turns it into structured data where needed.
public sealed record WebResearchRequest
{
    public required string System { get; init; }
    public required string Prompt { get; init; }

    // A hard cap on the searches, which are billed one by one.
    public required int MaxSearches { get; init; }

    public int MaxTokens { get; init; } = 12_000;
}

// The answer text, the pages it cites and where in the text each citation applies (a character
// offset), and how many searches ran.
public sealed record WebResearch(string Text, IReadOnlyList<WebCitation> Citations, int Searches);

public sealed record WebCitation(int Position, string Url, string Title);

public interface IAiClient
{
    // Returns the model's JSON response text, already checked to be complete.
    Task<string> CompleteJsonAsync(AiRequest request, CancellationToken ct);

    Task<WebResearch> ResearchAsync(WebResearchRequest request, CancellationToken ct);
}

public static class AiClientFactory
{
    public static IAiClient Create(AiConnection connection) => connection.Provider switch
    {
        AiProvider.Claude => new ClaudeAiClient(connection.ApiKey, connection.Model, connection.Meter),
        AiProvider.OpenAi => new OpenAiClient(connection.ApiKey, connection.Model, connection.Meter),
        _ => throw new AiException($"Unknown AI provider {connection.Provider}."),
    };
}

// One entry in the Settings model picker. Prices are USD per million tokens; CachedInputPrice is
// for input read back from the provider's prompt cache.
public sealed record AiModelInfo(string Id, string Name, string Description, decimal InputPrice, decimal OutputPrice, decimal CachedInputPrice)
{
    // Both providers bill input written to the prompt cache at this multiple of the input price.
    public const decimal CacheWriteFactor = 1.25m;

    // Both providers bill a web search at $10 per 1,000, whatever the model.
    public const decimal WebSearchPrice = 0.01m;

    // Some models bill a whole call at a multiple of their prices once its prompt (input, cache
    // writes and cache reads together) is longer than this many tokens. Null: one price throughout.
    public int? LongPromptTokens { get; init; }
    public decimal LongPromptFactor { get; init; } = 1m;

    // tokens is the input of all calls together, so the prompt length is judged per call.
    public decimal InputCost(int tokens, int calls = 1) =>
        tokens * InputPrice * FactorFor(tokens / Math.Max(calls, 1)) / 1_000_000m;

    // What a finished call cost, from the tokens the provider reported.
    public decimal Cost(AiUsage usage) =>
        (usage.InputTokens * InputPrice
         + usage.CacheWriteTokens * InputPrice * CacheWriteFactor
         + usage.CacheReadTokens * CachedInputPrice
         + usage.OutputTokens * OutputPrice)
        * FactorFor(usage.InputTokens + usage.CacheWriteTokens + usage.CacheReadTokens) / 1_000_000m
        + usage.WebSearches * WebSearchPrice;

    private decimal FactorFor(long promptTokens) => promptTokens > LongPromptTokens ? LongPromptFactor : 1m;
}

public static class AiModels
{
    // Default model per provider; users can change them in Settings.
    public const string DefaultClaude = "claude-opus-5-5";
    public const string DefaultOpenAi = "gpt-6.1-sol";

    // Neither provider's API reports prices, so these are copied from their pricing pages.
    // Update the list and this date together.
    public const string PricesAsOf = "October 2026";
    public const string ClaudePricingUrl = "https://platform.claude.com/docs/en/about-claude/pricing";
    public const string OpenAiPricingUrl = "https://developers.openai.com/api/docs/pricing";

    public static AiModelInfo? Find(AiProvider provider, string modelId) =>
        For(provider).FirstOrDefault(m => m.Id == modelId);

    public static AiModelInfo? Find(AiChoice choice) => Find(choice.Provider, choice.Model);

    public static IReadOnlyList<AiModelInfo> For(AiProvider provider) => provider == AiProvider.Claude ? Claude : OpenAi;

    public static string DefaultFor(AiProvider provider) => provider == AiProvider.Claude ? DefaultClaude : DefaultOpenAi;

    public static string PricingUrlFor(AiProvider provider) => provider == AiProvider.Claude ? ClaudePricingUrl : OpenAiPricingUrl;

    public static string ProviderName(AiProvider provider) => provider == AiProvider.Claude ? "Claude (Anthropic)" : "ChatGPT (OpenAI)";

    // The model's display name, or the raw ID for a custom model. Used for "Written by ...".
    public static string DisplayName(string modelId) =>
        Claude.Concat(OpenAi).FirstOrDefault(m => m.Id == modelId)?.Name
        ?? (Retired.TryGetValue(modelId, out var retired) ? retired.Name : modelId);

    // Models taken off the list: the name still shown for what they wrote, and the listed model
    // that saved choices are moved to at startup (SettingsService.ReplaceRetiredModelsAsync).
    public static readonly IReadOnlyDictionary<string, (string Name, string Replacement)> Retired =
        new Dictionary<string, (string, string)>
        {
            ["claude-haiku-4-5"] = ("Claude Haiku 4.5", "claude-haiku-5-5"),
        };

    public static readonly IReadOnlyList<AiModelInfo> Claude =
    [
        new("claude-fable-5-1", "Claude Fable 5.1", "Anthropic's most capable model. Best for very dense or technical material, at a premium price.", 10m, 50m, 0.25m),
        new("claude-opus-5-5", "Claude Opus 5.5", "Newest Opus: top quality at a lower price than Opus 5.", 4m, 20m, 0.20m),
        new("claude-opus-5", "Claude Opus 5", "Highly capable all-rounder for writing and grading questions.", 5m, 25m, 0.50m),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Newest Sonnet: strong quality at a much lower price. A good everyday choice.", 2m, 10m, 0.10m),
        new("claude-sonnet-5", "Claude Sonnet 5", "Previous Sonnet, same price as Sonnet 5.5. Kept for anyone who picked it.", 2m, 10m, 0.20m),
        new("claude-haiku-5-5", "Claude Haiku 5.5", "Fastest and cheapest. Fine for easy recall questions; weaker on hard ones. A call with more than 100k tokens of material costs five times the listed price.", 0.10m, 0.50m, 0.01m)
            { LongPromptTokens = 100_000, LongPromptFactor = 5m },
    ];

    public static readonly IReadOnlyList<AiModelInfo> OpenAi =
    [
        new("gpt-6-astra", "GPT-6 Astra", "OpenAI's most capable model, at a premium price.", 10m, 50m, 1m),
        new("gpt-6.1-sol", "GPT-6.1 Sol", "Newest Sol: near-Astra quality at a much lower price. A good everyday choice.", 2m, 10m, 0.10m),
        new("gpt-6-sol", "GPT-6 Sol", "Previous Sol, same price as GPT-6.1 Sol. Kept for anyone who picked it.", 2m, 10m, 0.20m),
        new("gpt-6-luna", "GPT-6 Luna", "Cheapest and fastest. Fine for easy recall questions; weaker on hard ones.", 0.10m, 0.50m, 0.01m),
    ];
}
