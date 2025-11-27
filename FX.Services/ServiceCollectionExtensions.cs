using FX.Infrastructure;
using System;
using Microsoft.Extensions.DependencyInjection;
using CoreI = FX.Core.Interfaces;
using FX.Infrastructure.VolDb;

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

            // Vol/Engine (MVP)    REMOVE LATER
            services.AddSingleton<CoreI.IVolInterpolator, FlatVolInterpolator>(); 
            services.AddSingleton<CoreI.IVolService, VolService>();

            services.AddSingleton<CoreI.IPriceEngine, LegacyPriceEngineAdapter>(); 

            // Runtime (subscribar på RequestPrice)
            services.AddSingleton<FxRuntime>();


            string username = "fxopt";
            string password = "fxopt987";

            // Rekommenderad MySQL-sträng (ersätt "fxvol" med din faktiska DB, t.ex. "fxoptions" om det är den du använder)
            var fxvolConn =
                "Server=srv78506;Port=3306;Database=fxvol;" +
                "User Id=" + username + ";" +
                "Password=" + password + ";" +
                "Connection Timeout=15;SslMode=None;TreatTinyAsBoolean=false;";

            // Read
            services.AddSingleton<CoreI.IVolRepository>(_ => new MySqlVolRepository(fxvolConn));
            // Write
            services.AddSingleton<CoreI.IVolWriteRepository>(_ => new MySqlVolWriteRepository(fxvolConn));

            return services;
        }
    }
}
