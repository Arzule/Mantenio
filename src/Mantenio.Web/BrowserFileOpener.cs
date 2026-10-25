using Mantenio.UI.Platform;
using Microsoft.JSInterop;

namespace Mantenio.Web;

internal sealed class BrowserFileOpener(IJSRuntime js) : IFileOpener
{
    public async Task OpenAsync(Uri url) => await js.InvokeVoidAsync("open", url.AbsoluteUri, "_blank", "noopener");

    public async Task SaveAsync(string fileName, string contentType, byte[] content)
    {
        await using var module = await ImportAsync();
        await module.InvokeVoidAsync("downloadFile", fileName, contentType, Convert.ToBase64String(content));
    }

    public async Task ShareToWhatsAppAsync(string fileName, string contentType, byte[] content, string message)
    {
        await using var module = await ImportAsync();
        await module.InvokeVoidAsync("shareToWhatsApp", fileName, contentType, Convert.ToBase64String(content), message);
    }

    private ValueTask<IJSObjectReference> ImportAsync() => js.InvokeAsync<IJSObjectReference>("import", "./_content/Mantenio.UI/mantenio.js");
}
