using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class ReceiptTests
{
    [Theory]
    [InlineData(180, "CIENTO OCHENTA CON 00/100 SOLES")]
    [InlineData(1, "UN CON 00/100 SOL")]
    [InlineData(21, "VEINTIÚN CON 00/100 SOLES")]
    [InlineData(31, "TREINTA Y UN CON 00/100 SOLES")]
    [InlineData(100, "CIEN CON 00/100 SOLES")]
    [InlineData(101, "CIENTO UN CON 00/100 SOLES")]
    [InlineData(165.5, "CIENTO SESENTA Y CINCO CON 50/100 SOLES")]
    [InlineData(1000, "MIL CON 00/100 SOLES")]
    [InlineData(2540.75, "DOS MIL QUINIENTOS CUARENTA CON 75/100 SOLES")]
    [InlineData(21000, "VEINTIÚN MIL CON 00/100 SOLES")]
    [InlineData(0.5, "CERO CON 50/100 SOLES")]
    public void Amounts_are_written_in_words(decimal amount, string expected) =>
        SpanishAmountInWords.Soles(amount).ShouldBe(expected);

    [Fact]
    public void Receipt_numbers_have_the_series_and_six_digits() =>
        Receipt.FormatNumber("0003", 42).ShouldBe("0003-000042");

    [Fact]
    public void A_proof_moves_through_its_states_only_forward()
    {
        var now = DateTimeOffset.UtcNow;
        var proof = new PaymentProof(Guid.NewGuid(), Guid.NewGuid(), 180m, new DateOnly(2026, 11, 2), "BCP", "00-1234 56", "key", now);

        proof.OperationNumber.ShouldBe("00123456");
        Should.Throw<InvalidOperationException>(() => proof.Void("x", Guid.NewGuid(), now));

        proof.Approve(180m, Guid.NewGuid(), now);
        Should.Throw<InvalidOperationException>(() => proof.Reject("x", Guid.NewGuid(), now));

        proof.Void("Pago duplicado", Guid.NewGuid(), now);
        proof.Status.ShouldBe(PaymentProofStatus.Voided);
    }

    [Fact]
    public void A_proof_an_admin_registers_is_made_out_to_the_payer()
    {
        var now = DateTimeOffset.UtcNow;
        var registered = PaymentProof.RegisteredByAdmin(Guid.NewGuid(), Guid.NewGuid(), "  Rosa Quispe ", 180m, new DateOnly(2026, 11, 2), "BCP", "00-123", "k", now);
        var uploaded = new PaymentProof(Guid.NewGuid(), Guid.NewGuid(), 180m, new DateOnly(2026, 11, 2), "BCP", "00124", "k", now);

        registered.PayerName.ShouldBe("Rosa Quispe");
        registered.IsRegisteredByAdmin.ShouldBeTrue();
        uploaded.IsRegisteredByAdmin.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => PaymentProof.RegisteredByAdmin(Guid.NewGuid(), Guid.NewGuid(), " ", 1m, default, "BCP", "1", "k", now));
    }
}
