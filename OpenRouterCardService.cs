using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class OpenRouterCardService(HttpClient httpClient, IConfiguration configuration, IAppStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = AppJsonOptions.CreateCompact();

    public async Task<CreateCardRequest> CompleteAsync(AiCompleteRequest request, string apiKey)
    {
        var options = await OpenRouterOptions.FromAsync(configuration, store);
        var type = request.Type ?? CardType.Word;
        var prompt = type == CardType.Feedback
            ? FeedbackPrompt(request.Text)
            : StandardPrompt(request.Text, type);

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        message.Headers.Add("HTTP-Referer", options.Referer);
        message.Headers.Add("X-OpenRouter-Title", "LinguaLite");
        message.Content = JsonContent.Create(new
        {
            model = options.DefaultModel,
            temperature = 0.2,
            max_tokens = 900,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "Return only valid JSON. No markdown. No extra text." },
                new { role = "user", content = prompt }
            }
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter error: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var jsonText = NormalizeJson(ExtractMessageContent(document.RootElement));
        return JsonSerializer.Deserialize<CreateCardRequest>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("پاسخ مدل خالی بود.");
    }

    private static string StandardPrompt(string text, CardType type) => $"""
        Create a flashcard for a Persian-speaking English learner.
        Return JSON with exactly these keys: front, back, example, prompt, answer, notes, type.
        type must be "{type}" unless another type is clearly better.
        front: exact English item or compact question.
        back: Persian meaning/explanation.
        example: one natural English sentence.
        prompt: one recall question.
        answer: ideal short answer.
        notes: Persian notes about usage, register, collocations and common mistakes.
        Learner input: {text}
        """;

    private static string FeedbackPrompt(string text) => $"""
        The learner writes a mistake, correction, or teacher feedback. Build a feedback flashcard, not a vocabulary card.
        Return JSON with exactly these keys: front, back, example, prompt, answer, notes, type.
        type must be "Feedback".
        front: do NOT reveal the correction. Put only a correction task or the wrong sentence, for example "Correct this: I programmer".
        back: the corrected sentence plus Persian explanation of the exact grammar/vocabulary problem.
        example: a correct natural English example using the fixed pattern.
        prompt: ask the learner to correct the original mistake or explain the rule.
        answer: the corrected sentence and the shortest rule.
        notes: Persian pattern, one similar example, and one warning about common mistakes.
        Learner feedback/mistake: {text}
        """;

    private static string ExtractMessageContent(JsonElement root)
    {
        return root.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            ? content.GetString() ?? "{}"
            : "{}";
    }

    private static string NormalizeJson(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`').Trim();
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                text = text[4..].Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}

public sealed record OpenRouterOptions(string DefaultModel, string Referer)
{
    public static OpenRouterOptions From(IConfiguration config) => new(
        config["OPENROUTER_MODEL"] ?? config["OpenRouter:DefaultModel"] ?? "google/gemma-4-31b-it:free",
        config["OPENROUTER_REFERER"] ?? config["OpenRouter:Referer"] ?? "https://lingualite.local");

    public static async Task<OpenRouterOptions> FromAsync(IConfiguration config, IAppStore store)
    {
        var settings = await store.GetSettingsAsync();
        var fallback = From(config);
        return new OpenRouterOptions(
            string.IsNullOrWhiteSpace(settings.OpenRouterModel) ? fallback.DefaultModel : settings.OpenRouterModel,
            string.IsNullOrWhiteSpace(settings.OpenRouterReferer) ? fallback.Referer : settings.OpenRouterReferer);
    }
}
