using System;
using System.Data;
using System.Globalization;
using FX.Core;
using FX.Core.Interfaces;
using FX.Infrastructure.Calendars.Legacy; // ExpiryInputResolver, CurrencyCalendarMapper, HolidayCalendar, HolidaySet

namespace FX.Infrastructure
{
    /// Wrappar din ExpiryInputResolver + DateConvention med DB-holidays.
    public sealed class LegacyExpiryInputResolverAdapter : IExpiryInputResolver
    {
        private readonly ICalendarResolver _resolver;
        private readonly string _connectionString;

        public LegacyExpiryInputResolverAdapter(ICalendarResolver resolver, string connectionString)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public ExpiryResolution Resolve(string rawInput, string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6) || pair6.Replace("/", "").Trim().Length != 6)
                throw new ArgumentException("pair6 måste vara t.ex. EURSEK eller EUR/SEK.");

            var calendars = _resolver.CalendarsForPair(pair6) ?? Array.Empty<string>();

            // Läs holidays runt nuvarande horisont (räcker långt i praktiken)
            var today = DateTime.Today;
            var from = today.AddYears(-1);
            var to = today.AddYears(+3);

            var hc = new HolidayCalendar(_connectionString);
            DataTable holidays = hc.GetHolidays(calendars, from, to);

            // Anropa din resolver
            var r = ExpiryInputResolver.Resolve(rawInput, pair6, holidays);

            return new ExpiryResolution
            {
                ExpiryDate = r.ExpiryDate,
                SettlementDate = r.SettlementDate,
                ExpiryIso = r.ExpiryIso,
                SettlementIso = r.SettlementIso,
                Mode = r.Mode,
                Normalized = r.Normalized,
                ExpiryWeekday = r.ExpiryWeekday
            };
        }
    }
}
