using System;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FX.Infrastructure.Calendars.Legacy
{
    /// <summary>
    /// Löser användarens expiry-input till riktiga datum (expiry & settlement)
    /// med hjälp av DateConvention. Hanterar både tenorer (on/1d/3w/1m/1y)
    /// och flexibla datumformat på svenska/engelska (t.ex. "01/02", "01 feb", "01-feb-25").
    /// </summary>
    public static class ExpiryInputResolver
    {
        public sealed class Result
        {
            public DateTime ExpiryDate { get; set; }
            public DateTime SettlementDate { get; set; }
            public string ExpiryIso => ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            public string SettlementIso => SettlementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            /// <summary>Veckodag för expiry, t.ex. "Mon (Mån)".</summary>
            public string ExpiryWeekday { get; set; }
            /// <summary>Beskriver hur input tolkades: "Tenor" eller "Date".</summary>
            public string Mode { get; set; }
            /// <summary>Den normaliserade inputen (t.ex. "3w", "on" eller "2026-02-01").</summary>
            public string Normalized { get; set; }
        }

        /// <summary>
        /// Huvudmetod: parsea textbox-strängen och räkna ut datum med DateConvention.
        /// </summary>
        public static Result Resolve(string rawInput, string ccyPair, DataTable holidays)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                throw new ArgumentException("Expiry-fältet är tomt.");

            if (string.IsNullOrWhiteSpace(ccyPair) || ccyPair.Replace("/", "").Trim().Length != 6)
                throw new ArgumentException("Valutapar måste vara t.ex. EURSEK eller EUR/SEK.");

            // 1) Försök tolka som tenor
            string tenor = NormalizeTenor(rawInput);
            var dc = new DateConvention(ccyPair, holidays);

            if (tenor != null)
            {
                var conv = dc.GetConvention(tenor);
                return new Result
                {
                    ExpiryDate = conv.ExpiryDate,
                    SettlementDate = conv.DeliveryDate,
                    ExpiryWeekday = FormatWeekday(conv.ExpiryDate),
                    Mode = "Tenor",
                    Normalized = tenor
                };
            }

            // 2) Annars tolka som datum (sv/en, olika format). Lägg år om saknas.
            DateTime explicitDate = ParseFlexibleDate(rawInput);
            // Skicka in explicit datum till DateConvention (som i din tidigare kod)
            var conv2 = dc.GetConvention(explicitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            return new Result
            {
                ExpiryDate = conv2.ExpiryDate,
                SettlementDate = conv2.DeliveryDate,
                ExpiryWeekday = FormatWeekday(conv2.ExpiryDate),
                Mode = "Date",
                Normalized = explicitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
        }

        // ---------- helpers ----------

        /// <summary>
        /// Normaliserar tenorsträngar. Returnerar t.ex. "on", "3d", "2w", "4m", "1y", annars null.
        /// </summary>
        private static string NormalizeTenor(string s)
        {
            var t = (s ?? "").Trim().ToLowerInvariant();

            // ON / O/N
            if (t == "on" || t == "o/n" || t == "overnight")
                return "on";

            // mönster "123 d|w|m|y" med valfritt mellanrum
            var m = Regex.Match(t, @"^\s*(\d+)\s*([dwmy])\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var num = m.Groups[1].Value;
                var unit = m.Groups[2].Value.ToLowerInvariant();
                return num + unit; // t.ex. "3w"
            }

            return null;
        }

        /// <summary>
        /// Försöker parsea datum från en mängd format och kulturer (sv/en). 
        /// Om år saknas → väljer aktuellt år; om datum redan passerat i år → nästa år.
        /// </summary>
        private static DateTime ParseFlexibleDate(string s)
        {
            var raw = (s ?? "").Trim();

            // Tillåt både bindestreck och snedstreck som skiljetecken
            // (vi testar exakta format nedan, men this helps free-form)
            var cultures = new[]
            {
                CultureInfo.GetCultureInfo("sv-SE"),
                CultureInfo.GetCultureInfo("en-US")
            };

            // Format-lista: både numeriska och med månadsnamn
            var fmtsWithYear = new[]
            {
                "d/M/yy","dd/MM/yy","d/M/yyyy","dd/MM/yyyy",
                "d-M-yy","dd-MM-yy","d-M-yyyy","dd-MM-yyyy",
                "d MMM yy","dd MMM yy","d MMM yyyy","dd MMM yyyy",
                "d-MMM-yy","dd-MMM-yy","d-MMM-yyyy","dd-MMM-yyyy",
                "yyyy-MM-dd"
            };

            var fmtsWithoutYear = new[]
            {
                "d/M","dd/MM","d-M","dd-M",
                "d MMM","dd MMM","d-MMM","dd-MMM"
            };

            DateTime dt;

            // 1) Försök först med format som innehåller årtal
            foreach (var ci in cultures)
            {
                if (DateTime.TryParseExact(raw, fmtsWithYear, ci, DateTimeStyles.None, out dt))
                    return dt.Date;
            }

            // 2) Utan årtal → lägg på år och justera om datum passerat
            foreach (var ci in cultures)
            {
                if (DateTime.TryParseExact(raw, fmtsWithoutYear, ci, DateTimeStyles.AllowWhiteSpaces, out dt))
                {
                    var today = DateTime.Today;
                    dt = new DateTime(today.Year, dt.Month, dt.Day);
                    if (dt.Date < today.Date)
                        dt = dt.AddYears(1);
                    return dt.Date;
                }
            }

            // 3) Sista-chans: låt TryParse med kulturerna försöka (fri text som "01 feb", "Feb 1")
            foreach (var ci in cultures)
            {
                if (DateTime.TryParse(raw, ci, DateTimeStyles.AllowWhiteSpaces, out dt))
                {
                    // Om år saknas kommer TryParse sätta current year redan.
                    // Se ändå till att justera framåt om datum passerat och inget år angavs uttryckligen.
                    // Heuristik: om strängen inte innehåller någon siffra med 4 tecken → tolka som utan år.
                    bool explicitYear = Regex.IsMatch(raw, @"\b\d{4}\b");
                    if (!explicitYear)
                    {
                        var today = DateTime.Today;
                        dt = new DateTime(today.Year, dt.Month, dt.Day);
                        if (dt.Date < today.Date)
                            dt = dt.AddYears(1);
                    }
                    return dt.Date;
                }
            }

            throw new FormatException("Kunde inte tolka datumet. Exempel: on, 3w, 1m, 01/02, 01-feb, 01/02/2026.");
        }

        private static string FormatWeekday(DateTime d)
        {
            // Visa båda språk kort (valfritt – lätt att ändra)
            string en = d.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
            string sv = d.ToString("ddd", CultureInfo.GetCultureInfo("sv-SE"));
            return $"{en} ({sv})";
        }
    }
}
