using System.Globalization;
using Android.Content;
using Mantenio.UI.Platform;

namespace Mantenio.Mobile;

/// <summary>
/// Downloads the new APK to the cache folder and hands it to the Android package installer, which asks the person to
/// confirm (the first time, also to allow installs from this app). Same signing key, so it installs over this one.
/// </summary>
internal sealed class ApkUpdater : IAppUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public int? InstalledVersionCode =>
        int.TryParse(AppInfo.Current.BuildString, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;

    public async Task InstallAsync(Uri apk, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, "update.apk");
        using (var response = await Http.GetAsync(apk, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        var context = Platform.AppContext;

        // MAUI Essentials registers this FileProvider for the cache folder (as for WhatsApp sharing).
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileProvider", new Java.IO.File(path));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
}
