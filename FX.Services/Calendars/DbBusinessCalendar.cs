using System;
using System.Collections.Generic;
using FX.Core.Interfaces;
using FX.Infrastructure.Calendars.Legacy; // HolidayCalendar, HolidaySet

namespace FX.Infrastructure
{
    /// IBusinessCalendar-brygga som läser helgdagar från din DB via HolidayCalendar.
    public sealed class DbBusinessCalendar : IBusinessCalendar
    {
        private readonly string _conn;
        private readonly object _lock = new object();

        // cache per "TARGET+SWEDEN" etc
        private readonly Dictionary<string, HolidaySet> _cache = new Dictionary<string, HolidaySet>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Tuple<DateTime, DateTime>> _range = new Dictionary<string, Tuple<DateTime, DateTime>>(StringComparer.OrdinalIgnoreCase);

        public DbBusinessCalendar(string connectionString) { _conn = connectionString; }

        public bool IsBusinessDay(string[] calendars, DateTime date)
        {
            var set = GetSet(calendars, date.AddYears(-1), date.AddYears(1));
            var dow = date.DayOfWeek;
            if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday) return false;
            return !set.IsNonBusinessDay(date);
        }

        public DateTime AddBusinessDays(string[] calendars, DateTime start, int n)
        {
            if (n == 0) return start;
            int dir = Math.Sign(n);
            int left = Math.Abs(n);
            DateTime d = start;
            EnsureRange(calendars, start, start.AddYears(3)); // rimlig buffert
            var set = GetSet(calendars, start.AddYears(-1), start.AddYears(3));

            while (left > 0)
            {
                d = d.AddDays(dir);
                var dow = d.DayOfWeek;
                if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday || set.IsNonBusinessDay(d)) continue;
                left--;
            }
            return d;
        }

        public int CountBusinessDaysForward(string[] calendars, DateTime start, DateTime end)
        {
            if (end <= start) return 0;
            EnsureRange(calendars, start, end);
            var set = GetSet(calendars, start, end);
            int n = 0; var d = start;
            while (d < end)
            {
                d = d.AddDays(1);
                var dow = d.DayOfWeek;
                if (dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday && !set.IsNonBusinessDay(d)) n++;
            }
            return n;
        }

        // ---- intern hjälp ----
        private HolidaySet GetSet(string[] calendars, DateTime from, DateTime to)
        {
            string key = calendars == null ? "" : string.Join("+", calendars).ToUpperInvariant();
            lock (_lock)
            {
                Tuple<DateTime, DateTime> have;
                if (!_cache.ContainsKey(key) || !_range.TryGetValue(key, out have) || from < have.Item1 || to > have.Item2)
                {
                    var hc = new HolidayCalendar(_conn);
                    var dt = hc.GetHolidays(calendars, from, to); // DataTable "HolidayDate"/"Market"
                    _cache[key] = new HolidaySet(dt);
                    _range[key] = Tuple.Create(from, to);
                }
                return _cache[key];
            }
        }

        private void EnsureRange(string[] calendars, DateTime from, DateTime to)
        {
            // trigger GetSet för att säkerställa cache
            GetSet(calendars, from, to);
        }
    }
}
