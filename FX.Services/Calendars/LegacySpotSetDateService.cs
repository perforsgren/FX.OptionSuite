using System;
using System.Data;
using System.Globalization;
using FX.Core;
using FX.Core.Interfaces;
using FX.Infrastructure.Calendars.Legacy; // CurrencyCalendarMapper, HolidayCalendar, DateConvention, HolidaySet

namespace FX.Infrastructure
{
    /// <summary>
    /// ISpotSetDateService som använder din legacy DateConvention exakt:
    /// - SpotDate/Delivery med T+lag enligt par (inkl. USD-special)
    /// - Expiry-explicit: rullning “M/Y-regel” (tillåt holiday, ej lör/sön/1 jan)
    /// - Helgdagar hämtas från din DB via HolidayCalendar
    /// </summary>
    public sealed class LegacySpotSetDateService : ISpotSetDateService
    {
        private readonly ICalendarResolver _resolver;
        private readonly string _connectionString;

        public LegacySpotSetDateService(ICalendarResolver resolver, string connectionString)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public SpotSetDates Compute(string pair6, DateTime today, DateTime expiry)
        {
            // 1) Vilka kalendrar gäller för paret?
            var calendars = _resolver.CalendarsForPair(pair6) ?? Array.Empty<string>();

            // 2) Läs in helgdagar för en rimlig range runt “idag..expiry”
            var from = new DateTime(Math.Min(today.Ticks, expiry.Ticks)).AddYears(-1);
            var to = new DateTime(Math.Max(today.Ticks, expiry.Ticks)).AddYears(+3);

            var hc = new HolidayCalendar(_connectionString);
            DataTable holidays = hc.GetHolidays(calendars, from, to); // din metod

            // 3) Kör legacy-konventionen
            var dc = new DateConvention(pair6, holidays);

            // Explicit expiry: DateConvention accepterar ISO-datumsträng
            var conv = dc.GetConvention(expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            // 4) Returnera
            var outp = new SpotSetDates
            {
                SpotDate = conv.SpotDate,
                SettlementDate = conv.DeliveryDate,
                // Vi beräknar lag i BD bara som info (kan skilja i USD-fall)
                SpotLagBusinessDays = CountBusinessDays(today, conv.SpotDate, holidays)
            };
            return outp;
        }

        // Räknar business days mellan två datum med din HolidaySet (union av kalendrar)
        private static int CountBusinessDays(DateTime start, DateTime end, DataTable unionHolidays)
        {
            if (end <= start) return 0;
            var hs = new HolidaySet(unionHolidays); // unionen
            int n = 0; var d = start;
            while (d < end)
            {
                d = d.AddDays(1);
                if (!HolidaySet.IsWeekend(d) && !hs.IsNonBusinessDay(d)) n++;
            }
            return n;
        }
    }
}
