using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace LearningPortal.Core.Ai;

// Claude via the official Anthropic SDK. Uses the beta Messages endpoint because server-side
// refusal fallbacks are a beta feature; structured outputs and prompt caching work the same there.
public sealed class ClaudeAiClient(string apiKey, string model, IAiMeter? meter = null) : IAiClient
{
    private const string FallbackBeta = "server-side-fallback-2026-06-01";

    public async Task<string> CompleteJsonAsync(AiRequest request, CancellationToken ct)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });

        var content = new List<BetaContentBlockParam>();
        if (request.Pdf is { } pdf)
            content.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(pdf.Content) } });
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            // Cache breakpoint after the material: system + material form the reusable prefix,
            // the varying prompt comes after it.
            content.Add(new BetaTextBlockParam { Text = request.Context, CacheControl = new BetaCacheControlEphemeral() });
        }
        content.Add(new BetaTextBlockParam { Text = request.Prompt });

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = request.System } },
            Messages = [new BetaUserMessageParam { Content = content }],
            OutputConfig = new BetaOutputConfig
            {
                Format = new BetaJsonOutputFormat { Schema = ToSchemaDictionary(request.Schema) },
            },
        };

        // Refusal fallbacks re-serve a declined request on another model inside the same call.
        // Only the newest models support them; others would reject the parameter.
        if (SupportsFallbacks(model))
        {
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } } };
        }

        var response = await SendAsync(client, parameters, ct);

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        var stop = StopName(response.StopReason);
        if (stop == "refusal")
            throw new AiException("The AI declined to answer this request. Try rewording the material or question.");
        if (stop == "max_tokens")
            throw new AiCutOffException("The AI's answer was cut off because it ran too long. Try fewer questions at a time.", text);

        if (string.IsNullOrWhiteSpace(text))
            throw new AiException("The AI returned an empty answer. Try again.");

        return text;
    }

    // Claude answers in text blocks, the cited ones carrying the pages they draw on. The basic web
    // search tool works on every current model; the newer versions filter results with code
    // execution, which only some models support and which can pause a long turn.
    public async Task<WebResearch> ResearchAsync(WebResearchRequest request, CancellationToken ct)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = request.System } },
            Messages = [new BetaUserMessageParam { Content = request.Prompt }],
            Tools = [new BetaWebSearchTool20250305 { MaxUses = request.MaxSearches }],
        };
        if (SupportsFallbacks(model))
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } } };

        var response = await SendAsync(client, parameters, ct);

        var stop = StopName(response.StopReason);
        if (stop == "refusal")
            throw new AiException("The AI declined to answer this request. Try rewording the question.");
        if (stop == "max_tokens")
            throw new AiCutOffException("The AI's answer was cut off because it ran too long. Try fewer questions at a time.");
        if (stop == "pause_turn")
            throw new AiException("The web search took too long. Try again.");

        var text = new System.Text.StringBuilder();
        var citations = new List<WebCitation>();
        foreach (var block in response.Content)
        {
            if (!block.TryPickText(out var t))
                continue;
            foreach (var citation in t.Citations ?? [])
            {
                if (citation.TryPickCitationsWebSearchResultLocation(out var web))
                    citations.Add(new WebCitation(text.Length, web.Url, web.Title ?? web.Url));
            }
            text.Append(t.Text);
        }
        if (text.Length == 0)
            throw new AiException("The AI returned an empty answer. Try again.");

        return new WebResearch(text.ToString(), citations, Searches(response));
    }

    private static int Searches(BetaMessage response) => (int)(response.Usage.ServerToolUse?.WebSearchRequests ?? 0);

    // Sends the request, records what it cost on a metered key, and turns failures into messages
    // the user can act on.
    private async Task<BetaMessage> SendAsync(AnthropicClient client, MessageCreateParams parameters, CancellationToken ct)
    {
        if (meter is not null)
            await meter.CheckAsync(ct);

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
        {
            throw Translate(ex);
        }

        // input_tokens leaves out what was written to or read from the cache.
        if (meter is not null)
            await meter.RecordAsync(new AiUsage(
                response.Usage.InputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0,
                response.Usage.OutputTokens,
                Searches(response)));

        return response;
    }

    // Streams a plain-text answer. Usage comes in two parts: the prompt's tokens with the start
    // event, the output's (a running total) with each message delta. It's recorded once the stream
    // ends, also when it ends early, so a metered key is charged for what was billed.
    public async IAsyncEnumerable<string> StreamTextAsync(AiTextRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
        var content = new List<BetaContentBlockParam>();
        if (!string.IsNullOrWhiteSpace(request.Context))
            content.Add(new BetaTextBlockParam { Text = request.Context, CacheControl = new BetaCacheControlEphemeral() });
        content.Add(new BetaTextBlockParam { Text = request.Prompt });

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = request.System } },
            Messages = [new BetaUserMessageParam { Content = content }],
        };
        if (SupportsFallbacks(model))
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } } };

        if (meter is not null)
            await meter.CheckAsync(ct);

        long input = 0, cacheWrite = 0, cacheRead = 0, output = 0;
        string? stop = null;
        var started = false;
        var events = client.Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                        break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
                {
                    throw Translate(ex);
                }

                var e = events.Current;
                if (e.TryPickStart(out var start))
                {
                    started = true;
                    input = start.Message.Usage.InputTokens;
                    cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
                    cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                    output = start.Message.Usage.OutputTokens;
                }
                else if (e.TryPickDelta(out var delta))
                {
                    output = delta.Usage.OutputTokens;
                    stop = StopName(delta.Delta.StopReason) ?? stop;
                }
                else if (e.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text) && text.Text.Length > 0)
                {
                    yield return text.Text;
                }
            }
        }
        finally
        {
            await events.DisposeAsync();
            if (meter is not null && started)
                await meter.RecordAsync(new AiUsage(input, cacheWrite, cacheRead, output));
        }

        if (stop == "refusal")
            throw new AiException("The AI declined to answer this. Try rewording your question.");
        if (stop == "max_tokens")
            throw new AiCutOffException("The reply was cut off because it ran too long. Ask for a shorter answer.");
    }

    // The SDK's stop reason prints as its JSON value, quotes included ("\"max_tokens\""), so a
    // plain ToString() never equals "max_tokens" and cut-off or refused answers slipped through.
    private static string? StopName(object? reason) => reason?.ToString()?.Trim('"');

    // Turns an SDK or network failure into a message the user can act on.
    private AiException Translate(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => new AiException(meter is null
            ? "The AI provider rejected your API key. Check it in Settings."
            : "The AI provider rejected the key from your admin. Let your admin know, or switch to your own keys in Settings."),
        AnthropicRateLimitException => new AiException(
            $"The AI provider is rate-limiting {(meter is null ? "your key" : "the key from your admin")}. Wait a minute and try again."),
        AnthropicNotFoundException => new AiException($"The AI model \"{model}\" wasn't found. Check the model ID."),
        AnthropicApiException api => new AiException($"The AI request failed: {api.Message}"),
        _ => new AiException($"Couldn't reach the AI provider: {ex.Message}"),
    };

    private static bool SupportsFallbacks(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal)
        || model.StartsWith("claude-fable-5-1", StringComparison.Ordinal);

    private static Dictionary<string, JsonElement> ToSchemaDictionary(JsonElement schema) =>
        schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
