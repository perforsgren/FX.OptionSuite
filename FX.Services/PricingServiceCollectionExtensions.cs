// C# 7.3
using System;
using Microsoft.Extensions.DependencyInjection;
using FX.Core.Domain.MarketData;
using FX.Services.MarketData;

namespace FX.Services
{
    /// <summary>
    /// Extra registrering för pris-orchestrering (autofetch via feeder).
    /// Separat från AddFxServices() för att undvika intrång i befintlig setup.
    /// </summary>
    public static class PricingServiceCollectionExtensions
    {
        /// <summary>
        /// Registrerar PricingOrchestrator (transient) och UsdAnchoredRateFeeder (transient).
        /// Förutsätter att IMarketStore redan registrerats av AddFxServices().
        /// </summary>
        public static IServiceCollection AddFxPricing(this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            // Feeder (on-demand)
            services.AddTransient<UsdAnchoredRateFeeder>();

            // Orchestrator (autofetch-callback via fabriken)
            services.AddTransient<PricingOrchestrator>(sp =>
            {
                var store = sp.GetRequiredService<IMarketStore>();
                return OrchestratorFactory.Create(store);
            });

            return services;
        }
    }
}
