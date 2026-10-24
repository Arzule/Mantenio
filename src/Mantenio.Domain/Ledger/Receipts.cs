using System.Globalization;
using System.Text;

namespace Mantenio.Domain.Ledger;

public enum ReceiptStatus
{
    Issued = 0,
    Voided = 1,
}

/// <summary>
/// The receipt of an approved payment proof. Numbers are global and gap-free ("0003-000001"); a voided receipt
/// keeps its number. Each receipt belongs to the monthly folio of the month it was issued in.
/// </summary>
public sealed class Receipt
{
    private Receipt() { } // EF Core

    public Receipt(Guid paymentProofId, int folioPeriod, long sequence, string number, DateTimeOffset issuedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        Id = Guid.CreateVersion7();
        PaymentProofId = paymentProofId;
        FolioPeriod = folioPeriod;
        Sequence = sequence;
        Number = number;
        IssuedAt = issuedAt;
        Status = ReceiptStatus.Issued;
    }

    public Guid Id { get; private set; }
    public Guid PaymentProofId { get; private set; }

    /// <summary>yyyymm of the monthly folio (the month it was issued).</summary>
    public int FolioPeriod { get; private set; }

    public long Sequence { get; private set; }
    public string Number { get; private set; } = "";
    public string? PdfObjectKey { get; private set; }
    public ReceiptStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }

    public void AttachPdf(string objectKey) => PdfObjectKey = objectKey;

    public void Void(DateTimeOffset now)
    {
        Status = ReceiptStatus.Voided;
        VoidedAt = now;
    }

    public static string FormatNumber(string series, long sequence) =>
        $"{series}-{sequence.ToString("D6", CultureInfo.InvariantCulture)}";
}

/// <summary>Amounts in words as written on Peruvian receipts: 180 → "CIENTO OCHENTA CON 00/100 SOLES".</summary>
public static class SpanishAmountInWords
{
    private static readonly string[] Units =
    [
        "CERO", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE", "DIEZ",
        "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE", "DIECISÉIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE", "VEINTE",
        "VEINTIUNO", "VEINTIDÓS", "VEINTITRÉS", "VEINTICUATRO", "VEINTICINCO", "VEINTISÉIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE",
    ];

    private static readonly string[] Tens = ["", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA"];

    private static readonly string[] Hundreds =
        ["", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS", "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS"];

    public static string Soles(decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        var whole = (long)decimal.Truncate(rounded);
        var cents = (int)((rounded - whole) * 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(whole, 999_999_999L, nameof(amount));
        // "UNO" becomes "UN" before a noun: "UN SOL", "VEINTIÚN SOLES", "TREINTA Y UN SOLES".
        var words = Apocope(Words(whole));
        return $"{words} CON {cents.ToString("D2", CultureInfo.InvariantCulture)}/100 {(whole == 1 ? "SOL" : "SOLES")}";
    }

    public static string Words(long number)
    {
        if (number < 30)
        {
            return Units[number];
        }

        var text = new StringBuilder();
        if (number >= 1_000_000)
        {
            var millions = number / 1_000_000;
            text.Append(millions == 1 ? "UN MILLÓN" : Apocope(Words(millions)) + " MILLONES");
            number %= 1_000_000;
            if (number == 0)
            {
                return text.ToString();
            }

            text.Append(' ');
        }

        if (number >= 1000)
        {
            var thousands = number / 1000;
            text.Append(thousands == 1 ? "MIL" : Apocope(Words(thousands)) + " MIL");
            number %= 1000;
            if (number == 0)
            {
                return text.ToString();
            }

            text.Append(' ');
        }

        text.Append(BelowThousand((int)number));
        return text.ToString();
    }

    private static string BelowThousand(int number)
    {
        if (number == 100)
        {
            return "CIEN";
        }

        if (number < 30)
        {
            return Units[number];
        }

        var parts = new List<string>();
        if (number >= 100)
        {
            parts.Add(Hundreds[number / 100]);
            number %= 100;
        }

        if (number > 0)
        {
            parts.Add(number < 30 ? Units[number] : number % 10 == 0 ? Tens[number / 10] : $"{Tens[number / 10]} Y {Units[number % 10]}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>"VEINTIUNO MIL" → "VEINTIÚN MIL", "TREINTA Y UNO MIL" → "TREINTA Y UN MIL".</summary>
    private static string Apocope(string words) =>
        words.EndsWith("VEINTIUNO", StringComparison.Ordinal) ? words[..^9] + "VEINTIÚN"
        : words.EndsWith("UNO", StringComparison.Ordinal) ? words[..^3] + "UN"
        : words;
}
