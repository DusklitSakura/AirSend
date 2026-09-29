namespace AirSend.Core.Updates;

/// <summary>One file attached to a GitHub release.</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

/// <summary>A GitHub release, reduced to what the updater needs.</summary>
public sealed record UpdateRelease(
    string TagName,
    string Title,
    string Notes,
    string PageUrl,
    IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>Parsed tag. Tags that are not a version compare as "nothing newer".</summary>
    public UpdateVersion Version => UpdateVersion.TryParse(TagName, out UpdateVersion parsed)
        ? parsed
        : new UpdateVersion(0, 0, 0);
}

/// <summary>
/// Outcome of one update check: the release that is available, or the error that
/// stopped us from finding out.
/// </summary>
public sealed record UpdateCheckResult(UpdateRelease? Release, string? Error, bool Manual)
{
    public bool UpToDate => Release is null && Error is null;
}
