using FX.Infrastructure;
using System;
using Microsoft.Extensions.DependencyInjection;
using CoreI = FX.Core.Interfaces;
using FX.Infrastructure.VolDb;
using FxTradeHub.Services;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Data.MySql.Repositories;

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

            // Blotter read service – kan vara Singleton eftersom den bara är ett tunt lager över IStpRepository
            services.AddSingleton<IBlotterReadService, BlotterReadService>();

            // Asynkron blotter-lässervice för async presenter
            services.AddSingleton<IBlotterReadServiceAsync, BlotterReadServiceAsync>();

            var tradeStpConnectionString = BuildTradeStpConnectionString();

            // STP-repositories (synkrona)
            services.AddSingleton<IStpRepository>(_ => new MySqlStpRepository(tradeStpConnectionString));
            services.AddSingleton<IStpLookupRepository>(_ => new MySqlStpLookupRepository(tradeStpConnectionString));

            // STP-repository (asynkront) – används av blotterns async read-path
            services.AddSingleton<IStpRepositoryAsync>(_ => new MySqlStpRepositoryAsync(tradeStpConnectionString));

            // Volatility read & write
            services.AddSingleton<CoreI.IVolRepository>(_ => new MySqlVolRepository(fxvolConn));
            services.AddSingleton<CoreI.IVolWriteRepository>(_ => new MySqlVolWriteRepository(fxvolConn));

            return services;
        }

        /// <summary>
        /// Bygger connection string för trade_stp-databasen.
        /// </summary>
        private static string BuildTradeStpConnectionString()
        {
            string username = "fxopt";
            string password = "fxopt987";

            return
                "Server=srv78506;Port=3306;Database=trade_stp;" +
                "User Id=" + username + ";" +
                "Password=" + password + ";" +
                "Connection Timeout=15;TreatTinyAsBoolean=false;";
        }


    }
}
