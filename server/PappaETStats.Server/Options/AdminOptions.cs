namespace PappaETStats.Server.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Pappa:Admin";

    public string? Token { get; init; }
}
