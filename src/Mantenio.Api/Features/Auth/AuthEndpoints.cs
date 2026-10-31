using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.RateLimiting;
using Mantenio.Domain.Audit;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace Mantenio.Api.Features.Auth;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<SignInResponse>();
        group.MapPost("/two-factor", VerifyTwoFactorAsync).RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<SignInResponse>();
        group.MapPost("/two-factor/resend", ResendTwoFactorAsync).RequireRateLimiting(RateLimitPolicies.Email)
            .Produces<SignInResponse>();
        group.MapPost("/email/confirm", ConfirmEmailAsync).RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<SignInResponse>();
        group.MapPost("/email/resend", ResendEmailConfirmationAsync).RequireRateLimiting(RateLimitPolicies.Email)
            .Produces<SignInResponse>();
        group.MapPost("/refresh", RefreshAsync).RequireRateLimiting(RateLimitPolicies.Refresh)
            .Produces<AuthTokens>();
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/password/forgot", ForgotPasswordAsync).RequireRateLimiting(RateLimitPolicies.Email);
        group.MapPost("/password/reset", ResetPasswordAsync).RequireRateLimiting(RateLimitPolicies.Auth);

        return app;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, UserManager<AppUser> users, SignInFlow flow, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return ApiProblems.Unauthorized(ErrorCodes.InvalidCredentials);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Locked();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return await users.IsLockedOutAsync(user) ? Locked() : ApiProblems.Unauthorized(ErrorCodes.InvalidCredentials);
        }

        await users.ResetAccessFailedCountAsync(user);

        // Only revealed after the right password, so it cannot be used to probe accounts.
        if (!user.IsActive)
        {
            return ApiProblems.Forbidden(ErrorCodes.AccountInactive);
        }

        return await flow.ContinueAsync(user, request.KeepSignedIn, cancellationToken);
    }

    private static Task<IResult> VerifyTwoFactorAsync(
        VerifyCodeRequest request, UserManager<AppUser> users, TokenService tokens, SessionService sessions, CancellationToken cancellationToken) =>
        VerifyCodeAsync(request, ChallengePurpose.TwoFactor, users, tokens, sessions, cancellationToken);

    private static Task<IResult> ConfirmEmailAsync(
        VerifyCodeRequest request, UserManager<AppUser> users, TokenService tokens, SessionService sessions, CancellationToken cancellationToken) =>
        VerifyCodeAsync(request, ChallengePurpose.ConfirmEmail, users, tokens, sessions, cancellationToken);

    /// <summary>
    /// Second step of login (admin 2FA) or registration (email confirmation). A correct email-confirmation code
    /// also proves access to the mailbox, so it counts as the admin second factor for that sign-in.
    /// </summary>
    private static async Task<IResult> VerifyCodeAsync(
        VerifyCodeRequest request,
        ChallengePurpose purpose,
        UserManager<AppUser> users,
        TokenService tokens,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var (challenge, user, problem) = await ResolveChallengeAsync(request.ChallengeToken, purpose, users, tokens);
        if (problem is not null)
        {
            return problem;
        }

        var valid = purpose == ChallengePurpose.TwoFactor
            ? await users.VerifyTwoFactorTokenAsync(user!, TokenOptions.DefaultEmailProvider, request.Code)
            : (await users.ConfirmEmailAsync(user!, request.Code)).Succeeded;
        if (!valid)
        {
            await users.AccessFailedAsync(user!);
            return await users.IsLockedOutAsync(user!) ? Locked() : ApiProblems.BadRequest(ErrorCodes.InvalidCode);
        }

        await users.ResetAccessFailedCountAsync(user!);
        var session = await sessions.StartAsync(user!, challenge!.KeepSignedIn, cancellationToken);
        return TypedResults.Ok(new SignInResponse(SignInStep.SignedIn, session));
    }

    private static Task<IResult> ResendTwoFactorAsync(
        ResendCodeRequest request, UserManager<AppUser> users, TokenService tokens, SignInFlow flow, CancellationToken cancellationToken) =>
        ResendAsync(request, ChallengePurpose.TwoFactor, users, tokens, flow, cancellationToken);

    private static Task<IResult> ResendEmailConfirmationAsync(
        ResendCodeRequest request, UserManager<AppUser> users, TokenService tokens, SignInFlow flow, CancellationToken cancellationToken) =>
        ResendAsync(request, ChallengePurpose.ConfirmEmail, users, tokens, flow, cancellationToken);

    private static async Task<IResult> ResendAsync(
        ResendCodeRequest request,
        ChallengePurpose purpose,
        UserManager<AppUser> users,
        TokenService tokens,
        SignInFlow flow,
        CancellationToken cancellationToken)
    {
        var (challenge, user, problem) = await ResolveChallengeAsync(request.ChallengeToken, purpose, users, tokens);
        return problem ?? await flow.SendCodeAsync(user!, purpose, challenge!.KeepSignedIn, cancellationToken);
    }

    private static async Task<(Challenge? Challenge, AppUser? User, IResult? Problem)> ResolveChallengeAsync(
        string challengeToken, ChallengePurpose purpose, UserManager<AppUser> users, TokenService tokens)
    {
        var challenge = await tokens.ReadChallengeAsync(challengeToken, purpose);
        var user = challenge is null ? null : await users.FindByIdAsync(challenge.UserId.ToString());
        if (challenge is null || user is null)
        {
            return (null, null, ApiProblems.Unauthorized(ErrorCodes.ChallengeExpired));
        }

        if (!user.IsActive)
        {
            return (null, null, ApiProblems.Forbidden(ErrorCodes.AccountInactive));
        }

        if (await users.IsLockedOutAsync(user))
        {
            return (null, null, Locked());
        }

        return (challenge, user, null);
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, SessionService sessions, CancellationToken cancellationToken)
    {
        var tokens = await sessions.RefreshAsync(request.RefreshToken, cancellationToken);
        return tokens is null ? ApiProblems.Unauthorized(ErrorCodes.InvalidRefreshToken) : TypedResults.Ok(tokens);
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, SessionService sessions, CancellationToken cancellationToken)
    {
        await sessions.SignOutAsync(request.RefreshToken, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Always 202, whether or not the email exists, so it cannot be used to discover accounts.</summary>
    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request, UserManager<AppUser> users, AccountEmails emails, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is { IsActive: true } && !await users.IsLockedOutAsync(user))
        {
            var code = await users.GeneratePasswordResetTokenAsync(user);
            await emails.SendPasswordResetCodeAsync(user, code, cancellationToken);
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        UserManager<AppUser> users,
        SessionService sessions,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is not { IsActive: true })
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidCode);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Locked();
        }

        var result = await users.ResetPasswordAsync(user, request.Code, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                await users.AccessFailedAsync(user);
                return ApiProblems.BadRequest(ErrorCodes.InvalidCode);
            }

            return ApiProblems.BadRequest(ErrorCodes.WeakPassword);
        }

        // The code arrived by email, so the address is proven; a temporary password is no longer temporary.
        user.EmailConfirmed = true;
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await sessions.RevokeAllAsync(user.Id, cancellationToken);

        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.User, user.Id.ToString(), "PasswordReset",
            Roles.ToAuditSource(user.Role),
            user.Id, time.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Locked() => ApiProblems.Error(StatusCodes.Status423Locked, ErrorCodes.AccountLocked);
}
