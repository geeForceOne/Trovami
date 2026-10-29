using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Trovami.Core.Models;

namespace Trovami.Core.Ai;

// A failure the household's owner can act on; the message is shown as it is.
public sealed class AiException(string message) : Exception(message);

public sealed record AiModelInfo(string Id, string Name, string Description);

public static class AiModels
{
    // Naming an item from one photo is easy work, so the cheapest models are the default.
    public const string DefaultClaude = "claude-haiku-5-5";
    public const string DefaultOpenAi = "gpt-6-luna";

    public static readonly IReadOnlyList<AiModelInfo> Claude =
    [
        new("claude-haiku-5-5", "Claude Haiku 5.5", "Fastest and cheapest; names everyday things well. Recommended."),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Better with unusual or technical things, at about 20 times the price."),
        new("claude-opus-5-5", "Claude Opus 5.5", "Most capable, at about 40 times the price."),
    ];

    public static readonly IReadOnlyList<AiModelInfo> OpenAi =
    [
        new("gpt-6-luna", "GPT-6 Luna", "Fastest and cheapest; names everyday things well. Recommended."),
        new("gpt-6.1-sol", "GPT-6.1 Sol", "Better with unusual or technical things, at about 20 times the price."),
    ];

    public static IReadOnlyList<AiModelInfo> For(AiProvider provider) => provider == AiProvider.OpenAi ? OpenAi : Claude;

    public static string DefaultFor(AiProvider provider) => provider == AiProvider.OpenAi ? DefaultOpenAi : DefaultClaude;

    public static string ProviderName(AiProvider provider) => provider switch
    {
        AiProvider.Claude => "Claude (Anthropic)",
        AiProvider.OpenAi => "ChatGPT (OpenAI)",
        _ => "Off",
    };
}

// Names one item from its photo. Returns null when the AI can't tell what it is.
public interface IAiNamer
{
    Task<string?> NameAsync(byte[] jpeg, CancellationToken ct);
}

public static class AiNamer
{
    internal const string Instructions =
        "The photo shows one thing someone is putting away in their home inventory, so they can find it again later. " +
        "Reply with a short, plain name for it in US English, 1 to 5 words, the way a person would label a box: " +
        "for example \"Cordless drill\", \"Christmas lights\", \"Passport\", \"Winter boots\". " +
        "Name the main thing in the photo; mention the brand only when it helps to tell it apart. " +
        "Reply with the name only, no punctuation at the end. If you can't tell what it is, reply with exactly UNKNOWN.";

    public static IAiNamer Create(AiProvider provider, string apiKey, string model) => provider switch
    {
        AiProvider.Claude => new ClaudeNamer(apiKey, model),
        AiProvider.OpenAi => new OpenAiNamer(apiKey, model),
        _ => throw new AiException("AI naming is off for this household."),
    };

    // The reply as a name: first line, without quotes or a closing period; null for UNKNOWN or
    // nothing usable.
    public static string? CleanReply(string? reply)
    {
        var line = (reply ?? "").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        line = line.Trim('"', '\'', '“', '”', '*', ' ').TrimEnd('.', '!').Trim();
        if (line.Length == 0 || line.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return null;
        if (line.Length > Item.MaxNameLength)
            line = line[..Item.MaxNameLength].Trim();
        return char.ToUpperInvariant(line[0]) + line[1..];
    }
}

// Claude through the official Anthropic SDK.
internal sealed class ClaudeNamer(string apiKey, string model) : IAiNamer
{
    public async Task<string?> NameAsync(byte[] jpeg, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var parameters = new MessageCreateParams
        {
            Model = model,
            // Room for the model's thinking; the name itself is a few tokens.
            MaxTokens = 4000,
            System = AiNamer.Instructions,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages =
            [
                new UserMessageParam
                {
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg } },
                        new TextBlockParam { Text = "What is this?" },
                    },
                },
            ],
        };

        Message response;
        try
        {
            response = await client.Messages.Create(parameters, cancellationToken: ct);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiException("Claude rejected the API key. Check it in the household's settings.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiException("Claude is rate-limiting the API key. Naming continues later.");
        }
        catch (AnthropicNotFoundException)
        {
            throw new AiException($"The model \"{model}\" wasn't found. Pick another one in the household's settings.");
        }
        catch (AnthropicApiException ex)
        {
            throw new AiException($"The AI request failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
        {
            throw new AiException($"Couldn't reach Claude: {ex.Message}");
        }

        if (response.StopReason?.ToString() == "refusal")
            return null;
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return AiNamer.CleanReply(text);
    }
}

// ChatGPT through the Responses API over plain HTTP, the way Recall calls it.
internal sealed class OpenAiNamer(string apiKey, string model) : IAiNamer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<string?> NameAsync(byte[] jpeg, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = AiNamer.Instructions,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
                        new JsonObject { ["type"] = "input_text", ["text"] = "What is this?" },
                    },
                },
            },
            ["max_output_tokens"] = 4000,
            ["store"] = false,
        };
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
            throw new AiException($"Couldn't reach OpenAI: {ex.Message}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new AiException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "OpenAI rejected the API key. Check it in the household's settings.",
                    System.Net.HttpStatusCode.TooManyRequests => "OpenAI is rate-limiting the API key, or its credit is used up.",
                    System.Net.HttpStatusCode.NotFound => $"The model \"{model}\" wasn't found. Pick another one in the household's settings.",
                    _ => $"The AI request failed ({(int)response.StatusCode}).",
                });
            var root = JsonNode.Parse(text);
            var output = new StringBuilder();
            foreach (var item in root?["output"]?.AsArray() ?? [])
            {
                if (item?["type"]?.GetValue<string>() != "message")
                    continue;
                foreach (var part in item["content"]?.AsArray() ?? [])
                    if (part?["type"]?.GetValue<string>() == "output_text")
                        output.Append(part["text"]?.GetValue<string>());
            }
            return AiNamer.CleanReply(output.ToString());
        }
    }
}
