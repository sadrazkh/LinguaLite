using System.Text.RegularExpressions;

public static partial class FeedbackCardPresenter
{
    public static FeedbackParts Parse(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return new FeedbackParts(string.Empty, string.Empty);

        var normalized = value
            .Replace("→", "->", StringComparison.Ordinal)
            .Replace("=>", "->", StringComparison.Ordinal);

        var labeled = WrongCorrectRegex().Match(normalized);
        if (labeled.Success)
        {
            return new FeedbackParts(Clean(labeled.Groups["wrong"].Value), Clean(labeled.Groups["correct"].Value));
        }

        var arrow = normalized.Split("->", 2, StringSplitOptions.TrimEntries);
        if (arrow.Length == 2)
        {
            return new FeedbackParts(Clean(arrow[0]), Clean(arrow[1]));
        }

        var slash = normalized.Split('/', 2, StringSplitOptions.TrimEntries);
        if (slash.Length == 2)
        {
            return new FeedbackParts(Clean(slash[0]), Clean(slash[1]));
        }

        return new FeedbackParts(Clean(value), string.Empty);
    }

    public static object ToReviewShape(FlashCard card)
    {
        if (card.Type != CardType.Feedback) return card;
        var fromFront = Parse(card.Front);
        var wrong = fromFront.Wrong;
        var correct = string.IsNullOrWhiteSpace(fromFront.Correct) ? card.Answer : fromFront.Correct;

        var front = string.IsNullOrWhiteSpace(wrong)
            ? card.Prompt
            : $"Correct this: {wrong}";
        var backHasCorrect = !string.IsNullOrWhiteSpace(correct)
            && card.Back.Contains(correct, StringComparison.OrdinalIgnoreCase);
        var back = string.Join("\n\n", new[]
            {
                string.IsNullOrWhiteSpace(correct) || backHasCorrect ? string.Empty : $"Correct: {correct}",
                card.Back
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

        return new
        {
            card.Id,
            Front = string.IsNullOrWhiteSpace(front) ? card.Front : front,
            Back = string.IsNullOrWhiteSpace(back) ? card.Back : back,
            card.Example,
            Prompt = string.IsNullOrWhiteSpace(card.Prompt) ? front : card.Prompt,
            Answer = correct,
            card.Notes,
            card.Type,
            card.Box,
            card.TotalReviews,
            card.CorrectReviews,
            card.CreatedAt,
            card.NextReviewAt,
            card.LastReviewedAt,
            Feedback = new { wrong, correct }
        };
    }

    private static string Clean(string value)
    {
        return value
            .Replace("wrong:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("incorrect:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("correct:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("correct this:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("استاد گفت:", "", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '"', '\'', ':', '-');
    }

    [GeneratedRegex(@"wrong\s*:\s*(?<wrong>.+?)\s*(?:->|correct\s*:)\s*(?<correct>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WrongCorrectRegex();
}

public sealed record FeedbackParts(string Wrong, string Correct);
