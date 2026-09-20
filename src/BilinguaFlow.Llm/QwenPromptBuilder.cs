using System.Text;
using BilinguaFlow.Core.Speech;

namespace BilinguaFlow.Llm;

public sealed class QwenPromptBuilder(QwenSettings settings)
{
    public const string SystemPrompt = """
        You are an offline real-time transcription corrector and translator.
        Process speech-recognition text from a live movie or meeting.
        Correct only obvious ASR recognition errors and only when context provides strong evidence.
        Never invent information. Preserve names, numbers, commands, file names, URLs, model names,
        product names, and technical terminology. Preserve AWS, Terraform, ECS, RDS, CloudWatch,
        GitHub Actions, Security Group, SNMP, and Syslog exactly when appropriate.
        Do not summarize, explain, or rewrite creatively. Translate the corrected text into natural
        Simplified Chinese. If incomplete, keep the source mostly unchanged and translate conservatively.
        Return JSON only in exactly this shape:
        {"correctedText":"...","translatedText":"..."}
        No markdown, comments, or additional text. /no_think
        """;

    public string Build(LlmTranslationRequest request)
    {
        var recent = request.RecentUtterances.TakeLast(settings.RecentContextCount).ToArray();
        var languageInstruction = request.SourceLanguage == Core.Models.SourceLanguage.Japanese
            ? "The source language is Japanese. Correct obvious Japanese ASR errors and preserve Japanese names and technical terms."
            : "The source language is English. Correct obvious English ASR errors and preserve technical terms, names, numbers, and commands.";
        var body = new StringBuilder()
            .AppendLine(languageInstruction)
            .AppendLine($"Mode: {request.Mode}")
            .AppendLine($"Source: {request.Source}")
            .AppendLine($"Profile: {request.Profile.Name}")
            .AppendLine($"Profile description: {request.Profile.Description}");
        if (request.Profile.Keywords.Count > 0) body.AppendLine($"Keywords: {string.Join(", ", request.Profile.Keywords)}");
        if (!string.IsNullOrWhiteSpace(request.AdditionalContext)) body.AppendLine($"Additional context: {request.AdditionalContext.Trim()}");
        if (recent.Length > 0)
        {
            body.AppendLine("Previous utterances:");
            for (var i = 0; i < recent.Length; i++) body.AppendLine($"{i + 1}. {recent[i]}");
        }
        body.AppendLine($"Current ASR: {request.OriginalText}").Append("/no_think");
        return $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n{body}<|im_end|>\n<|im_start|>assistant\n";
    }
}
