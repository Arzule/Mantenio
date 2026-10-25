using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Infrastructure.Push;

public enum PushResult
{
    Sent,

    /// <summary>The app was uninstalled or the token expired: forget the device.</summary>
    InvalidToken,

    Failed,
}

public sealed record PushMessage(string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data);

public interface IPushSender
{
    bool IsEnabled { get; }

    Task<PushResult> SendAsync(PushMessage message, CancellationToken cancellationToken);
}

/// <summary>"Push" section. Without a service account, push is off (notices stay in the app only).</summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Firebase service account key: the JSON, or the JSON in base64. Secret (Container Apps / user-secrets).</summary>
    public string ServiceAccountJson { get; set; } = "";

    public string ServiceAccountJsonText =>
        ServiceAccountJson.TrimStart().StartsWith('{') ? ServiceAccountJson : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ServiceAccountJson.Trim()));

    /// <summary>Tests turn the background dispatcher off and dispatch explicitly.</summary>
    public bool BackgroundDispatch { get; set; } = true;
}

/// <summary>Firebase Cloud Messaging HTTP v1 (free, no message limits).</summary>
internal sealed partial class FcmPushSender : IPushSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private readonly HttpClient _http;
    private readonly ITokenAccess _credential;
    private readonly string _projectId;
    private readonly ILogger<FcmPushSender> _logger;

    public FcmPushSender(HttpClient http, IOptions<PushOptions> options, ILogger<FcmPushSender> logger)
    {
        _http = http;
        _logger = logger;
        var json = options.Value.ServiceAccountJsonText;
        using (var document = JsonDocument.Parse(json))
        {
            _projectId = document.RootElement.GetProperty("project_id").GetString()!;
        }

        _credential = CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential().CreateScoped(Scope);
    }

    public bool IsEnabled => true;

    public async Task<PushResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var accessToken = await _credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_projectId}/messages:send")
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token = message.Token,
                    notification = new { title = message.Title, body = message.Body },
                    data = message.Data,
                    android = new { priority = "high", notification = new { channel_id = "notices" } },
                },
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushResult.Sent;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound || error.Contains("UNREGISTERED", StringComparison.Ordinal)
            || (response.StatusCode == HttpStatusCode.BadRequest && error.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
        {
            return PushResult.InvalidToken;
        }

        LogFailed((int)response.StatusCode, error.Length > 300 ? error[..300] : error);
        return PushResult.Failed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FCM answered {Status}: {Error}")]
    private partial void LogFailed(int status, string error);
}

/// <summary>Push not configured (development, demo profile): notices stay in the app.</summary>
internal sealed class DisabledPushSender : IPushSender
{
    public bool IsEnabled => false;

    public Task<PushResult> SendAsync(PushMessage message, CancellationToken cancellationToken) => Task.FromResult(PushResult.Failed);
}

internal static class PushSetup
{
    public static IServiceCollection AddMantenioPush(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PushOptions.SectionName);
        services.AddOptions<PushOptions>().Bind(section);
        if (string.IsNullOrWhiteSpace(section[nameof(PushOptions.ServiceAccountJson)]))
        {
            services.AddSingleton<IPushSender, DisabledPushSender>();
        }
        else
        {
            services.AddHttpClient<IPushSender, FcmPushSender>();
        }

        services.AddScoped<FounderDevices>();
        services.AddSingleton<PushDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<PushDispatcher>());
        return services;
    }
}
