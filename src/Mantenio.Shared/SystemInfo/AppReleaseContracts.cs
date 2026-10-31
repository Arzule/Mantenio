namespace Mantenio.Shared.SystemInfo;

/// <summary>
/// The latest Android version. An app older than <paramref name="MinVersionCode"/> must update before going on; one
/// older than <paramref name="VersionCode"/> may. <paramref name="DownloadPath"/> is relative to the API address.
/// </summary>
public sealed record AndroidRelease(int VersionCode, string VersionName, int MinVersionCode, string DownloadPath);
