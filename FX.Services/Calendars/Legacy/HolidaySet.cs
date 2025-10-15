using System;
using System.Collections.Generic;
using System.Data;

namespace FX.Infrastructure.Calendars.Legacy
{
    /// <summary>
    /// Snabb uppslagning av helgdagar via HashSet. Byggs från en Holidays-DataTable.
    /// </summary>
    public sealed class HolidaySet
    {
        private readonly HashSet<DateTime> _dates;

        /// <param name="holidays">DataTable med kolumner "Market" (string) och "HolidayDate" (DateTime)</param>
        /// <param name="marketsFilter">Om angivet: filtrera på dessa market-namn (case-insensitive)</param>
        public HolidaySet(DataTable holidays, IEnumerable<string> marketsFilter = null)
        {
            _dates = new HashSet<DateTime>();
            if (holidays == null || holidays.Rows.Count == 0) return;

            HashSet<string> filter = null;
            if (marketsFilter != null)
            {
                filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in marketsFilter) if (!string.IsNullOrWhiteSpace(m)) filter.Add(m.Trim());
            }

            foreach (DataRow r in holidays.Rows)
            {
                var market = Convert.ToString(r["Market"]);
                if (filter != null && market != null && !filter.Contains(market)) continue;

                var dtObj = r["HolidayDate"];
                if (dtObj is DateTime)
                    _dates.Add(((DateTime)dtObj).Date);
            }
        }

        /// <summary>True om datumet finns i holiday-tabellen (utan helgkontroll).</summary>
        public bool IsHoliday(DateTime date) => _dates.Contains(date.Date);

        /// <summary>True om datumet är lör/sön.</summary>
        public static bool IsWeekend(DateTime date)
        {
            var d = date.DayOfWeek;
            return d == DayOfWeek.Saturday || d == DayOfWeek.Sunday;
        }

        /// <summary>True om datumet inte är bankdag (helg eller holiday).</summary>
        public bool IsNonBusinessDay(DateTime date) => IsWeekend(date) || IsHoliday(date);
    }
}
