using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class OpenRouterCardService(HttpClient httpClient, IConfiguration configuration, IAppStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = AppJsonOptions.CreateCompact();

    public async Task<CreateCardRequest> CompleteAsync(AiCompleteRequest request, string apiKey)
    {
        var type = request.Type ?? CardType.Word;
        var prompt = type == CardType.Feedback
            ? FeedbackPrompt(request.Text, Level(request.LanguageLevel))
            : StandardPrompt(request.Text, type, Level(request.LanguageLevel));
        var jsonText = await RequestJsonAsync(prompt, apiKey, 0.2, 900);
        return JsonSerializer.Deserialize<CreateCardRequest>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("پاسخ مدل برای کارت خالی بود.");
    }

    public async Task<DictionaryResult> LookupDictionaryAsync(DictionaryRequest request, string apiKey)
    {
        var jsonText = await RequestJsonAsync(DictionaryPrompt(request.Text, Level(request.LanguageLevel)), apiKey, 0.15, 900);
        return JsonSerializer.Deserialize<DictionaryResult>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("پاسخ مدل برای دیکشنری خالی بود.");
    }

    public async Task<List<PackageCardRequest>> CompletePackageCardsAsync(PackageCardsGenerateRequest request, string apiKey)
    {
        var words = (request.Words ?? [])
            .Select(word => word.Trim())
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        if (words.Count == 0)
        {
            return [];
        }

        var generated = new List<PackageCardRequest>();
        foreach (var chunk in words.Chunk(20))
        {
            var chunkWords = chunk.ToList();
            var maxTokens = Math.Clamp(chunkWords.Count * 280, 1200, 6000);
            var jsonText = await RequestJsonAsync(PackageCardsPrompt(chunkWords, request.Type, Level(request.LanguageLevel)), apiKey, 0.18, maxTokens);
            var result = JsonSerializer.Deserialize<PackageCardsGenerateResult>(jsonText, JsonOptions)
                ?? throw new InvalidOperationException("پاسخ مدل برای کارت‌های بسته خالی بود.");
            generated.AddRange(result.Cards);
        }

        return generated
            .Where(card => !string.IsNullOrWhiteSpace(card.Front) && !string.IsNullOrWhiteSpace(card.Back))
            .Select(card => card with
            {
                Id = string.IsNullOrWhiteSpace(card.Id) ? Slug(card.Front) : Slug(card.Id),
                Front = card.Front.Trim(),
                Back = card.Back.Trim(),
                Example = card.Example?.Trim() ?? string.Empty,
                Prompt = card.Prompt?.Trim() ?? string.Empty,
                Answer = card.Answer?.Trim() ?? string.Empty,
                Notes = card.Notes?.Trim() ?? string.Empty,
                Type = card.Type
            })
            .ToList();
    }

    public async Task<CorrectionResult> CorrectTextAsync(CorrectionRequest request, string apiKey)
    {
        var jsonText = await RequestJsonAsync(CorrectionPrompt(request.Text, Level(request.LanguageLevel)), apiKey, 0.15, 1200);
        return JsonSerializer.Deserialize<CorrectionResult>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("پاسخ مدل برای اصلاح متن خالی بود.");
    }

    private async Task<string> RequestJsonAsync(string prompt, string apiKey, double temperature, int maxTokens)
    {
        var options = await OpenRouterOptions.FromAsync(configuration, store);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        message.Headers.Add("HTTP-Referer", options.Referer);
        message.Headers.Add("X-OpenRouter-Title", "LinguaLite");
        message.Content = JsonContent.Create(new
        {
            model = options.DefaultModel,
            temperature,
            max_tokens = maxTokens,
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
        return NormalizeJson(ExtractMessageContent(document.RootElement));
    }

    private static string StandardPrompt(string text, CardType type, string level) => $"""
        Create a clean Leitner flashcard for a Persian-speaking English learner.
        Learner CEFR level: {level}. Match English examples, prompts, and vocabulary difficulty to this level.
        Return JSON with exactly these keys: front, back, example, prompt, answer, notes, type.
        type must be "{type}" unless another type is clearly better.
        front: exact English item or compact question. Do not make it too long.
        back: Persian meaning or explanation, clear and learner-friendly.
        example: one natural English sentence.
        prompt: one active recall question.
        answer: ideal short answer.
        notes: Persian notes about usage, register, collocations, and common mistakes.
        Learner input: {text}
        """;

    private static string PackageCardsPrompt(List<string> words, CardType type, string level) => $"""
        Build package-ready Leitner flashcards for Persian-speaking English learners.
        Learner CEFR level for examples and prompts: {level}.
        Return only one JSON object with exactly one key: cards.
        cards must be an array. Create exactly one card for each input item, in the same order.
        Each card must have exactly these keys: id, front, back, example, prompt, answer, notes, type.
        type must be "{type}" unless the item is clearly not vocabulary.
        id: URL-safe lowercase English slug based on the front.
        front: the exact English word or phrase, short and clean.
        back: concise Persian meaning with the most useful sense first.
        example: one natural English sentence, not generic, matched to the learner level.
        prompt: one active-recall question that does not reveal the answer.
        answer: short ideal answer, usually Persian meaning or corrected target.
        notes: Persian usage note with collocation, register, and one common mistake.
        Avoid duplicate cards. Avoid repeating the same example pattern.
        Input items:
        {string.Join("\n", words.Select((word, index) => $"{index + 1}. {word}"))}
        """;

    private static string FeedbackPrompt(string text, string level) => $"""
        The learner writes a real mistake, correction, or teacher feedback. Build a feedback flashcard, not a vocabulary card.
        Learner CEFR level: {level}. Explain the rule in Persian, but keep English examples suitable for this level.
        Return JSON with exactly these keys: front, back, example, prompt, answer, notes, type.
        type must be "Feedback".
        front: do NOT reveal the correction. Put only a correction task or the wrong sentence, for example "Correct this: I programmer".
        back: corrected sentence plus Persian explanation of the exact grammar/vocabulary problem.
        example: one correct natural English example using the fixed pattern.
        prompt: ask the learner to correct the original mistake or explain the rule.
        answer: corrected sentence and the shortest rule.
        notes: Persian pattern, one similar example, and one warning about common mistakes.
        Learner feedback or mistake: {text}
        """;

    private static string DictionaryPrompt(string text, string level) => $"""
        You are a compact English dictionary for a Persian-speaking learner.
        Learner CEFR level: {level}. Make definitions and examples suitable for this level; avoid repeating the same generic examples.
        Return only JSON with exactly these keys:
        word, pronunciation, partOfSpeech, persianMeaning, englishDefinition, synonyms, examples, notes.
        Rules:
        - word: the best normalized English word or phrase from the learner input.
        - pronunciation: simple IPA or easy pronunciation. Empty string if unsure.
        - persianMeaning: concise Persian meaning, with the most useful senses first.
        - englishDefinition: simple English definition at B1-B2 level.
        - synonyms: 3 to 5 short English synonyms or related words.
        - examples: 2 natural English sentences, useful for daily conversation.
        - notes: Persian usage note, collocations, register, and one common mistake.
        Learner input: {text}
        """;

    private static string CorrectionPrompt(string text, string level) => $"""
        You are an English writing and speaking coach for a Persian-speaking learner.
        Learner CEFR level: {level}. Keep explanations understandable at this level and suggest alternatives that slightly stretch the learner.
        Analyze the learner text and return only JSON with exactly these keys:
        original, corrected, persianTranslation, overallNote, issues, betterAlternatives.
        issues must be an array of objects with exactly: original, corrected, reason, severity.
        Rules:
        - corrected: one natural corrected version, not over-formal unless the original is formal.
        - persianTranslation: Persian translation of the corrected sentence.
        - overallNote: short Persian explanation of the main pattern.
        - issues: each issue should isolate one grammar, word choice, article, tense, or word-order problem.
        - severity: one of "low", "medium", "high".
        - betterAlternatives: 2 or 3 natural alternative English sentences.
        Learner text: {text}
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

    private static string Level(string? value)
    {
        var normalized = (value ?? "B1").Trim().ToUpperInvariant();
        return normalized is "A1" or "A2" or "B1" or "B2" or "C1" or "C2" ? normalized : "B1";
    }

    private static string Slug(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }

        return cleaned.Trim('-');
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
