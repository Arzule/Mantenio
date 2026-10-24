using Mantenio.Client;
using Mantenio.UI.Errors;
using Microsoft.AspNetCore.Components;

namespace Mantenio.UI.Components;

/// <summary>Pages and components that call the API: one call at a time, errors shown in Spanish.</summary>
public abstract class BusyComponentBase : ComponentBase
{
    protected bool Busy { get; private set; }

    /// <summary>Spanish message of the last failed call, or null.</summary>
    protected string? Error { get; set; }

    /// <summary>Runs an API call, ignoring double clicks while one is running. Returns false when it failed.</summary>
    protected async Task<bool> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Busy)
        {
            return false;
        }

        Busy = true;
        Error = null;
        try
        {
            await action();
            return true;
        }
        catch (ApiException ex)
        {
            Error = ErrorMessages.For(ex.Code);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
