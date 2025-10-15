using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class CalendarResolverDefault : ICalendarResolver
    {
        public string[] CalendarsForPair(string pair6)
        {
            // Fallback – namnen spelar ingen roll för weekend-only.
            // När vi byter till DB-kalender används riktiga kalendrar.
            return new[] { "WEEKEND" };
        }
    }
}
