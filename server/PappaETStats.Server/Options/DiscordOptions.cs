namespace PappaETStats.Server.Options;

public sealed class DiscordOptions
{
    public const string SectionName = "Pappa:Discord";

    /// <summary>Discord OAuth2 application client id. Leave empty to disable Discord login.</summary>
    public string? ClientId { get; init; }

    /// <summary>Discord OAuth2 application client secret.</summary>
    public string? ClientSecret { get; init; }
}
