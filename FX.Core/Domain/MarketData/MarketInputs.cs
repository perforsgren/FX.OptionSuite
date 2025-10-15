using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Samlat paket med spot/rd/rf som "sided quotes" + låsregel.
    /// Detta är det minsta paketet prismotorn behöver för att räkna premien,
    /// innan DF/forward/swap-beräkningar kopplas på i kommande steg.
    /// 
    /// Använd ToEffectiveSided(useMid) för att:
    /// - tvinga mid-läge (bid=ask=mid) när UI väljer det,
    /// - validera sided-konsistens (bid ≤ ask),
    /// - skapa en defensiv kopia för prissning.
    /// </summary>
    public sealed class MarketInputs
    {
        /// <summary>Spot-quote för paren (valfritt i detta steg; många system har spot på toppnivå).</summary>
        public SidedQuote Spot { get; set; }

        /// <summary>Domestic rate (rd) som sided quote.</summary>
        public SidedQuote Rd { get; set; }

        /// <summary>Foreign rate (rf) som sided quote.</summary>
        public SidedQuote Rf { get; set; }

        /// <summary>Låsregel som används när användaren editerar forward/swap och vi måste back-solvera rd/rf.</summary>
        public LockMode LockMode { get; set; } = LockMode.HoldRd;

        /// <summary>
        /// Skapar en defensiv, "effektiv" kopia för prisning.
        /// Om useMid=true tvingas bid=ask=mid på samtliga fält som har Mid.
        /// </summary>
        public MarketInputs ToEffectiveSided(bool useMid)
        {
            var eff = new MarketInputs
            {
                Spot = this.Spot != null ? (useMid ? this.Spot.AsMidSided() : this.Spot.Clone()) : null,
                Rd = this.Rd != null ? (useMid ? this.Rd.AsMidSided() : this.Rd.Clone()) : null,
                Rf = this.Rf != null ? (useMid ? this.Rf.AsMidSided() : this.Rf.Clone()) : null,
                LockMode = this.LockMode
            };

            // Validera sided-konsistens innan vi skickar vidare:
            if (eff.Spot != null) eff.Spot.ValidateSidedOrThrow("Spot");
            if (eff.Rd != null) eff.Rd.ValidateSidedOrThrow("rd");
            if (eff.Rf != null) eff.Rf.ValidateSidedOrThrow("rf");

            return eff;
        }
    }
}
