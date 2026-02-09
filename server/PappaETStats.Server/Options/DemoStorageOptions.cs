namespace PappaETStats.Server.Options;

public sealed class DemoStorageOptions
{
    public const string SectionName = "Pappa:DemoStorage";

    /// <summary>
    /// Root directory where demos are stored. Can be relative to ContentRoot.
    /// Default: App_Data/demos
    /// </summary>
    public string? RootPath { get; init; }

    /// <summary>
    /// Max allowed upload size in bytes. 0 means "use server defaults".
    /// Default: 1 GiB.
    /// </summary>
    public long MaxUploadBytes { get; init; } = 1024L * 1024L * 1024L;

    /// <summary>
    /// If true, requires that the match already exists in DB (stats ingested) before accepting a demo upload.
    /// (Property name kept for backwards compatibility with existing config.)
    /// </summary>
    public bool RequireRoundExists { get; init; } = true;

    /// <summary>
    /// If true, keeps the original demo file after creating the zip.
    /// </summary>
    public bool KeepOriginalAfterZip { get; init; } = true;
}
