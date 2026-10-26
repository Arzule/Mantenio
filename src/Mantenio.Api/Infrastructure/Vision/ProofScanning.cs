using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Infrastructure.Vision;

/// <summary>Reads the text of a payment voucher image (OCR). The lines go to <see cref="Domain.Ledger.ProofTextReader"/>.</summary>
public interface IProofScanner
{
    bool IsEnabled { get; }

    /// <summary>The recognized lines, top to bottom; null when the service failed (the form is simply left as it is).</summary>
    Task<IReadOnlyList<string>?> ReadLinesAsync(byte[] jpeg, CancellationToken cancellationToken);
}

/// <summary>"Vision" section: Azure AI Vision (free F0 tier: 5,000 images a month, 20 a minute).</summary>
public sealed class VisionOptions
{
    public const string SectionName = "Vision";

    /// <summary>e.g. https://canadacentral.api.cognitive.microsoft.com/</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Secret (Container Apps / user-secrets).</summary>
    public string Key { get; set; } = "";
}

/// <summary>Azure AI Vision Image Analysis 4.0, "read" feature.</summary>
internal sealed partial class AzureVisionProofScanner(HttpClient http, IOptions<VisionOptions> options, ILogger<AzureVisionProofScanner> logger)
    : IProofScanner
{
    private readonly VisionOptions _options = options.Value;

    public bool IsEnabled => true;

    public async Task<IReadOnlyList<string>?> ReadLinesAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(_options.Endpoint), "computervision/imageanalysis:analyze?api-version=2024-02-01&features=read");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(jpeg) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.Key);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed((int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var lines = new List<string>();
            if (document.RootElement.TryGetProperty("readResult", out var read) && read.TryGetProperty("blocks", out var blocks))
            {
                foreach (var block in blocks.EnumerateArray())
                {
                    lines.AddRange(block.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString() ?? ""));
                }
            }

            return lines;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogError(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure AI Vision answered {Status}")]
    private partial void LogFailed(int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure AI Vision call failed")]
    private partial void LogError(Exception exception);
}

/// <summary>Not configured (development, demo profile): the resident fills the form by hand.</summary>
internal sealed class DisabledProofScanner : IProofScanner
{
    public bool IsEnabled => false;

    public Task<IReadOnlyList<string>?> ReadLinesAsync(byte[] jpeg, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}

internal static class VisionSetup
{
    public static IServiceCollection AddMantenioVision(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(VisionOptions.SectionName);
        services.AddOptions<VisionOptions>().Bind(section);
        if (string.IsNullOrWhiteSpace(section[nameof(VisionOptions.Endpoint)]) || string.IsNullOrWhiteSpace(section[nameof(VisionOptions.Key)]))
        {
            services.AddSingleton<IProofScanner, DisabledProofScanner>();
        }
        else
        {
            services.AddHttpClient<IProofScanner, AzureVisionProofScanner>(client => client.Timeout = TimeSpan.FromSeconds(20));
        }

        return services;
    }
}
