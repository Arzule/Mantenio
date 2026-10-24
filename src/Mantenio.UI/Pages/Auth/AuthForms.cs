using System.ComponentModel.DataAnnotations;
using Mantenio.Shared.Auth;
using Mantenio.UI.Resources;

namespace Mantenio.UI.Pages.Auth;

// Form models with Spanish validation messages. The API validates the same rules again.

public sealed class LoginForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [EmailAddress(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.InvalidEmail))]
    public string Email { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    public string Password { get; set; } = "";

    public bool KeepSignedIn { get; set; }
}

public sealed class InvitationCodeForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [RegularExpression(ValidationPatterns.SixDigits, ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.CodeFormat))]
    public string Code { get; set; } = "";
}

public sealed class RegisterForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [MaxLength(100)]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [MaxLength(100)]
    public string LastName { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [EmailAddress(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.InvalidEmail))]
    public string Email { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [RegularExpression(PasswordPattern, ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordHint))]
    public string Password { get; set; } = "";

    [Compare(nameof(Password), ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordsDontMatch))]
    public string ConfirmPassword { get; set; } = "";

    public bool KeepSignedIn { get; set; }

    /// <summary>At least 8 characters and one digit, the same rule the API enforces.</summary>
    public const string PasswordPattern = @"^(?=.*\d).{8,}$";
}

public sealed class ForgotPasswordForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [EmailAddress(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.InvalidEmail))]
    public string Email { get; set; } = "";
}

public sealed class ResetPasswordForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [RegularExpression(ValidationPatterns.SixDigits, ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.CodeFormat))]
    public string Code { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [RegularExpression(RegisterForm.PasswordPattern, ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordHint))]
    public string NewPassword { get; set; } = "";

    [Compare(nameof(NewPassword), ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordsDontMatch))]
    public string ConfirmPassword { get; set; } = "";
}

public sealed class ChangePasswordForm
{
    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.Required))]
    [RegularExpression(RegisterForm.PasswordPattern, ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordHint))]
    public string NewPassword { get; set; } = "";

    [Compare(nameof(NewPassword), ErrorMessageResourceType = typeof(UiStrings), ErrorMessageResourceName = nameof(UiStrings.PasswordsDontMatch))]
    public string ConfirmPassword { get; set; } = "";
}
