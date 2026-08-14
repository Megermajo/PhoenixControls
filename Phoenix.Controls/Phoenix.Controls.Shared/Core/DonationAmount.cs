using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Canonical money for inbound donation / tip events.
    ///
    /// WHY THIS EXISTS. Twelve-odd Streamer.bot donation integrations each report an
    /// amount in their own shape: a JSON number, an invariant string ("5.00"), a
    /// European string ("5,00"), a currency-decorated string ("$5.00", "1.234,56 EUR"),
    /// or — Twitch charity — an integer <c>value</c> plus a separate
    /// <c>decimalPlaces</c> exponent and never a float at all. The two existing
    /// consumers both parsed with <c>NumberStyles.Float</c> + InvariantCulture, which
    /// silently yields 0 for every one of the decorated / European forms, and the
    /// alert-text renderer floored to int so a EUR 2.50 tip announced as "2".
    ///
    /// The fix is to normalise ONCE at the ingestion boundary into two representations
    /// that every downstream consumer can use without re-parsing:
    ///   * <see cref="Value"/> — an invariant <see cref="decimal"/>. Rendered to
    ///     scripts / alert text as {amount}, so the cents survive.
    ///   * <see cref="MinorUnits"/> — integer minor units (cents). The exact, drift-free
    ///     form for arithmetic; Loyalty points and Timer seconds are both integer
    ///     scalings of it.
    /// decimal (not double) because money is base-10 and the multiply-then-round in the
    /// points/seconds paths must not inherit binary float drift.
    ///
    /// Deliberately NOT a currency conversion layer: <see cref="Currency"/> is carried
    /// through verbatim for display and for the streamer's own script logic. Phoenix
    /// never converts between currencies — a rate source would be a network dependency
    /// and a silent source of wrong money.
    /// </summary>
    public readonly record struct DonationAmount(decimal Value, long MinorUnits, string Currency)
    {
        /// <summary>The zero amount — what an unparseable payload resolves to.</summary>
        public static readonly DonationAmount None = new(0m, 0L, "");

        /// <summary>True when a positive amount was actually recovered.</summary>
        public bool HasValue => MinorUnits > 0;

        /// <summary>
        /// Invariant decimal rendering, trimmed to the currency's natural precision
        /// (2 for most, 0 for JPY-class). This is what {amount} renders and what
        /// <c>event.amount</c> carries.
        /// </summary>
        public string ToInvariantString()
            => Value.ToString("0." + new string('#', Math.Max(0, ExponentFor(Currency))), CultureInfo.InvariantCulture);

        // ── Currency exponents ────────────────────────────────────────────────
        // Minor-unit exponent per ISO-4217. Only the zero- and three-decimal
        // exceptions are listed; everything unlisted (and the empty/unknown case)
        // is the 2-decimal default, which is correct for every currency a streaming
        // donation realistically arrives in.
        private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
        {
            "JPY", "KRW", "VND", "CLP", "ISK", "HUF", "TWD", "UGX", "XAF", "XOF", "XPF", "RWF", "PYG", "DJF", "GNF", "KMF", "VUV",
        };
        private static readonly HashSet<string> ThreeDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
        {
            "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
        };

        /// <summary>Minor-unit exponent for an ISO code; 2 when unknown.</summary>
        public static int ExponentFor(string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency)) return 2;
            var c = currency.Trim();
            if (ZeroDecimalCurrencies.Contains(c)) return 0;
            if (ThreeDecimalCurrencies.Contains(c)) return 3;
            return 2;
        }

        /// <summary>
        /// Builds an amount from an explicit integer + exponent pair. This is the
        /// Twitch charity shape (<c>value</c> + <c>decimalPlaces</c>) and is exact —
        /// no string parsing, no separator ambiguity.
        /// </summary>
        public static DonationAmount FromMinorUnits(long rawValue, int decimalPlaces, string currency)
        {
            if (decimalPlaces < 0 || decimalPlaces > 9) decimalPlaces = ExponentFor(currency);
            decimal scale = 1m;
            for (int i = 0; i < decimalPlaces; i++) scale *= 10m;
            decimal value = rawValue / scale;

            // Re-express in the CURRENCY's canonical exponent so MinorUnits is
            // comparable across sources even when a payload used a different one.
            return FromDecimal(value, currency);
        }

        /// <summary>Builds an amount from an already-decimal value.</summary>
        public static DonationAmount FromDecimal(decimal value, string? currency)
        {
            string cur = (currency ?? "").Trim().ToUpperInvariant();
            int exp = ExponentFor(cur);
            decimal scale = 1m;
            for (int i = 0; i < exp; i++) scale *= 10m;

            // AwayFromZero matches the Loyalty money layer's rounding
            // (LoyaltyService.Earn.cs) — banker's rounding on money is a
            // surprise the streamer would have to reverse-engineer.
            decimal minor = Math.Round(value * scale, 0, MidpointRounding.AwayFromZero);
            if (minor > long.MaxValue || minor < long.MinValue) return None;

            return new DonationAmount(value, (long)minor, cur);
        }

        /// <summary>
        /// Parses a free-form amount string from an unspecified broker payload.
        ///
        /// Handles, in one pass: JSON numbers rendered as text, invariant decimals,
        /// European decimal commas, grouping separators in either convention, leading
        /// or trailing currency symbols, and an embedded ISO code. Returns
        /// <see cref="None"/> on anything it cannot read — a donation that fails to
        /// parse must be worth zero, never a guess.
        /// </summary>
        /// <param name="raw">The payload's amount field, in whatever shape it arrived.</param>
        /// <param name="currencyHint">A currency from a sibling payload field, if any.</param>
        public static DonationAmount Parse(string? raw, string? currencyHint = null)
        {
            if (string.IsNullOrWhiteSpace(raw)) return None;

            string s = raw.Trim();
            string currency = (currencyHint ?? "").Trim().ToUpperInvariant();

            // Pull out an embedded 3-letter ISO code ("5.00 EUR" / "EUR 5.00").
            // Only when no explicit hint was supplied — a sibling `currency` field
            // is more trustworthy than text scraped out of the amount.
            if (currency.Length == 0)
            {
                var iso = ExtractIsoCode(s);
                if (iso.Length == 3) currency = iso;
            }

            // Strip everything that is not a digit, separator or sign. This removes
            // currency symbols (both leading and trailing), the ISO letters, and any
            // stray whitespace including the non-breaking space European formats use.
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-' || ch == '+')
                    sb.Append(ch);
            }
            string cleaned = sb.ToString();
            if (cleaned.Length == 0) return None;

            // Sign: keep only a single leading sign.
            bool negative = cleaned[0] == '-';
            cleaned = cleaned.TrimStart('+', '-');
            if (cleaned.Length == 0) return None;

            string normalized = NormalizeSeparators(cleaned);
            if (normalized.Length == 0) return None;

            if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint,
                                  CultureInfo.InvariantCulture, out decimal value))
                return None;

            if (negative) value = -value;
            return FromDecimal(value, currency);
        }

        /// <summary>
        /// Resolves '.' / ',' into a single invariant decimal point.
        ///
        /// The rule: when BOTH separators appear, the RIGHTMOST is the decimal point
        /// and the other is grouping — true for every real-world convention
        /// ("1,234.56" and "1.234,56" both mean the same). When only ONE appears it is
        /// ambiguous, and the tie-break is the digit count after it: exactly three
        /// digits with no other separator reads as grouping ("1,234" = 1234), anything
        /// else reads as a decimal point ("5,00" = 5.00, "1,2345" = 1.2345).
        /// </summary>
        private static string NormalizeSeparators(string cleaned)
        {
            int lastDot = cleaned.LastIndexOf('.');
            int lastComma = cleaned.LastIndexOf(',');

            if (lastDot >= 0 && lastComma >= 0)
            {
                char decimalSep = lastDot > lastComma ? '.' : ',';
                char groupSep = decimalSep == '.' ? ',' : '.';
                return cleaned.Replace(groupSep.ToString(), "").Replace(decimalSep, '.');
            }

            int only = lastDot >= 0 ? lastDot : lastComma;
            if (only < 0) return cleaned; // pure integer

            char sep = lastDot >= 0 ? '.' : ',';
            // More than one occurrence of the same separator can only be grouping
            // ("1.234.567").
            int count = 0;
            foreach (char ch in cleaned) if (ch == sep) count++;
            if (count > 1) return cleaned.Replace(sep.ToString(), "");

            int digitsAfter = cleaned.Length - only - 1;
            if (digitsAfter == 3)
            {
                // "1,234" → grouping. But "0,123" style with a leading zero is a
                // decimal in every locale that writes it, so keep that as a decimal.
                string head = cleaned.Substring(0, only);
                bool headIsGroupable = head.Length > 0 && head.Length <= 3 && head != "0";
                if (headIsGroupable) return cleaned.Remove(only, 1);
            }
            return cleaned.Replace(sep, '.');
        }

        /// <summary>Finds a standalone 3-letter ISO code in an amount string.</summary>
        private static string ExtractIsoCode(string s)
        {
            int run = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                bool isLetter = i < s.Length && char.IsLetter(s[i]);
                if (isLetter) { run++; continue; }
                if (run == 3)
                    return s.Substring(i - 3, 3).ToUpperInvariant();
                run = 0;
            }
            return "";
        }
    }
}
