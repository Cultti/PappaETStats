namespace PappaETStats.Server.Options;

public sealed class DbOptions
{
    public const string SectionName = "Pappa:Db";

    public string Provider { get; init; } = "sqlite";

    /// <summary>
    /// Provider-specific connection string.
    /// For sqlite this can be e.g. "Data Source=App_Data/pappastats.db".
    /// </summary>
    public string? ConnectionString { get; init; }
}
