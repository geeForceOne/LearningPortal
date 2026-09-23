using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace LearningPortal.Core.Ai;

// Claude via the official Anthropic SDK. Uses the beta Messages endpoint because server-side
// refusal fallbacks are a beta feature; structured outputs and prompt caching work the same there.
public sealed class ClaudeAiClient(string apiKey, string model) : IAiClient
{
    private const string FallbackBeta = "server-side-fallback-2026-06-01";

    public async Task<string> CompleteJsonAsync(AiRequest request, CancellationToken ct)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });

        var content = new List<BetaContentBlockParam>();
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
            Messages = [new BetaMessageParam { Role = Role.User, Content = content }],
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

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiException("The AI provider rejected your API key. Check it in Settings.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiException("The AI provider is rate-limiting your key. Wait a minute and try again.");
        }
        catch (AnthropicNotFoundException)
        {
            throw new AiException($"The AI model \"{model}\" wasn't found. Check the model name in Settings.");
        }
        catch (AnthropicApiException ex)
        {
            throw new AiException($"The AI request failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
        {
            throw new AiException($"Couldn't reach the AI provider: {ex.Message}");
        }

        var stop = response.StopReason?.ToString();
        if (stop == "refusal")
            throw new AiException("The AI declined to answer this request. Try rewording the material or question.");
        if (stop == "max_tokens")
            throw new AiException("The AI's answer was cut off because it ran too long. Try fewer questions at a time.");

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (string.IsNullOrWhiteSpace(text))
            throw new AiException("The AI returned an empty answer. Try again.");

        return text;
    }

    private static bool SupportsFallbacks(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal)
        || model.StartsWith("claude-fable-5-1", StringComparison.Ordinal);

    private static Dictionary<string, JsonElement> ToSchemaDictionary(JsonElement schema) =>
        schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
