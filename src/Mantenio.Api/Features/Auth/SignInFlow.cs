using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Identity;

namespace Mantenio.Api.Features.Auth;

/// <summary>
/// What happens once the password (or invitation) step succeeded: confirm the email first if needed,
/// then the admin second factor, then the session.
/// </summary>
internal sealed class SignInFlow(UserManager<AppUser> users, SessionService sessions, TokenService tokens, AccountEmails emails)
{
    public async Task<IResult> ContinueAsync(AppUser user, bool keepSignedIn, CancellationToken cancellationToken)
    {
        if (!user.EmailConfirmed)
        {
            return await SendCodeAsync(user, ChallengePurpose.ConfirmEmail, keepSignedIn, cancellationToken);
        }

        if (SessionPolicy.RequiresTwoFactor(user.Role))
        {
            return await SendCodeAsync(user, ChallengePurpose.TwoFactor, keepSignedIn, cancellationToken);
        }

        return TypedResults.Ok(new SignInResponse(SignInStep.SignedIn, await sessions.StartAsync(user, keepSignedIn, cancellationToken)));
    }

    /// <summary>Emails a fresh code and returns the challenge the client sends back with it.</summary>
    public async Task<IResult> SendCodeAsync(AppUser user, ChallengePurpose purpose, bool keepSignedIn, CancellationToken cancellationToken)
    {
        bool sent;
        SignInStep step;
        if (purpose == ChallengePurpose.ConfirmEmail)
        {
            var code = await users.GenerateEmailConfirmationTokenAsync(user);
            sent = await emails.SendEmailConfirmationCodeAsync(user, code, cancellationToken);
            step = SignInStep.EmailConfirmationRequired;
        }
        else
        {
            var code = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
            sent = await emails.SendTwoFactorCodeAsync(user, code, cancellationToken);
            step = SignInStep.TwoFactorRequired;
        }

        if (!sent)
        {
            return ApiProblems.Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.EmailUnavailable);
        }

        return TypedResults.Ok(new SignInResponse(
            step,
            ChallengeToken: tokens.CreateChallenge(user, purpose, keepSignedIn),
            MaskedEmail: MaskEmail(user.Email!)));
    }

    /// <summary>"gerardo@gmail.com" → "g•••••o@gmail.com", so the user knows where to look without exposing the address.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 1)
        {
            return email;
        }

        var local = email[..at];
        var hidden = new string('•', Math.Max(local.Length - 2, 1));
        return local.Length <= 2 ? $"{local[0]}{hidden}{email[at..]}" : $"{local[0]}{hidden}{local[^1]}{email[at..]}";
    }
}
