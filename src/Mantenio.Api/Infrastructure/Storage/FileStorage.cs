using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Mantenio.Api.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Infrastructure.Storage;

/// <summary>
/// Private object storage for payment-proof images and receipt PDFs (Cloudflare R2 in production, a local folder
/// in development and tests). Files are only read through short-lived URLs (~5 min).
/// </summary>
public interface IFileStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Deletes the file; nothing happens when it does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Uri GetReadUrl(string key, TimeSpan lifetime);
}

public static class StorageKeys
{
    public static string PaymentProof(Guid apartmentId, Guid proofId) => $"payment-proofs/{apartmentId:N}/{proofId:N}.jpg";

    public static string Receipt(string number, bool voided) => $"receipts/{number}{(voided ? "-anulado" : "")}.pdf";

    /// <summary>The founder's push tokens, kept outside the database for the outage alert.</summary>
    public const string FounderDevices = "system/founder-devices.json";
}

/// <summary>"Storage" section. Provider "R2" needs the R2 values; anything else uses the local folder.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "Local";
    public string R2AccountId { get; set; } = "";
    public string R2AccessKeyId { get; set; } = "";
    public string R2SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "";

    /// <summary>Local provider: folder for the files (default ".storage" next to the app).</summary>
    public string LocalPath { get; set; } = ".storage";
}

internal sealed class R2FileStorage : IFileStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public R2FileStorage(IOptions<StorageOptions> options)
    {
        var settings = options.Value;
        _bucket = settings.Bucket;
        _client = new AmazonS3Client(
            new BasicAWSCredentials(settings.R2AccessKeyId, settings.R2SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = $"https://{settings.R2AccountId}.r2.cloudflarestorage.com",
                ForcePathStyle = true,
                AuthenticationRegion = "auto",

                // R2 does not accept every checksum header newer SDKs send by default.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            DisablePayloadSigning = true,
        }, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken);

    public Uri GetReadUrl(string key, TimeSpan lifetime) =>
        new(_client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
        }));

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Development/tests: files in a local folder, read through <c>/api/files/{key}</c> with an HMAC-signed expiry,
/// so links behave like presigned URLs.
/// </summary>
internal sealed class LocalFileStorage(IOptions<StorageOptions> options, IOptions<JwtOptions> jwt, IHttpContextAccessor http, TimeProvider time)
    : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.LocalPath);
    private readonly byte[] _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt.Value.SigningKey), 32,
        info: Encoding.UTF8.GetBytes("mantenio-local-files"));

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    public Uri GetReadUrl(string key, TimeSpan lifetime)
    {
        var expires = time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var request = http.HttpContext?.Request;
        var origin = request is null ? "http://localhost" : $"{request.Scheme}://{request.Host}";
        return new Uri($"{origin}/api/files/{key}?exp={expires}&sig={Sign(key, expires)}");
    }

    public bool IsValid(string key, long expires, string signature) =>
        expires >= time.GetUtcNow().ToUnixTimeSeconds()
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, expires)), Encoding.ASCII.GetBytes(signature));

    private string Sign(string key, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{key}|{expires.ToString(CultureInfo.InvariantCulture)}")));

    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid key.", nameof(key));
        }

        return path;
    }
}

internal static class StorageSetup
{
    public static IServiceCollection AddMantenioStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(StorageOptions.SectionName);
        services.AddOptions<StorageOptions>().Bind(section);
        services.AddHttpContextAccessor();
        if (string.Equals(section[nameof(StorageOptions.Provider)], "R2", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFileStorage, R2FileStorage>();
        }
        else
        {
            services.AddSingleton<LocalFileStorage>();
            services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());
        }

        return services;
    }

    /// <summary>Serves local files behind signed, expiring links (only registered with the local provider).</summary>
    public static IEndpointRouteBuilder MapLocalFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/{**key}", async (string key, long exp, string sig, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var storage = services.GetService<LocalFileStorage>();
            if (storage is null || !storage.IsValid(key, exp, sig))
            {
                return Results.NotFound();
            }

            var bytes = await storage.GetAsync(key, cancellationToken);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, key.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "image/jpeg");
        }).AllowAnonymous().ExcludeFromDescription();
        return app;
    }
}
