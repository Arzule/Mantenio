using Mantenio.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Mantenio.Api.Tests.Security;

/// <summary>Like production: the app runs with a login that does not own the tables, and only the migrations use the owner.</summary>
public sealed class RuntimeLoginTests(RuntimeLoginTests.Factory factory) : IClassFixture<RuntimeLoginTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_migrating_with_the_owner_the_app_keeps_using_the_runtime_login()
    {
        for (var i = 0; i < 3; i++)
        {
            var user = await factory.WithDbAsync(db => db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync(Ct));
            user.ShouldBe(factory.RuntimeRole);
        }

        var error = await Should.ThrowAsync<PostgresException>(() =>
            factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"Charges\"", Ct)));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>An API whose app login is not the owner of the tables.</summary>
    public sealed class Factory : ApiFactory
    {
        protected override bool SeparateRuntimeLogin => true;
    }
}
