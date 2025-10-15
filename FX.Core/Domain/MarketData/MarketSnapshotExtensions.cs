using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Extensions för att bygga "effektiva" marknadsindata (sided eller mid-forced)
    /// från din befintliga MarketSnapshot + ett indata-paket (UI/feed).
    /// 
    /// Fördel: Vi ändrar inte MarketSnapshot-klassens kod direkt (minimalt intrång),
    /// men får en naturlig plats att skapa prisningsklara inputs.
    /// </summary>
    public static class MarketSnapshotExtensions
    {
        /// <summary>
        /// Bygger ett prisningsklart paket (MarketInputs) från ett "rått" inputs-paket,
        /// normaliserat enligt "useMid".
        /// 
        /// - useMid=false: använd befintliga Bid/Ask på Spot/rd/rf (sided-prisning).
        /// - useMid=true : tvinga bid=ask=mid där Mid finns (rent mid-läge).
        /// 
        /// Not:
        ///  - Denna metod rör inte övriga fält i MarketSnapshot; vi bygger en isolerad payload.
        ///  - DF/forward/swap-beräkningar kommer i nästa steg (egen service).
        /// </summary>
        /// <param name="snapshot">Aktuell MarketSnapshot (används för tidsstämplar/konventioner i nästa steg).</param>
        /// <param name="raw">Råa UI/feed-värden för Spot/rd/rf.</param>
        /// <param name="useMid">True för rent mid-läge (bid=ask=mid), annars sided.</param>
        public static MarketInputs BuildEffectiveMarketInputs(this MarketSnapshot snapshot, MarketInputs raw, bool useMid)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            // 1) Normalisera till ett "effektivt" paket (mid-forced eller sided).
            var eff = raw.ToEffectiveSided(useMid);

            // 2) (Framöver) Här kan vi injicera snapshot-baserad metadata (kalendrar/daycount/valutadag m.m.)
            //    till en separat DF-/forward-/swaps-service. Vi låter denna metod vara "ren"
            //    och returnera enbart sided inputs som prismotorn kan konsumera direkt.
            //
            //    Exempel (i nästa steg):
            //      var dfFwd = _fxCurveCalc.Build(snapshot, eff);
            //      -> returnera antingen enriched payload eller låta prismotorn konsumera "eff" + dfFwd.

            return eff;
        }
    }
}
