var builder = DistributedApplication.CreateBuilder(args);

// Local PostgreSQL mirrors production (Aiven, PostgreSQL 18). Data survives restarts thanks to the volume.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithDataVolume("mantenio-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

// One database per condo, named after the profile slug (e.g. demo_db), like in production.
var database = postgres.AddDatabase("mantenio", databaseName: $"{Mantenio.Shared.Condos.CondoProfile.Current.Slug}_db");

// JWT signing key: generated once and kept in this AppHost's user secrets (never in the repo).
var jwtSigningKey = builder.AddParameter(
    "jwt-signing-key",
    new GenerateParameterDefault { MinLength = 64, Special = false },
    secret: true,
    persist: true);

// Local inbox: every email the API sends (2FA, confirmation and reset codes) shows up in the Mailpit web UI.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31.6")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithLifetime(ContainerLifetime.Persistent);
var smtp = mailpit.GetEndpoint("smtp");

var api = builder.AddProject<Projects.Mantenio_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("Email__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Email__Port", smtp.Property(EndpointProperty.Port))
    .WithEnvironment("Email__Security", "None")
    .WaitFor(mailpit)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Mantenio_Web>("web")
    .WithReference(api)
    .WaitFor(api);

await builder.Build().RunAsync();
