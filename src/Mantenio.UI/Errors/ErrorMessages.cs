using Mantenio.Shared.Errors;
using Mantenio.UI.Resources;

namespace Mantenio.UI.Errors;

/// <summary>Translates API error codes (<see cref="ErrorCodes"/>) into Spanish UI messages.</summary>
public static class ErrorMessages
{
    public static string For(string? code) =>
        (code is null ? null : UiStrings.ResourceManager.GetString("Error_" + code, UiStrings.Culture))
        ?? UiStrings.Error_internal_error;
}
