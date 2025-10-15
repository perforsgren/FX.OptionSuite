namespace FX.Core.Domain
{
    /// <summary>
    /// Resultat med tre premiumsidor och greker beräknade på Mid-vol.
    /// Premium-värden anges i prisvalutan enligt dina övriga konventioner.
    /// </summary>
    public sealed class TwoSidedPriceResult
    {
        public double PremiumBid { get; }
        public double PremiumMid { get; }
        public double PremiumAsk { get; }

        public double Delta { get; }
        public double Gamma { get; }
        public double Vega { get; }
        public double Theta { get; }

        public TwoSidedPriceResult(
            double premiumBid,
            double premiumMid,
            double premiumAsk,
            double delta,
            double gamma,
            double vega,
            double theta)
        {
            PremiumBid = premiumBid;
            PremiumMid = premiumMid;
            PremiumAsk = premiumAsk;
            Delta = delta;
            Gamma = gamma;
            Vega = vega;
            Theta = theta;
        }
    }
}
