using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Källa för ett fält (feed eller manuellt). Används i merge-pipelinen och UI-indikatorer.
    /// </summary>
    public enum QuoteSource
    {
        Unknown = 0,
        Feed = 1,
        User = 2,
    }

    /// <summary>
    /// Låsregel vid back-solve (används när man ändrar forward/swap och måste lösa rd/rf).
    /// HoldRd = håll domestic-räntan och lös rf; HoldRf = håll foreign och lös rd; Split = justera båda.
    /// </summary>
    public enum LockMode
    {
        HoldRd = 0,
        HoldRf = 1,
        Split = 2,
    }

    /// <summary>
    /// Sided quote för tal (t.ex. rd, rf, spot): bär bid/ask samt mid/spread för UI-redigering.
    /// - Mid/Spread används för redigering (ändra mid eller spread och deriviera sidorna).
    /// - Bid/Ask används i prisning (prismotorn konsumerar sided-data).
    /// - Source/IsOverride styr feed vs. manuell prioritet.
    /// </summary>
    public sealed class SidedQuote
    {
        /// <summary>Bid-sida (effektiv sida som används i prisning).</summary>
        public double? Bid { get; set; }

        /// <summary>Ask-sida (effektiv sida som används i prisning).</summary>
        public double? Ask { get; set; }

        /// <summary>Mid för UI. Använd <see cref="RebuildSidesFromMidAndSpread"/> för att få sidorna.</summary>
        public double? Mid { get; set; }

        /// <summary>Spreadbredd i samma enhet som Bid/Ask (ränta i absolut tal, ej nödvändigtvis bp).</summary>
        public double? Spread { get; set; }

        /// <summary>Källa: Feed eller User.</summary>
        public QuoteSource Source { get; set; }

        /// <summary>True om användaren manuellt satt värdet (feed ska då inte skriva över).</summary>
        public bool IsOverride { get; set; }

        /// <summary>
        /// Skapar en kopia där bid=ask=mid (mid-läge). Används när UI/policy kräver ren "mid-prisning".
        /// </summary>
        public SidedQuote AsMidSided()
        {
            if (!Mid.HasValue)
                throw new InvalidOperationException("Kan inte tvinga mid-läge: Mid saknas.");

            return new SidedQuote
            {
                Bid = Mid,
                Ask = Mid,
                Mid = Mid,
                Spread = 0.0,
                Source = this.Source,
                IsOverride = this.IsOverride
            };
        }

        /// <summary>
        /// Härleder Bid/Ask = Mid ± Spread/2. Använd efter att användaren ändrat Mid eller Spread i UI.
        /// Bevarar monotoni (bid ≤ ask) och kastar undantag om spread leder till fel ordning.
        /// </summary>
        public void RebuildSidesFromMidAndSpread()
        {
            if (!Mid.HasValue || !Spread.HasValue)
                throw new InvalidOperationException("Mid och Spread krävs för att härleda Bid/Ask.");

            var half = Spread.Value / 2.0;
            Bid = Mid.Value - half;
            Ask = Mid.Value + half;

            if (Bid.Value > Ask.Value)
                throw new InvalidOperationException("Monotoni bruten efter härledning (Bid > Ask). Kontrollera spreadtecken.");
        }

        /// <summary>
        /// Basvalidering för sided-data (kräver att både Bid och Ask finns samt att Bid ≤ Ask).
        /// Kalla detta innan värden skickas till prismotor.
        /// </summary>
        public void ValidateSidedOrThrow(string name)
        {
            if (!Bid.HasValue || !Ask.HasValue)
                throw new InvalidOperationException($"{name}: Bid/Ask saknas.");

            if (Bid.Value > Ask.Value)
                throw new InvalidOperationException($"{name}: Bid ({Bid}) får inte vara större än Ask ({Ask}).");
        }

        /// <summary>
        /// Skapar en enkel klon. Mid/Spread/metadata följer med för UI-syften.
        /// </summary>
        public SidedQuote Clone()
        {
            return new SidedQuote
            {
                Bid = this.Bid,
                Ask = this.Ask,
                Mid = this.Mid,
                Spread = this.Spread,
                Source = this.Source,
                IsOverride = this.IsOverride
            };
        }
    }
}
