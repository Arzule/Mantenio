using System.Net;
using System.Net.Http.Json;
using Mantenio.Client.Auth;

namespace Mantenio.UI.Tests;

/// <summary>Answers API calls with canned responses keyed by path, and records what was sent.</summary>
internal sealed class FakeApi : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    public FakeApi Returns(string path, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[path] = () => new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
        return this;
    }

    public FakeApi Returns(string path, Func<HttpResponseMessage> response)
    {
        _responses[path] = response;
        return this;
    }

    public FakeApi Fails(string path, HttpStatusCode status, string code) =>
        Returns(path, new { status = (int)status, code }, status);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://api.test") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add(path);
        return Task.FromResult(_responses.TryGetValue(path, out var response)
            ? response()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class MemoryTokenStore : ITokenStore
{
    public StoredSession? Session { get; private set; }

    public Task<StoredSession?> LoadAsync() => Task.FromResult(Session);

    public Task SaveAsync(StoredSession session)
    {
        Session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Session = null;
        return Task.CompletedTask;
    }
}
