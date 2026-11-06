using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Trovami.Core.Models;
using Trovami.Core.Services;

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
        new("gpt-6.1-terra", "GPT-6.1 Terra", "Better at visual detail, at about 10 times the price."),
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

// What the AI saw in a photo. Name is null when it can't tell what the thing is; the keywords may
// still say something (a color, a material).
public sealed record AiDescription(string? Name, IReadOnlyList<string> Keywords);

// Names one item from its photo and gives words to find it by. known: the household's keywords so
// far, most used first, for the AI to reuse.
public interface IAiNamer
{
    Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, AiLanguage language, CancellationToken ct);

    // A question without a photo; the answer as text. Throws AiException when there's no answer.
    Task<string> AskAsync(string instructions, string text, CancellationToken ct);
}

public static class AiNamer
{
    internal static string Instructions(AiLanguage language)
    {
        var (lang, unsure) = (language.Name, language.Unsure);
        var text =
            "The photo shows one thing someone is putting away in their home inventory, so they can find it again later. " +
            $"Reply with a short, plain name for it in {lang}, 1 to 5 words, the way a person would label a box: " +
            "for example \"Cordless drill\", \"Christmas lights\", \"Passport\", \"Winter boots\". " +
            "Name the main thing in the photo; mention the brand only when it helps to tell it apart. " +
            "Put the name alone on the first line, no punctuation at the end; if you can't tell what it is, write exactly UNKNOWN there. " +
            $"If you have a guess but aren't fairly sure what it is, write your best guess as the name and include the keyword \"{unsure}\". " +
            $"Never put the word {unsure} in the name itself: that word goes only in the keywords, and for nothing else. " +
            "On the second line write \"Keywords:\" and up to 5 lowercase words or short phrases, separated by commas, " +
            "that someone might search for: its color, material, kind of thing and what it's for, and the brand if it's visible. " +
            "For example: \"Keywords: blue, power tool, bosch\" for a cordless drill. Nothing else. " +
            "Be choosy: a keyword earns its place only if someone would search for it to find this thing. " +
            "The number is a limit, not a goal; fewer is better than filler, and none is fine. " +
            "Leave out words already in the name (the name is searched too), words that fit anything in a home " +
            "(such as item, thing, object, household, storage or useful), and anything the photo doesn't show. " +
            "Clothes, shoes, bags and other accessories need more, because people own many similar pieces. " +
            "Their name has up to 6 words and says what sets this piece apart: its color, pattern, cut or style, and type, " +
            "for example \"Red cable-knit crew-neck sweater\" or \"Navy striped linen midi dress\", never just \"Red pullover\". " +
            "Their keywords go up to 10, picked the same way: the type and broader words for it (such as knitwear or top), colors, pattern, " +
            "fabric, neckline, sleeves, fit or length, season and occasion, and the brand and size if a label shows them. " +
            "Name a fabric only when the photo or a label makes it fairly clear. " +
            $"If the message lists keywords the household already uses, take those that are in {lang} wherever one fits, word for word, " +
            $"and only add a new keyword when none of them says it; new keywords are in {lang}. " +
            $"Leave out listed keywords in another language; brand names belong to no language. " +
            $"The name is always in {lang}, whatever language the listed keywords or any text on the thing are in.";
        if (language != AiLanguages.English)
            text += $" The examples above are in English, but all you write is in {lang}, except the word UNKNOWN and the label \"Keywords:\", which stay as they are.";
        return language.WritingRule is { } rule ? $"{text} {rule}" : text;
    }

    // The household's keywords that go along with the photo, so the AI reuses them; the rarely used
    // ones past this aren't seen and get near-doubles. More would cost tokens on every photo.
    public const int MaxKnownKeywords = 500;

    // For a batch of translations, with room for the model's thinking.
    public const int MaxAnswerTokens = 12000;

