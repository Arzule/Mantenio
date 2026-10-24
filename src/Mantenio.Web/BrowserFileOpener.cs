using Mantenio.UI.Platform;
using Microsoft.JSInterop;

namespace Mantenio.Web;

internal sealed class BrowserFileOpener(IJSRuntime js) : IFileOpener
{
    public async Task OpenAsync(Uri url) => await js.InvokeVoidAsync("open", url.AbsoluteUri, "_blank", "noopener");
}
