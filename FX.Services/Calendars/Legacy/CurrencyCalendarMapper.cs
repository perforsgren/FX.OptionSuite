using System;
using System.Collections.Generic;
using System.Data;

namespace FX.Infrastructure.Calendars.Legacy
{
    public static class CurrencyCalendarMapper
    {
        // Currency -> Calendar name
        private static readonly Dictionary<string, string> CurrencyToCalendar =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "EUR", "TARGET" },
            { "USD", "USA" },
            { "SEK", "SWEDEN" },
            { "NOK", "NORWAY" },
            { "GBP", "ENGLAND" },
            { "CAD", "CANADA" },
            { "CHF", "SWITZERLAND" },
            { "AUD", "AUSTRALIA" },
            { "RUB", "RUSSIA" },
            { "JPY", "JAPAN" }
        };

        public static string[] GetCalendarsForPair(string ccyPair)
        {
            if (string.IsNullOrWhiteSpace(ccyPair))
                throw new ArgumentException("Valutapar saknas.", nameof(ccyPair));

            var pair = ccyPair.Replace("/", "").Trim().ToUpperInvariant();
            if (pair.Length != 6)
                throw new ArgumentException("Valutapar måste vara 6 tecken, t.ex. EURSEK.", nameof(ccyPair));

            var ccy1 = pair.Substring(0, 3);
            var ccy2 = pair.Substring(3, 3);

            string cal1, cal2;
            if (!CurrencyToCalendar.TryGetValue(ccy1, out cal1))
                throw new KeyNotFoundException("Ingen kalender mappad för " + ccy1);
            if (!CurrencyToCalendar.TryGetValue(ccy2, out cal2))
                throw new KeyNotFoundException("Ingen kalender mappad för " + ccy2);

            return new[] { cal1, cal2 };
        }

        public static bool IsBusinessDay(string ccyPair, DateTime date, HolidayCalendar holidayCal)
        {
            var d = date.Date;
            if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                return false;

            var calendars = GetCalendarsForPair(ccyPair);
            // hämta bara denna dag
            var dt = holidayCal.GetHolidays(calendars, d, d);
            return dt == null || dt.Rows.Count == 0;
        }

        public static DateTime NextBusinessDay(string ccyPair, DateTime startDate, HolidayCalendar holidayCal,
                                               bool includeStart = true, int lookaheadDays = 370)
        {
            var calendars = GetCalendarsForPair(ccyPair);
            DateTime start = startDate.Date;
            DateTime from = start;
            DateTime to = start.AddDays(Math.Max(lookaheadDays, 1));

            var holidays = holidayCal.GetHolidays(calendars, from, to);
            var holidaySet = BuildHolidaySet(holidays);

            DateTime d = includeStart ? start : start.AddDays(1);
            int safety = 0;
            while (safety++ < lookaheadDays + 2)
            {
                if (!IsWeekend(d) && !holidaySet.Contains(d))
                    return d;
                d = d.AddDays(1);
            }
            throw new InvalidOperationException("NextBusinessDay: ingen dag hittades inom lookahead.");
        }

        public static DateTime PreviousBusinessDay(string ccyPair, DateTime startDate, HolidayCalendar holidayCal,
                                                   bool includeStart = true, int lookbackDays = 370)
        {
            var calendars = GetCalendarsForPair(ccyPair);
            DateTime start = startDate.Date;
            DateTime from = start.AddDays(-Math.Max(lookbackDays, 1));
            DateTime to = start;

            var holidays = holidayCal.GetHolidays(calendars, from, to);
            var holidaySet = BuildHolidaySet(holidays);

            DateTime d = includeStart ? start : start.AddDays(-1);
            int safety = 0;
            while (safety++ < lookbackDays + 2)
            {
                if (!IsWeekend(d) && !holidaySet.Contains(d))
                    return d;
                d = d.AddDays(-1);
            }
            throw new InvalidOperationException("PreviousBusinessDay: ingen dag hittades inom lookback.");
        }

        // ---- helpers ----
        private static bool IsWeekend(DateTime d)
        {
            var w = d.DayOfWeek;
            return w == DayOfWeek.Saturday || w == DayOfWeek.Sunday;
        }

        private static HashSet<DateTime> BuildHolidaySet(DataTable holidays)
        {
            var set = new HashSet<DateTime>();
            if (holidays == null) return set;

            foreach (DataRow r in holidays.Rows)
            {
                object val = r["HolidayDate"]; // kolumnnamn enligt HolidayCalendar.GetHolidays
                if (val is DateTime)
                    set.Add(((DateTime)val).Date);
            }
            return set;
        }
    }
}
