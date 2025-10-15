// FX.Messages/Commands/RequestPrice.TwoWay.cs
using System;
using FX.Core.Domain;

namespace FX.Messages.Commands
{
    /// <summary>
    /// Partial som kompletterar befintlig RequestPrice med:
    /// - VolBidPct/VolAskPct (i PROCENT från UI, t.ex. 5 = 5%)
    /// - SpotBidOverride/SpotAskOverride (tvåvägs spot från UI; om UI har ett mid duplicerar presentern)
    /// - Hjälpare för att sätta domänens Vol (i DECIMAL, t.ex. 0.05)
    ///
    /// OBS:
    /// - Vi ändrar inte din originalklass.
    /// - Själva skapandet av Domain.PricingRequest (inkl. SpotBid/SpotAsk) görs i mappen/handlern.
    ///   Den här partialen exponerar bara indata + sätter Vol på ett redan byggt domänobjekt.
    /// </summary>
    public sealed partial class RequestPrice
    {
        /// <summary>Vol-bid i PROCENT (ex: 5.0 = 5%). Lämna null för en-sidig vol.</summary>
        public double? VolBidPct { get; set; }

        /// <summary>Vol-ask i PROCENT (ex: 6.0 = 6%). Lämna null för en-sidig vol.</summary>
        public double? VolAskPct { get; set; }

        /// <summary>Spot-bid override från UI. Om UI bara har ett mid, duplicerar presentern till båda sidor.</summary>
        public double? SpotBidOverride { get; set; }

        /// <summary>Spot-ask override från UI. Om UI bara har ett mid, duplicerar presentern till båda sidor.</summary>
        public double? SpotAskOverride { get; set; }

        /// <summary>
        /// Sätter Vol (tvåvägsvol i DECIMAL) på ett redan byggt domän-PricingRequest.
        /// Använd när mappen redan har skapat PricingRequest med Pair/Legs/SpotBid/SpotAsk/Rd/Rf/SurfaceId/StickyDelta.
        /// </summary>
        public PricingRequest ApplyVolToDomain(PricingRequest baseRequest)
        {
            if (baseRequest == null) throw new ArgumentNullException(nameof(baseRequest));
            baseRequest.SetVol(BuildVolOrThrow());
            return baseRequest;
        }

        /// <summary>
        /// Konverterar Pct-fälten till decimal och validerar att Bid ≤ Ask när båda finns.
        /// Minst en sida måste vara satt.
        /// </summary>
        private VolQuote BuildVolOrThrow()
        {
            double? bidDec = VolBidPct.HasValue ? VolBidPct.Value / 100.0 : (double?)null;
            double? askDec = VolAskPct.HasValue ? VolAskPct.Value / 100.0 : (double?)null;

            if (!bidDec.HasValue && !askDec.HasValue)
                throw new ArgumentException("RequestPrice saknar vol: sätt VolBidPct och/eller VolAskPct.");

            return new VolQuote(bidDec, askDec); // Validering (Bid ≤ Ask) sker i VolQuote.
        }
    }
}
