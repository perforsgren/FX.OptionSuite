namespace FX.Core.Domain
{
    /// <summary>Resultat från prissättningsmotorn (per struktur eller aggregerat).</summary>
    public sealed class PricerResult
    {
        public double Price { get; }
        public double Delta { get; }
        public double Vega { get; }
        public double Gamma { get; }
        public double Theta { get; }

        public PricerResult(double price, double delta, double vega, double gamma, double theta)
        {
            Price = price;
            Delta = delta;
            Vega = vega;
            Gamma = gamma;
            Theta = theta;
        }
    }
}
