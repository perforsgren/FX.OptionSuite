using FX.Core.Interfaces;
using FX.Infrastructure.Calendars.Legacy; // CurrencyCalendarMapper

namespace FX.Infrastructure
{
    /// Använder din CurrencyCalendarMapper (EUR→TARGET, SEK→SWEDEN, etc.)
    public sealed class LegacyCalendarResolverAdapter : ICalendarResolver
    {
        public string[] CalendarsForPair(string pair6)
            => CurrencyCalendarMapper.GetCalendarsForPair(pair6);
    }
}
