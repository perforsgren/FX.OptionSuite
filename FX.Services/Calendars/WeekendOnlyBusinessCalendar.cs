using System;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class WeekendOnlyBusinessCalendar : IBusinessCalendar
    {
        public bool IsBusinessDay(string[] calendars, DateTime date)
        {
            var dow = date.DayOfWeek;
            return dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday;
        }

        public DateTime AddBusinessDays(string[] calendars, DateTime start, int n)
        {
            if (n <= 0) return start;
            int added = 0; var d = start;
            while (added < n)
            {
                d = d.AddDays(1);
                if (IsBusinessDay(calendars, d)) added++;
            }
            return d;
        }

        public int CountBusinessDaysForward(string[] calendars, DateTime start, DateTime end)
        {
            if (end <= start) return 0;
            int n = 0; var d = start;
            while (d < end)
            {
                d = d.AddDays(1);
                if (IsBusinessDay(calendars, d)) n++;
            }
            return n;
        }
    }
}
