namespace FX.Core.Domain
{
    /// <summary>En vol-punkt i smile/termstruktur (ex. tenor=1M, label=25D, vol=0.12).</summary>
    public sealed class VolNode
    {
        public string Tenor { get; }       // "1W","1M","3M","1Y","ATM"
        public string Label { get; }       // "ATM","25D","10D","RR","BF" (enkelt i steg 2)
        public double Volatility { get; }  // i decimaltal (ex. 0.12)

        public VolNode(string tenor, string label, double vol)
        {
            Tenor = (tenor ?? "").ToUpperInvariant();
            Label = (label ?? "").ToUpperInvariant();
            Volatility = vol;
        }

        public override string ToString() => Tenor + ":" + Label + "=" + Volatility.ToString("0.####");
    }
}
