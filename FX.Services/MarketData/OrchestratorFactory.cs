// C# 7.3
using System;
using FX.Core.Domain.MarketData;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Fabrik som skapar PricingOrchestrator med en callback som
    /// hämtar rd/rf via UsdAnchoredRateFeeder (autofetch).
    /// </summary>
    public static class OrchestratorFactory
    {
        /// <summary>
        /// Skapar en PricingOrchestrator kopplad till angivet MarketStore.
        /// Autofetch-callbacken använder forceRefresh=false (cache) som standard.
        /// </summary>
        public static PricingOrchestrator Create(IMarketStore marketStore)
        {
            if (marketStore == null) throw new ArgumentNullException(nameof(marketStore));

            // Callback: anropas av Orchestrator när rd/rf saknas.
            Action<string, string, DateTime, DateTime, DateTime> ensureRdRf =
                (p6, leg, today, spot, set) =>
                {
                    using (var feeder = new UsdAnchoredRateFeeder(marketStore))
                    {
                        // Vid autofetch (t.ex. tenor-/datumbyte) använder vi cache → forceRefresh=false.
                        feeder.EnsureRdRfFor(p6, leg, today, spot, set, forceRefresh: false);
                    }
                };

            return new PricingOrchestrator(marketStore, ensureRdRf);
        }
    }
}
