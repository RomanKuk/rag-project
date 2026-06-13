using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Security;

namespace DocumentQA.Infrastructure.Security;

/// <summary>
/// Deterministic regex-based PII masker. Order matters: emails are masked before
/// phone/card so the digit patterns don't partially chew an email's local part.
/// Credit-card candidates are Luhn-validated to avoid masking ordinary numbers
/// (page counts, years, IDs). Citation markers like [doc.pdf, page 12] never
/// match these patterns.
/// </summary>
public sealed class RegexPiiRedactor : IPiiRedactor
{
    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex Ssn = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled);

    // 13-16 digit sequences allowing single space/hyphen separators.
    private static readonly Regex CardCandidate = new(
        @"\b(?:\d[ -]?){13,16}\b",
        RegexOptions.Compiled);

    // North-American style phone numbers (10 digits, optional separators/parens).
    private static readonly Regex Phone = new(
        @"\(?\b\d{3}\)?[ .\-]\d{3}[ .\-]\d{4}\b",
        RegexOptions.Compiled);

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = Email.Replace(text, "[REDACTED-EMAIL]");
        text = Ssn.Replace(text, "[REDACTED-SSN]");
        text = CardCandidate.Replace(text, m =>
        {
            var digits = new string(Array.FindAll(m.Value.ToCharArray(), char.IsDigit));
            return LuhnValid(digits) ? "[REDACTED-CARD]" : m.Value;
        });
        text = Phone.Replace(text, "[REDACTED-PHONE]");
        return text;
    }

    private static bool LuhnValid(string digits)
    {
        if (digits.Length is < 13 or > 16) return false;
        int sum = 0;
        bool alt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (alt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }
}
