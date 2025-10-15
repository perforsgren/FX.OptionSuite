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

            // Kalender via din DB
            services.AddSingleton<CoreI.ICalendarResolver, LegacyCalendarResolverAdapter>();
            var conn = Environment.GetEnvironmentVariable("AHS_SQL_CONN") ?? "Data Source=AHSKvant-prod-db;Initial Catalog=AHS;Integrated Security=True;Connect Timeout=15";
            services.AddSingleton<CoreI.IBusinessCalendar>(sp => new DbBusinessCalendar(conn));
            services.AddSingleton<CoreI.ISpotSetDateService>(sp => new LegacySpotSetDateService(sp.GetRequiredService<CoreI.ICalendarResolver>(), conn));

            // NY: Tenor/Datum-parser (UI-input -> riktiga datum)
            services.AddSingleton<CoreI.IExpiryInputResolver>(sp => new LegacyExpiryInputResolverAdapter(sp.GetRequiredService<CoreI.ICalendarResolver>(), conn));

            // Vol/Engine (MVP)
            services.AddSingleton<CoreI.IVolInterpolator, FlatVolInterpolator>(); 
            services.AddSingleton<CoreI.IVolService, VolService>();
            services.AddSingleton<CoreI.IDayWeightService, DayWeightService>();

            services.AddSingleton<CoreI.IPriceEngine, LegacyPriceEngineAdapter>(); 

            // Runtime (subscribar på RequestPrice)
            services.AddSingleton<FxRuntime>();

            return services;
        }
    }
}
