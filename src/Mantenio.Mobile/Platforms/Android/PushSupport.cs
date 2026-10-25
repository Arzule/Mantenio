using Android.App;
using Android.Content;
using Android.Gms.Tasks;
using AndroidX.Core.App;
using Firebase;
using Firebase.Messaging;
using Mantenio.Client;
using Mantenio.Shared.Condos;
using Mantenio.UI.Platform;
using Microsoft.Extensions.Logging;

namespace Mantenio.Mobile;

/// <summary>Firebase Cloud Messaging: started from the condo profile (there is no google-services.json).</summary>
internal static class PushSupport
{
    public const string ChannelId = "notices";
    public const string TokenKey = "push_token";

    public static bool IsAvailable => CondoProfile.Current is { Firebase: not null, Modules.PushNotifications: true };

    /// <summary>Called when the process starts (also when a push wakes up a closed app).</summary>
    public static void Initialize(Context context)
    {
        if (!IsAvailable)
        {
            return;
        }

        var settings = CondoProfile.Current.Firebase!;
        if (FirebaseApp.GetApps(context).Count == 0)
        {
            FirebaseApp.InitializeApp(context, new FirebaseOptions.Builder()
                .SetApplicationId(settings.ApplicationId)
                .SetApiKey(settings.ApiKey)
                .SetProjectId(settings.ProjectId)
                .SetGcmSenderId(settings.SenderId)
                .Build());
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(ChannelId, Mantenio.UI.Resources.UiStrings.PushChannelName, NotificationImportance.High)
            {
                Description = Mantenio.UI.Resources.UiStrings.PushChannelDescription,
            };
            ((NotificationManager)context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
        }
    }

    public static System.Threading.Tasks.Task<string> GetTokenAsync()
    {
        var completion = new TaskCompletionSource<string>();
        // The .NET binding flags getToken() as deprecated, but it is Firebase's current API for the registration token.
#pragma warning disable CS0618
        FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(completion));
#pragma warning restore CS0618
        return completion.Task;
    }

    private sealed class TokenListener(TaskCompletionSource<string> completion) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(Android.Gms.Tasks.Task task)
        {
            if (task.IsSuccessful)
            {
                completion.TrySetResult(task.Result!.ToString()!);
            }
            else
            {
                completion.TrySetException(task.Exception ?? new Java.Lang.Exception("Firebase token unavailable"));
            }
        }
    }
}

/// <summary>Registers this phone's token after sign-in (asks for the notifications permission on Android 13+).</summary>
internal sealed partial class AndroidPushRegistration(MantenioApi api, ILogger<AndroidPushRegistration> logger) : IPushRegistration
{
    public async System.Threading.Tasks.Task RegisterAsync()
    {
        if (!PushSupport.IsAvailable)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>);
                if (status != PermissionStatus.Granted)
                {
                    return;
                }
            }

            var token = await PushSupport.GetTokenAsync();
            Preferences.Default.Set(PushSupport.TokenKey, token);
            await api.RegisterDeviceAsync(token, "android");
        }
        catch (Exception ex)
        {
            // Push is a convenience: the notices are always in the app.
            LogRegisterFailed(ex);
        }
    }

    public async System.Threading.Tasks.Task UnregisterAsync()
    {
        var token = Preferences.Default.Get<string?>(PushSupport.TokenKey, null);
        if (token is null)
        {
            return;
        }

        try
        {
            await api.UnregisterDeviceAsync(token);
        }
        catch (ApiException)
        {
            // Offline: the server forgets the token when FCM reports it invalid.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push registration failed")]
    private partial void LogRegisterFailed(Exception exception);
}

/// <summary>
/// With the app closed Android shows the notification itself; with the app open FCM hands it here, so it is shown
/// the same way.
/// </summary>
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class MantenioMessagingService : FirebaseMessagingService
{
    public override void OnMessageReceived(RemoteMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var notification = message.GetNotification();
        if (notification is null)
        {
            return;
        }

        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var pending = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = new NotificationCompat.Builder(this, PushSupport.ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.icon);
        builder.SetContentTitle(notification.Title);
        builder.SetContentText(notification.Body);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Body));
        builder.SetAutoCancel(true);
        builder.SetContentIntent(pending);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
        {
            return;
        }

        NotificationManagerCompat.From(this)!.Notify(Environment.TickCount, builder.Build()!);
    }

    /// <summary>A new token is registered at the next app start (the user may be signed out now).</summary>
    [Obsolete("Mirrors the binding, which marks the callback deprecated although Firebase still calls it.")]
    public override void OnNewToken(string token) => Preferences.Default.Remove(PushSupport.TokenKey);
}
