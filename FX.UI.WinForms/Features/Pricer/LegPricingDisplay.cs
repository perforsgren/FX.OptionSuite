namespace FX.UI.WinForms
{
    public sealed class LegPricingDisplay
    {
        public string Leg { get; set; }

        // Per unit – tvåväg (visning/overlay)
        public double PremUnitBid { get; set; }
        public double PremUnitAsk { get; set; }
        public int PremUnitDecimals { get; set; } = 6;

        // Totals i aktiv displayvaluta – tvåväg (visning/overlay)
        public double PremTotalBid { get; set; }
        public double PremTotalAsk { get; set; }
        public int PremTotalDecimals { get; set; } = 2;

        // Härledda VISNINGSvärden (från avrundade regler)
        public double PipsRounded1 { get; set; }        // 1 d.p. (från PU mid)
        public double PercentRounded4 { get; set; }     // 4 d.p. (från PU mid)

        // Risk – positionsnivå/visning
        public double DeltaPos { get; set; }            // avrundad i “K”-steg (som i din vy)
        public double DeltaPctRounded1 { get; set; }    // 1 d.p.
        public double VegaPosRounded100 { get; set; }   // till närmaste 100
        public double GammaPosRounded100 { get; set; }  // till närmaste 100
        public double ThetaPos { get; set; }            // 2 d.p.

        // För bold-regeln (Buy → Ask bold)
        public bool BoldAsk { get; set; }
    }
}
