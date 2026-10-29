using Microsoft.EntityFrameworkCore;
using Mantenio.Api.Infrastructure.Persistence;

namespace Mantenio.Api.Features.Setup;

/// <summary>
/// Command-line entry points (`Mantenio.Api &lt;command&gt; --option value`), run once per condo instance:
/// <list type="bullet">
///   <item><c>migrate</c>: applies pending migrations.</item>
///   <item><c>seed-apartments</c>: loads the towers and apartments from the condo profile's apartments.csv.</item>
///   <item><c>seed-founder --email x --first-name y --last-name z</c>: creates the founder with a temporary password.</item>
/// </list>
/// </summary>
internal static class SetupCommands
{
    public static readonly string[] Names = ["migrate", "seed-apartments", "seed-founder"];

    public static async Task<int> RunAsync(WebApplication app, string command)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var config = app.Configuration;
        var output = Console.Out;

        switch (command)
        {
            case "migrate":
                await MigrationConnection.MigrateAsync(services.GetRequiredService<AppDbContext>(), app.Configuration);
                await output.WriteLineAsync("Migrations applied.");
                return 0;

            case "seed-apartments":
                var csv = Seeding.EmbeddedApartmentList();
                if (csv is null)
                {
                    await Console.Error.WriteLineAsync("This condo profile has no apartments.csv.");
                    return 1;
                }

                var added = await services.GetRequiredService<Seeding>().SeedApartmentsAsync(csv, CancellationToken.None);
                await output.WriteLineAsync($"Apartments added: {added}.");
                return 0;

            case "seed-founder":
                var email = config["email"];
                var firstName = config["first-name"];
                var lastName = config["last-name"];
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                {
                    await Console.Error.WriteLineAsync("Usage: seed-founder --email <email> --first-name <name> --last-name <name>");
                    return 1;
                }

                var founder = await services.GetRequiredService<Seeding>()
                    .CreateFounderAsync(email, firstName, lastName, password: null, CancellationToken.None);
                if (founder is null)
                {
                    await Console.Error.WriteLineAsync("A founder already exists.");
                    return 1;
                }

                await output.WriteLineAsync($"Founder created: {founder.Value.User.Email}");
                await output.WriteLineAsync($"Temporary password (shown once, must be changed at first login): {founder.Value.Password}");
                return 0;

            default:
                await Console.Error.WriteLineAsync($"Unknown command '{command}'. Available: {string.Join(", ", Names)}.");
                return 1;
        }
    }
}

/// <summary>
/// Development only ("DevSeed" section): fills an empty local database with the apartments and a founder
/// with a known password, so the app can be tried right after `dotnet run --project src/Mantenio.AppHost`.
/// </summary>
internal sealed class DevSeedOptions
{
    public const string SectionName = "DevSeed";

    public bool Enabled { get; set; }
    public string FounderEmail { get; set; } = "";
    public string FounderPassword { get; set; } = "";
    public string FounderFirstName { get; set; } = "";
    public string FounderLastName { get; set; } = "";

    public static async Task RunAsync(WebApplication app)
    {
        var options = app.Configuration.GetSection(SectionName).Get<DevSeedOptions>();
        if (options is not { Enabled: true } || !app.Environment.IsDevelopment())
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var seeding = scope.ServiceProvider.GetRequiredService<Seeding>();
        if (Seeding.EmbeddedApartmentList() is { } csv)
        {
            await seeding.SeedApartmentsAsync(csv, CancellationToken.None);
        }

        if (options.FounderEmail.Length > 0)
        {
            await seeding.CreateFounderAsync(
                options.FounderEmail, options.FounderFirstName, options.FounderLastName, options.FounderPassword, CancellationToken.None);
        }
    }
}
