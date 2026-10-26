using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class ProofTextReaderTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public void Reads_the_condo_paper_receipt_as_the_ocr_returns_it()
    {
        // Real output of Azure AI Vision for a receipt of El Prado (names replaced).
        string[] lines =
        [
            "JPC", "RECIBO DE PAGO", "0002- No 004543", "SI", "180.00", "Fecha:", "02", "12", "25", "Recibí de:", "NOMBRE",
            "Edificio:", "H", "Departamento", "102", "El importe:", "CIENTO OCHENTA CON 00/00", "soles",
            "Cuota correspondiente al mes de", "OCTOBRE 2025", "CANCELADO", "Fecha Banco", "02-12-2025", "Nº Oper. Banc.",
            "00382520", "TESORERO",
        ];

        var fields = ProofTextReader.Read(lines, Today);

        fields.ShouldBe(new ProofTextFields(180.00m, new DateOnly(2025, 12, 2), "00382520"));
    }

    [Fact]
    public void Reads_a_bank_app_screenshot_with_labels_above_the_values()
    {
        string[] lines =
        [
            "¡Operación exitosa!", "S/ 180.00", "Monto transferido", "25 oct 2026 - 09:05 pm", "Número de operación", "07654321",
            "Cuenta destino", "BCP ****2637",
        ];

        ProofTextReader.Read(lines, Today).ShouldBe(new ProofTextFields(180.00m, new DateOnly(2026, 10, 25), "07654321"));
    }

    [Fact]
    public void Reads_labels_and_values_on_the_same_line()
    {
        string[] lines = ["Monto: S/ 1,250.50", "Fecha: 24/10/2026 18:30", "Código de operación: 123456789"];

        ProofTextReader.Read(lines, Today).ShouldBe(new ProofTextFields(1250.50m, new DateOnly(2026, 10, 24), "123456789"));
    }

    [Fact]
    public void The_fee_is_not_the_amount()
    {
        string[] lines = ["Comisión S/ 5.00", "Total S/ 180.00"];

        ProofTextReader.Read(lines, Today).Amount.ShouldBe(180.00m);
    }

    [Theory]
    [InlineData("S/ 180,00", 180.00)]
    [InlineData("S/. 95", 95)]
    [InlineData("PEN 2 400.00", 2400.00)]
    public void Reads_the_usual_ways_of_writing_soles(string line, decimal expected) =>
        ProofTextReader.Read([line], Today).Amount.ShouldBe(expected);

    [Fact]
    public void Future_dates_are_skipped_and_a_day_without_year_is_the_latest_past_one()
    {
        string[] lines = ["Fecha", "27/10/2026", "Enviado el 20 de octubre"];

        ProofTextReader.Read(lines, Today).OperationDate.ShouldBe(new DateOnly(2026, 10, 20));
        ProofTextReader.Read(["30 de diciembre"], Today).OperationDate.ShouldBe(new DateOnly(2025, 12, 30));
    }

    [Fact]
    public void A_date_or_an_amount_after_the_label_is_not_the_operation_number()
    {
        string[] lines = ["Operación realizada", "25/10/2026", "S/ 180.00", "Nro. de operación", "A0012345"];

        ProofTextReader.Read(lines, Today).OperationNumber.ShouldBe("A0012345");
    }

    [Fact]
    public void Text_without_payment_data_gives_nothing()
    {
        ProofTextReader.Read(["Hola", "casi 50 personas", "Movimientos"], Today)
            .ShouldBe(new ProofTextFields(null, null, null));
    }
}
