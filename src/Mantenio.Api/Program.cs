using Mantenio.Api.Features.SystemInfo;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddMantenioProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

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

app.MapDefaultEndpoints();
app.MapSystemInfoEndpoints();

await app.RunAsync();
