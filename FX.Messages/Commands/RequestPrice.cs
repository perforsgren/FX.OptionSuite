// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  UI ber om prissättning asynkront och väntar på PriceCalculated.
// Vad:     Innehåller pair, ben, overrides och korrelations-id för svaret.
// Klar när:Presenter i UI kan posta denna via IMessageBus.
// ============================================================
using System;
using System.Collections.Generic;
using FX.Messages.Dtos;

namespace FX.Messages.Commands
{
    /// <summary>Command: be om prissättning (en eller flera ben).</summary>
    public sealed partial class RequestPrice
    {
        public string Pair6 { get; set; }

        // Overrides kan vara null -> användas som "ta från marketdata/AppState"
        public double? SpotOverride { get; set; }

        //public double? SpotBidOverride { get; set; }   // om satt: använd som Spot bid
        //public double? SpotAskOverride { get; set; }   // om satt: använd som Spot ask

        public double? RdOverride { get; set; }
        public double? RfOverride { get; set; }

        public string SurfaceId { get; set; }
        public bool StickyDelta { get; set; }

        // Benen som ska prissättas
        public List<Leg> Legs { get; set; } = new List<Leg>();

        // För spårning i bus/logg
        public Guid CorrelationId { get; set; } = Guid.NewGuid();

        /// <summary>Nästlad typ för ett ben i affären.</summary>
        public sealed class Leg
        {
            public string Side { get; set; }      // "BUY"/"SELL"
            public string Type { get; set; }      // "CALL"/"PUT"
            public string Strike { get; set; }    // "11.00" eller "25D"
            public string ExpiryIso { get; set; } // "yyyy-MM-dd"
            public double Notional { get; set; }
        }
    }
}