    // On an item when the AI isn't sure what it is, so such items can be found and checked.
    // This is the English word; a household's own is in AiLanguages.
    public const string UnsureKeyword = "unsure";

    // The text sent with the photo.
    internal static string Question(IReadOnlyList<string> known) => known.Count == 0
        ? "What is this?"
        : "What is this? Keywords this household already uses: " + string.Join(", ", known.Take(MaxKnownKeywords));

    public static IAiNamer Create(AiProvider provider, string apiKey, string model) => provider switch
    {
        AiProvider.Claude => new ClaudeNamer(apiKey, model),
        AiProvider.OpenAi => new OpenAiNamer(apiKey, model),
        _ => throw new AiException("AI naming is off for this household."),
    };

    // "Unsure lava lamp" or "Lava lamp (unsure)": the AI does that despite being told not to.
    private static Regex UnsureInName(string word) =>
        new($@"^\W*{Regex.Escape(word)}\b\W*|\W*\b{Regex.Escape(word)}\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The reply's name line and "Keywords:" line, in whichever order they came.
    public static AiDescription ParseReply(string? reply, AiLanguage? language = null)
    {
        language ??= AiLanguages.English;
        var lines = AiLanguages.WithoutEszett(reply ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim('*', ' ')).ToList();
        var keywordLine = lines.FirstOrDefault(l => l.StartsWith("Keywords:", StringComparison.OrdinalIgnoreCase));
        var nameLine = lines.FirstOrDefault(l => l != keywordLine) ?? "";
        var withoutUnsure = UnsureInName(language.Unsure).Replace(nameLine, "");
        var name = CleanReply(withoutUnsure);
        IEnumerable<string> keywords = keywordLine is null ? [] : keywordLine["Keywords:".Length..].Split(',');
        // A reply without a usable name means it couldn't tell.
        if ((name is null && lines.Count > 0) || withoutUnsure != nameLine)
            keywords = keywords.Prepend(language.Unsure);
        // "New" from the AI would describe the thing's looks; the keyword means newly added.
        return new AiDescription(name, ItemKeywords.Clean(keywords).Where(k => k != language.New).ToList());
    }

    // A name line without quotes or a closing period; null for UNKNOWN or nothing usable.
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
    public async Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, AiLanguage language, CancellationToken ct)
    {
        var text = await SendAsync(AiNamer.Instructions(language), 4000,
            [
                new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg } },
                new TextBlockParam { Text = AiNamer.Question(known) },
            ], ct);
        return text is null ? new AiDescription(null, []) : AiNamer.ParseReply(text, language);
    }

    public async Task<string> AskAsync(string instructions, string text, CancellationToken ct) =>
        await SendAsync(instructions, AiNamer.MaxAnswerTokens, [new TextBlockParam { Text = text }], ct)
            ?? throw new AiException("Claude declined to answer.");

    // Null when the model refuses. maxTokens leaves room for the model's thinking.
    private async Task<string?> SendAsync(string system, long maxTokens, List<ContentBlockParam> content, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = system,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new UserMessageParam { Content = content }],
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
        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }
}

// ChatGPT through the Responses API over plain HTTP, the way Recall calls it.
internal sealed class OpenAiNamer(string apiKey, string model) : IAiNamer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, AiLanguage language, CancellationToken ct)
    {
        var text = await SendAsync(AiNamer.Instructions(language), 4000, new JsonArray
        {
            new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
            new JsonObject { ["type"] = "input_text", ["text"] = AiNamer.Question(known) },
        }, ct);
        return AiNamer.ParseReply(text, language);
    }

    public Task<string> AskAsync(string instructions, string text, CancellationToken ct) =>
        SendAsync(instructions, AiNamer.MaxAnswerTokens, new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = text } }, ct);

    private async Task<string> SendAsync(string instructions, int maxTokens, JsonArray content, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["max_output_tokens"] = maxTokens,
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
            return output.ToString();
        }
    }
}
