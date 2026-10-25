using Android.Content;

namespace Mantenio.Mobile;

/// <summary>Opens WhatsApp (or WhatsApp Business) with a file attached; the admin then picks the resident.</summary>
internal static class WhatsAppShare
{
    private static readonly string[] Packages = ["com.whatsapp", "com.whatsapp.w4b"];

    public static bool TryOpen(string path, string contentType, string message)
    {
        var context = Platform.AppContext;

        // MAUI Essentials registers this FileProvider (it shares files from the cache folder).
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileProvider", new Java.IO.File(path));
        foreach (var package in Packages)
        {
            var intent = new Intent(Intent.ActionSend);
            intent.SetType(contentType);
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.PutExtra(Intent.ExtraText, message);
            intent.SetPackage(package);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            if (intent.ResolveActivity(context.PackageManager!) is not null)
            {
                context.StartActivity(intent);
                return true;
            }
        }

        return false;
    }
}
