using Mantenio.UI.Platform;

namespace Mantenio.UI.Tests;

/// <summary>Records what the UI asked to open or save.</summary>
internal sealed class FakeFileOpener : IFileOpener
{
    public List<Uri> Opened { get; } = [];

    public List<string> Saved { get; } = [];

    public List<(string FileName, string Message)> SharedToWhatsApp { get; } = [];

    public Task OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }

    public Task SaveAsync(string fileName, string contentType, byte[] content)
    {
        Saved.Add(fileName);
        return Task.CompletedTask;
    }

    public Task ShareToWhatsAppAsync(string fileName, string contentType, byte[] content, string message)
    {
        SharedToWhatsApp.Add((fileName, message));
        return Task.CompletedTask;
    }
}
