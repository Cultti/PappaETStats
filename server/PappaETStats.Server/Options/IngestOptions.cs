namespace PappaETStats.Server.Options;

public sealed class IngestOptions
{
    public const string SectionName = "Pappa:Ingest";

    public string? Token { get; init; }
}
