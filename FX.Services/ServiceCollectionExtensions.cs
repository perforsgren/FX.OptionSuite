using FX.Infrastructure;
using System;
using Microsoft.Extensions.DependencyInjection;
using CoreI = FX.Core.Interfaces;

namespace FX.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddFxServices(this IServiceCollection services)
        {
            // Bus + AppState
            services.AddSingleton<CoreI.IMessageBus, InProcMessageBus>();
            services.AddSingleton<AppStateStore>();

            // Kalenderdel (byt ut resolver + calendar när du kopplar DB i Infrastructure)
            //services.AddSingleton<CoreI.ICalendarResolver, CalendarResolverDefault>();
            //services.AddSingleton<CoreI.IBusinessCalendar, WeekendOnlyBusinessCalendar>();
            //services.AddSingleton<CoreI.ISpotSetDateService, SpotSetDateService>();

            // Kalender via din DB
            services.AddSingleton<CoreI.ICalendarResolver, LegacyCalendarResolverAdapter>();
            var conn = Environment.GetEnvironmentVariable("AHS_SQL_CONN")
                       ?? "Data Source=AHSKvant-prod-db;Initial Catalog=AHS;Integrated Security=True;Connect Timeout=15";
            services.AddSingleton<CoreI.IBusinessCalendar>(sp => new DbBusinessCalendar(conn));
            services.AddSingleton<CoreI.ISpotSetDateService>(sp => new LegacySpotSetDateService(sp.GetRequiredService<CoreI.ICalendarResolver>(), conn));

            // NY: Tenor/Datum-parser (UI-input -> riktiga datum)
            services.AddSingleton<CoreI.IExpiryInputResolver>(sp => new LegacyExpiryInputResolverAdapter(sp.GetRequiredService<CoreI.ICalendarResolver>(), conn));

            // Vol/Engine (MVP)
            services.AddSingleton<CoreI.IVolInterpolator, FlatVolInterpolator>(); // <— ny klass nedan
            services.AddSingleton<CoreI.IVolService, VolService>();
            services.AddSingleton<CoreI.IDayWeightService, DayWeightService>();
            //services.AddSingleton<CoreI.IPriceEngine, SimplePriceEngine>();
            services.AddSingleton<CoreI.IPriceEngine, LegacyPriceEngineAdapter>(); // din riktiga

            // Runtime (subscribar på RequestPrice)
            services.AddSingleton<FxRuntime>();

            return services;
        }
    }
}
