using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mantenio.Shared.Errors;

namespace Mantenio.Client;

/// <summary>An API call that failed. <see cref="Code"/> is an <see cref="ErrorCodes"/> value the UI turns into Spanish.</summary>
public sealed class ApiException : Exception
{
    /// <summary>Client-side only: the request never reached the API (no connection, timeout).</summary>
    public const string NetworkUnavailable = "network_unavailable";

    public ApiException(string code, HttpStatusCode? status = null, Exception? inner = null)
        : base($"API error '{code}'", inner)
    {
        Code = code;
        Status = status;
    }

    public ApiException() : this(ErrorCodes.InternalError)
    {
    }

    public ApiException(string message) : this(ErrorCodes.InternalError, null, new InvalidOperationException(message))
    {
    }

    public ApiException(string message, Exception innerException) : this(ErrorCodes.InternalError, null, innerException)
    {
        ArgumentNullException.ThrowIfNull(message);
    }

    public string Code { get; }
    public HttpStatusCode? Status { get; }
}

internal static class ApiResponses
{
    /// <summary>Returns the body as <typeparamref name="T"/>, or throws <see cref="ApiException"/> with the ProblemDetails code.</summary>
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        await response.EnsureSuccessAsync(cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new ApiException(ErrorCodes.InternalError, response.StatusCode);
    }

    public static async Task EnsureSuccessAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
            HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
            HttpStatusCode.NotFound => ErrorCodes.NotFound,
            HttpStatusCode.TooManyRequests => ErrorCodes.TooManyRequests,
            _ => ErrorCodes.InternalError,
        };

        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("code", out var codeElement)
                && codeElement.GetString() is { Length: > 0 } problemCode)
            {
                code = problemCode;
            }
        }
        catch (JsonException)
        {
            // Not ProblemDetails (proxy error page, empty body): keep the status-based code.
        }

        throw new ApiException(code, response.StatusCode);
    }

    /// <summary>Runs the request and maps connection failures to <see cref="ApiException.NetworkUnavailable"/>.</summary>
    public static async Task<HttpResponseMessage> SendSafeAsync(this Task<HttpResponseMessage> request)
    {
        try
        {
            return await request;
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(ApiException.NetworkUnavailable, null, ex);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new ApiException(ApiException.NetworkUnavailable, null, ex);
        }
    }
}
