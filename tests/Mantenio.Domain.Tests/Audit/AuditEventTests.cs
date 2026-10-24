using Mantenio.Domain.Audit;
using Shouldly;

namespace Mantenio.Domain.Tests.Audit;

public sealed class AuditEventTests
{
    [Fact]
    public void System_events_cannot_have_an_actor() =>
        Should.Throw<ArgumentException>(() =>
            new AuditEvent("User", "1", "Seeded", AuditSource.System, Guid.NewGuid(), DateTimeOffset.UtcNow));

    [Fact]
    public void Admin_events_keep_the_actor_and_the_reason()
    {
        var actor = Guid.NewGuid();

        var auditEvent = new AuditEvent("User", "42", "Deactivated", AuditSource.Admin, actor, DateTimeOffset.UtcNow, reason: "moved out");

        auditEvent.ActorUserId.ShouldBe(actor);
        auditEvent.Reason.ShouldBe("moved out");
    }
}
