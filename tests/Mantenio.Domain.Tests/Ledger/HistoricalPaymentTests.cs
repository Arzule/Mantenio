using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class HistoricalPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_amount_and_date_are_required_and_it_is_not_a_reviewable_proof()
    {
        var proof = PaymentProof.Historical(Guid.NewGuid(), Guid.NewGuid(), 180m, new DateOnly(2023, 3, 2), null, null, null, " 0002-1234 ", null, Now);

        proof.Status.ShouldBe(PaymentProofStatus.Historical);
        proof.HasImage.ShouldBeFalse();
        proof.BankName.ShouldBe("");
        proof.OperationNumber.ShouldBe("");
        proof.PaperReceiptNumber.ShouldBe("0002-1234");
        Should.Throw<InvalidOperationException>(() => proof.Approve(180m, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Its_data_can_be_corrected_and_its_photo_replaced()
    {
        var proof = PaymentProof.Historical(Guid.NewGuid(), Guid.NewGuid(), 180m, new DateOnly(2023, 3, 2), null, null, null, null, "first.jpg", Now);

        proof.SetHistoricalDetails(190m, new DateOnly(2023, 3, 3), "Vecina", "BCP", "00-123 456", null);
        var previous = proof.ReplaceImage("second.jpg");

        previous.ShouldBe("first.jpg");
        proof.ImageObjectKey.ShouldBe("second.jpg");
        proof.DeclaredAmount.ShouldBe(190m);
        proof.OperationNumber.ShouldBe("00123456");
        proof.PayerName.ShouldBe("Vecina");
    }

    [Fact]
    public void A_regular_proof_cannot_be_edited_as_historical()
    {
        var proof = new PaymentProof(Guid.NewGuid(), Guid.NewGuid(), 180m, new DateOnly(2026, 10, 1), "BCP", "123", "image.jpg", Now);

        Should.Throw<InvalidOperationException>(() => proof.ReplaceImage("other.jpg"));
        Should.Throw<InvalidOperationException>(() => proof.SetHistoricalDetails(1m, new DateOnly(2026, 10, 1), null, null, null, null));
    }
}
