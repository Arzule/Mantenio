var builder = DistributedApplication.CreateBuilder(args);

// Local PostgreSQL mirrors production (Aiven, PostgreSQL 18). Data survives restarts thanks to the volume.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithDataVolume("mantenio-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("mantenio", databaseName: "pradosj_db");

var api = builder.AddProject<Projects.Mantenio_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Mantenio_Web>("web")
    .WithReference(api)
    .WaitFor(api);

await builder.Build().RunAsync();
