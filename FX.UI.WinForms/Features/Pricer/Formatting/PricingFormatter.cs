using System;

namespace FX.UI.WinForms
{
    public sealed class PricingFormatter
    {
        public enum DisplayCcy { Quote, Base }

        private readonly string _quoteCcy;   // "SEK", "JPY", ...
        private readonly DisplayCcy _ccy;

        public PricingFormatter(string quoteCcy, DisplayCcy ccy)
        {
            _quoteCcy = quoteCcy ?? "";
            _ccy = ccy;
        }

        public LegPricingDisplay Build(
            string leg,
            double? perUnitBid, double? perUnitAsk,  // per-unit (kan vara singel → mid)
            double notional,
            double spot,
            int sideSign,                             // +1 Buy, −1 Sell
            double deltaUnit, double vegaUnit, double gammaUnit, double thetaUnit,
            bool boldAsk)
        {
            // 1) Per-unit (singel → mid)
            double b = perUnitBid.HasValue ? perUnitBid.Value : (perUnitAsk ?? 0.0);
            double a = perUnitAsk.HasValue ? perUnitAsk.Value : b;
            double mid = 0.5 * (b + a);

            // 2) Pips & Percent – AVRUNDAD visning (driver totals)
            double pipSize = GetPipSize(_quoteCcy);
            double pipsMid = (pipSize > 0.0) ? mid / pipSize : 0.0;
            double pipsRounded1 = Math.Round(pipsMid, 1, MidpointRounding.AwayFromZero);

            double percentMid = PercentFromPerUnit(mid, notional, spot);
            double percentRounded4 = Math.Round(percentMid, 4, MidpointRounding.AwayFromZero);

            // 3) Totals i aktiv displayvaluta – styrs av AVRUNDAD visning
            //    QUOTE: runda pips → per unit → total
            //    BASE : runda %    → total base
            double totBidDisp, totAskDisp;
            if (_ccy == DisplayCcy.Quote)
            {
                totBidDisp = TotalFromRoundedPips(b, pipSize, notional, sideSign);
                totAskDisp = TotalFromRoundedPips(a, pipSize, notional, sideSign);
            }
            else
            {
                totBidDisp = TotalFromRoundedPercent(b, notional, spot, sideSign);
                totAskDisp = TotalFromRoundedPercent(a, notional, spot, sideSign);
            }

            // 4) Risk – positionsnivå + avrundning (samma känsla som nu)
            double posDelta = sideSign * deltaUnit * notional;
            double posVega = sideSign * vegaUnit * notional;
            double posGamma = sideSign * gammaUnit * notional * spot * 0.01; // cash @1% i base
            double posTheta = thetaUnit * notional * sideSign;

            var vm = new LegPricingDisplay
            {
                Leg = leg,

                PremUnitBid = b,
                PremUnitAsk = a,

                PremTotalBid = totBidDisp,
                PremTotalAsk = totAskDisp,

                PipsRounded1 = pipsRounded1,
                PercentRounded4 = percentRounded4,

                DeltaPos = RoundToStep(posDelta, 10000.0),
                DeltaPctRounded1 = Math.Round(
                    (Math.Abs(notional) > 0.0 ? (posDelta / notional) * 100.0 : 0.0), 1, MidpointRounding.AwayFromZero),
                VegaPosRounded100 = RoundToStep(posVega, 100.0),
                GammaPosRounded100 = RoundToStep(posGamma, 100.0),
                ThetaPos = Math.Round(posTheta, 2, MidpointRounding.AwayFromZero),

                BoldAsk = boldAsk
            };

            return vm;
        }

        // === Helpers ===

        private static double GetPipSize(string quote)
        {
            if (string.Equals(quote, "JPY", StringComparison.OrdinalIgnoreCase)) return 0.01;
            return 0.0001;
        }

        private static double PercentFromPerUnit(double perUnit, double N, double S)
        {
            double denomAbs = Math.Abs(N * S);
            if (denomAbs <= 0.0) return 0.0;
            return (perUnit * N) / denomAbs * 100.0;
        }

        private static double TotalFromRoundedPips(double perUnit, double pip, double N, int sg)
        {
            if (pip <= 0.0) return 0.0;
            // 1) pips → 1 d.p.
            double pips = perUnit / pip;
            double pipsRounded1 = Math.Round(pips, 1, MidpointRounding.AwayFromZero);
            // 2) tillbaka till per-unit
            double perUnitRounded = pipsRounded1 * pip;
            // 3) total i quote
            return perUnitRounded * N * sg;
        }

        private static double TotalFromRoundedPercent(double perUnit, double N, double S, int sg)
        {
            // 1) % från per unit
            double pct = PercentFromPerUnit(perUnit, N, S);
            // 2) runda 4 d.p.
            double pctRounded4 = Math.Round(pct, 4, MidpointRounding.AwayFromZero);
            // 3) total i BASE: (%/100)*|N| * side
            double baseAbs = Math.Abs(N);
            return ((baseAbs > 0.0 ? (pctRounded4 / 100.0) * baseAbs : 0.0) * sg);
        }

        private static double RoundToStep(double x, double step)
        {
            if (step <= 0.0) return x;
            return Math.Round(x / step, 0, MidpointRounding.AwayFromZero) * step;
        }
    }
}
