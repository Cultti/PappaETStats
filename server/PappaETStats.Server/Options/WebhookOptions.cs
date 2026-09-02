namespace PappaETStats.Server.Options;

public sealed class WebhookOptions
{
    public const string SectionName = "Pappa:Webhook";

    /// <summary>
    /// Full webhook URL to call when a match (round 2) has been ingested.
    /// Example: "http://localhost:8080/webhook/game-completed".
    /// Leave empty to disable.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Optional bearer token for webhook authentication.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Public base URL for the frontend. Used to build the MatchDetails link.
    /// Example: "https://stats.example.com".
    /// </summary>
    public string? FrontendBaseUrl { get; init; }
}
