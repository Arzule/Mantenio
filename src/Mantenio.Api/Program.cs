using Mantenio.Api.Features.Account;
using Mantenio.Api.Features.Apartments;
using Mantenio.Api.Features.Auth;
using Mantenio.Api.Features.BankAccounts;
using Mantenio.Api.Features.Devices;
using Mantenio.Api.Features.Invitations;
using Mantenio.Api.Features.Ledger;
using Mantenio.Api.Features.Notifications;
using Mantenio.Api.Features.Reports;
using Mantenio.Api.Features.Settings;
using Mantenio.Api.Features.Setup;
using Mantenio.Api.Features.SystemInfo;
using Mantenio.Api.Features.Users;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Api.Infrastructure.RateLimiting;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Scalar.AspNetCore;

// `Mantenio.Api <command> --option value` runs a setup command instead of the web server (see SetupCommands), and
// `Mantenio.Api job <name>` runs a scheduled job (see JobCommands).
var command = args.Length > 0 && SetupCommands.Names.Contains(args[0]) ? args[0] : null;
var jobName = args.Length > 0 && args[0] == "job" ? (args.Length > 1 ? args[1] : "") : null;
var builder = WebApplication.CreateBuilder(command is not null ? args[1..] : jobName is not null ? args[Math.Min(2, args.Length)..] : args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<AppDbContext>(AppDbContext.ConnectionName, settings =>
{
    // Aiven Free allows 20 connections in total; keep headroom for jobs, migrations and admin tools.
    if (settings.ConnectionString is not null)
    {
        settings.ConnectionString = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            MaxPoolSize = 10,
        }.ConnectionString;
    }
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMantenioProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddMantenioAuth();
builder.Services.AddMantenioRateLimiting(builder.Configuration);
builder.Services.AddMantenioEmail(builder.Configuration, builder.Environment);
builder.Services.AddScoped<SignInFlow>();
builder.Services.AddScoped<Seeding>();
builder.Services.AddSingleton<CondoClock>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddMantenioStorage(builder.Configuration);
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<ReceiptEmails>();
builder.Services.AddScoped<DelinquencyService>();
builder.Services.AddScoped<LedgerJobs>();
builder.Services.AddMantenioPush(builder.Configuration);

// The web app (Cloudflare Pages) lives on another origin. The Android app calls the API natively, without CORS.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (jobName is not null)
{
    return await JobCommands.RunAsync(app.Services, jobName, CancellationToken.None);
}

if (command is not null)
{
    return await SetupCommands.RunAsync(app, command);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await DevSeedOptions.RunAsync(app);

app.MapDefaultEndpoints();
app.MapSystemInfoEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapInvitationEndpoints();
app.MapApartmentEndpoints();
app.MapUserAdminEndpoints();
app.MapApartmentAdminEndpoints();
app.MapSettingsEndpoints();
app.MapBankAccountEndpoints();
app.MapNotificationEndpoints();
app.MapLedgerEndpoints();
app.MapReportEndpoints();
app.MapDeviceEndpoints();
app.MapLocalFiles();

await app.RunAsync();
return 0;
