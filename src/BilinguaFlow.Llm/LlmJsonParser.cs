using System.Text.Json;

namespace BilinguaFlow.Llm;

public sealed record ParsedTranslation(string CorrectedText, string TranslatedText);

public static class LlmJsonParser
{
    public static bool TryParse(string output, out ParsedTranslation? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(output)) return false;
        var cleaned = output.Trim().Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var document = JsonDocument.Parse(cleaned[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("correctedText", out var corrected) ||
                !document.RootElement.TryGetProperty("translatedText", out var translated)) return false;
            var correctedText = corrected.GetString();
            var translatedText = translated.GetString();
            if (string.IsNullOrWhiteSpace(correctedText) || translatedText is null) return false;
            result = new ParsedTranslation(correctedText, translatedText);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
