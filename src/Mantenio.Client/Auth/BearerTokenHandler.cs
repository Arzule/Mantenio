using System.Net;
using System.Net.Http.Headers;

namespace Mantenio.Client.Auth;

/// <summary>Adds the access token to every call; on a 401 it renews the token once and retries.</summary>
public sealed class BearerTokenHandler(AuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await session.GetAccessTokenAsync();
        Authorize(request, token);
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null || request.Content is StreamContent)
        {
            return response;
        }

        var renewed = await session.GetAccessTokenAsync(forceRefresh: true);
        if (renewed is null || renewed == token)
        {
            return response;
        }

        response.Dispose();
        Authorize(request, renewed);
        return await base.SendAsync(request, cancellationToken);
    }

    private static void Authorize(HttpRequestMessage request, string? token) =>
        request.Headers.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
}
