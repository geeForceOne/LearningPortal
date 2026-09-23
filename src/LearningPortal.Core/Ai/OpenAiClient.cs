using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearningPortal.Core.Ai;

// OpenAI via the Responses API over plain HTTP, with Structured Outputs (text.format json_schema).
// OpenAI caches long prompt prefixes automatically; prompt_cache_key keeps a topic's requests
// routed to the same cache.
public sealed class OpenAiClient(string apiKey, string model) : IAiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string> CompleteJsonAsync(AiRequest request, CancellationToken ct)
    {
        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Context))
            content.Add(new JsonObject { ["type"] = "input_text", ["text"] = request.Context });
        content.Add(new JsonObject { ["type"] = "input_text", ["text"] = request.Prompt });

        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = request.System,
            ["input"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["max_output_tokens"] = request.MaxTokens,
            ["store"] = false,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.Schema.GetRawText()),
                },
            },
        };
        if (request.CacheKey is not null)
            body["prompt_cache_key"] = request.CacheKey;

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new AiException($"Couldn't reach the AI provider: {ex.Message}");
        }

        using (response)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new AiException(DescribeError(response.StatusCode, responseText));

            return ExtractOutput(responseText);
        }
    }

    private string DescribeError(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "The AI provider rejected your API key. Check it in Settings.",
            HttpStatusCode.TooManyRequests => "The AI provider is rate-limiting your key or your quota is used up. Wait and try again.",
            HttpStatusCode.NotFound => $"The AI model \"{model}\" wasn't found. Check the model name in Settings.",
            _ => $"The AI request failed ({(int)status}): {detail ?? "no details given"}",
        };
    }

    private static string ExtractOutput(string responseText)
    {
        var root = JsonNode.Parse(responseText)!;

        if (root["status"]?.GetValue<string>() == "incomplete")
        {
            var reason = root["incomplete_details"]?["reason"]?.GetValue<string>();
            throw new AiException(reason == "max_output_tokens"
                ? "The AI's answer was cut off because it ran too long. Try fewer questions at a time."
                : $"The AI didn't finish its answer ({reason ?? "unknown reason"}). Try again.");
        }

        var sb = new StringBuilder();
        foreach (var item in root["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message")
                continue;

            foreach (var part in item["content"]?.AsArray() ?? [])
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "output_text":
                        sb.Append(part["text"]?.GetValue<string>());
                        break;
                    case "refusal":
                        throw new AiException("The AI declined to answer this request. Try rewording the material or question.");
                }
            }
        }

        if (sb.Length == 0)
            throw new AiException("The AI returned an empty answer. Try again.");

        return sb.ToString();
    }
}
