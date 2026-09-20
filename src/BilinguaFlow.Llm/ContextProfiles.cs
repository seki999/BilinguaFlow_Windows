using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Llm;

public static class ContextProfiles
{
    private static readonly string[] ItKeywords =
    [
        "AWS", "Azure", "Terraform", "ECS", "RDS", "CloudWatch", "GitHub Actions",
        "Security Group", "SNMP", "Syslog", "API", "Java", "Python", "Docker",
        "Kubernetes", "CI/CD", "Git", "Jira"
    ];

    public static ContextProfile GeneralMovie { get; } = new(
        "General Movie", SourceLanguage.Japanese,
        "Movie dialogue. Preserve names and natural dialogue. Do not rewrite creatively.", []);

    public static ContextProfile JapaneseItMeeting { get; } = new(
        "Japanese IT Meeting", SourceLanguage.Japanese,
        "Japanese IT infrastructure and software development meeting.", ItKeywords);

    public static ContextProfile EnglishItMeeting { get; } = new(
        "English IT Meeting", SourceLanguage.English,
        "English IT infrastructure and software development meeting.", ItKeywords);

    public static IReadOnlyList<ContextProfile> All { get; } =
        [GeneralMovie, JapaneseItMeeting, EnglishItMeeting];

    public static ContextProfile DefaultFor(CaptureMode mode, SourceLanguage language) => mode == CaptureMode.Movie
        ? GeneralMovie with { Language = language }
        : language == SourceLanguage.Japanese ? JapaneseItMeeting : EnglishItMeeting;
}
