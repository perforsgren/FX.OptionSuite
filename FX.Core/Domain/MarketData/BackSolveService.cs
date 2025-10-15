using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// BackSolveService
    /// ----------------
    /// Ansvar:
    /// - Back-solvera rd eller rf när användaren sätter Forward eller Swap (mid-värde).
    /// - Respektera deterministisk låsning:
    ///   * Om endast en av rd/rf har IsOverride = true → håll den och lös den andra.
    ///   * Annars styr LockMode (HoldRd / HoldRf / Split). (Split: enkel proportionell regel i log-DF kan läggas till vid behov.)
    /// - Bevara spreadbredden: lös MID först, sätt sedan BID/ASK = MID ± spread/2.
    /// - Använder legacy exponential discount (DF = exp(-r*T)) för forward-fönstret (spot→settlement),
    ///   med money-market-dagbas: domestic=quote, foreign=base enligt legacy.
    ///
    /// Exponering:
    /// - BackSolveFromForwardMid(...): mål-forward (mid).
    /// - BackSolveFromSwapMid(...): mål-swap (mid).
    ///
    /// Output:
    /// - Uppdaterade inputs (rd/rf) + ett färskt FxCurveResult (DF/F/Swaps) från FxCurveCalculator.BuildLegacyExp(..).
    /// </summary>
    public sealed class BackSolveService
    {
        private readonly FxCurveCalculator _calc = new FxCurveCalculator();

        /// <summary>
        /// Back-solvera rd eller rf från ett målvärde på Forward (mid).
        /// - Om precis en av rd/rf är override → håll den och lös den andra.
        /// - Annars följ inputs.LockMode (HoldRd/HoldRf/Split).
        /// - Bevarar spread (mid först).
        /// </summary>
        /// <param name="pair6">Valutapar som "EURSEK" (Base=foreign, Quote=domestic).</param>
        /// <param name="today">Dagens datum.</param>
        /// <param name="expiry">Optionens förfallodag (används i result för DF_exp).</param>
        /// <param name="spotDate">Spot date.</param>
        /// <param name="settlement">Settlement-datum för forward/hedge.</param>
        /// <param name="inputs">Effektiv payload: Spot/rd/rf (sided eller mid-forced), inkl. LockMode + IsOverride-flaggor.</param>
        /// <param name="targetForwardMid">Målvärde på forward (mid).</param>
        /// <returns>Tuple med (uppdaterade inputs, färsk FxCurveResult).</returns>
        public Tuple<MarketInputs, FxCurveResult> BackSolveFromForwardMid(
            string pair6,
            DateTime today,
            DateTime expiry,
            DateTime spotDate,
            DateTime settlement,
            MarketInputs inputs,
            double targetForwardMid)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (inputs.Spot == null) throw new InvalidOperationException("Spot saknas.");
            if (inputs.Rd == null) throw new InvalidOperationException("rd saknas.");
            if (inputs.Rf == null) throw new InvalidOperationException("rf saknas.");

            // Validera sided (defensivt).
            inputs.Spot.ValidateSidedOrThrow("Spot");
            inputs.Rd.ValidateSidedOrThrow("rd");
            inputs.Rf.ValidateSidedOrThrow("rf");

            // Härleder mid-spot; target F är mid.
            var S_mid = ResolveMid(inputs.Spot.Mid, inputs.Spot.Bid, inputs.Spot.Ask);
            if (!S_mid.HasValue || S_mid.Value <= 0.0)
                throw new InvalidOperationException("Kan inte lösa från forward: mid-spot saknas eller är ogiltig.");

            if (targetForwardMid <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(targetForwardMid), "Forward (mid) måste vara > 0.");

            // Tider och MM-denominator (legacy: domestic=quote, foreign=base).
            string baseCcy, quoteCcy;
            GetPairCurrencies(pair6, out baseCcy, out quoteCcy);

            int denomDom = MoneyMarketDenomForCcy(quoteCcy);
            int denomFor = MoneyMarketDenomForCcy(baseCcy);

            // FWD-fönster (spot → settlement) i MM-år:
            double T_d = Math.Max(0.0, (settlement - spotDate).TotalDays) / (double)denomDom;
            double T_f = Math.Max(0.0, (settlement - spotDate).TotalDays) / (double)denomFor;

            if (T_d <= 0.0 || T_f <= 0.0)
                throw new InvalidOperationException("Forward-fönster har T=0 (spot=settlement); kan inte back-solvera ränta från forward.");

            // ln(F/S) = -(r_f*T_f - r_d*T_d)  => r_f = (r_d*T_d - ln(F/S)) / T_f
            // resp. r_d = (r_f*T_f + ln(F/S)) / T_d
            double lnFdivS = Math.Log(targetForwardMid / S_mid.Value);

            // Välj vilken ränta som hålls/löses.
            var mode = ResolveLockMode(inputs);
            if (mode == LockMode.HoldRd)
            {
                var rd_mid = ResolveMid(inputs.Rd.Mid, inputs.Rd.Bid, inputs.Rd.Ask).GetValueOrDefault();
                var rf_mid_new = (rd_mid * T_d - lnFdivS) / T_f;
                ApplyMidKeepingSpread(inputs.Rf, rf_mid_new);
            }
            else if (mode == LockMode.HoldRf)
            {
                var rf_mid = ResolveMid(inputs.Rf.Mid, inputs.Rf.Bid, inputs.Rf.Ask).GetValueOrDefault();
                var rd_mid_new = (rf_mid * T_f + lnFdivS) / T_d;
                ApplyMidKeepingSpread(inputs.Rd, rd_mid_new);
            }
            else // Split – enkel baseline: håll rd och lös rf (kan bytas till proportionell log-DF vid behov)
            {
                var rd_mid = ResolveMid(inputs.Rd.Mid, inputs.Rd.Bid, inputs.Rd.Ask).GetValueOrDefault();
                var rf_mid_new = (rd_mid * T_d - lnFdivS) / T_f;
                ApplyMidKeepingSpread(inputs.Rf, rf_mid_new);
            }

            // Bygg nytt kurvresultat med legacy-metodik (för att UI ska få F/Swaps/DF).
            var res = _calc.BuildLegacyExp(pair6, today, expiry, spotDate, settlement, inputs);
            return Tuple.Create(inputs, res);
        }

        /// <summary>
        /// Back-solvera rd/rf från ett målvärde på Swap (mid).
        /// - targetSwapMid definierar Forward via F = S + Swap, och vi återanvänder forward-baserad lösning.
        /// - Låsregler/override-schemat följs som i BackSolveFromForwardMid.
        /// </summary>
        public Tuple<MarketInputs, FxCurveResult> BackSolveFromSwapMid(
            string pair6,
            DateTime today,
            DateTime expiry,
            DateTime spotDate,
            DateTime settlement,
            MarketInputs inputs,
            double targetSwapMid)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (inputs.Spot == null) throw new InvalidOperationException("Spot saknas.");

            inputs.Spot.ValidateSidedOrThrow("Spot");
            var S_mid = ResolveMid(inputs.Spot.Mid, inputs.Spot.Bid, inputs.Spot.Ask);
            if (!S_mid.HasValue)
                throw new InvalidOperationException("Kan inte lösa från swap: mid-spot saknas.");

            var targetFwdMid = S_mid.Value + targetSwapMid;
            return BackSolveFromForwardMid(pair6, today, expiry, spotDate, settlement, inputs, targetFwdMid);
        }

        // ================== Hjälpmetoder ==================

        /// <summary>
        /// Resolve MID från (Mid) eller (Bid+Ask)/2 om Mid saknas. Returnerar null om sidor saknas.
        /// </summary>
        private static double? ResolveMid(double? mid, double? bid, double? ask)
        {
            if (mid.HasValue) return mid.Value;
            if (bid.HasValue && ask.HasValue) return 0.5 * (bid.Value + ask.Value);
            return null;
        }

        /// <summary>
        /// Bestäm effektiv låsregel med override-prioritet:
        /// - Om exakt en (rd/rf) har IsOverride=true → håll den (HoldRd/HoldRf).
        /// - Om båda eller ingen → returnera inputs.LockMode.
        /// </summary>
        private static LockMode ResolveLockMode(MarketInputs inputs)
        {
            bool rdOv = inputs.Rd != null && inputs.Rd.IsOverride;
            bool rfOv = inputs.Rf != null && inputs.Rf.IsOverride;

            if (rdOv && !rfOv) return LockMode.HoldRd;
            if (!rdOv && rfOv) return LockMode.HoldRf;
            return inputs.LockMode; // båda eller ingen override → använd explicit regel
        }

        /// <summary>
        /// Sätt nytt MID och bevara spreadbredden:
        /// - Spread tas från (Ask-Bid) om båda finns; annars från q.Spread om satt; annars 0.
        /// - Sätter Mid=q och bygger Bid/Ask = Mid ± Spread/2.
        /// - Validerar monotoni.
        /// </summary>
        private static void ApplyMidKeepingSpread(SidedQuote q, double newMid)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));

            double spread;
            if (q.Bid.HasValue && q.Ask.HasValue)
                spread = q.Ask.Value - q.Bid.Value;
            else if (q.Spread.HasValue)
                spread = q.Spread.Value;
            else
                spread = 0.0;

            q.Mid = newMid;
            q.Spread = spread;

            if (spread == 0.0)
            {
                q.Bid = newMid;
                q.Ask = newMid;
            }
            else
            {
                var half = spread / 2.0;
                q.Bid = newMid - half;
                q.Ask = newMid + half;
                if (q.Bid.Value > q.Ask.Value)
                    throw new InvalidOperationException("Monotoni bruten efter ApplyMidKeepingSpread (Bid > Ask).");
            }
        }

        /// <summary>
        /// Splittar valutapar till BASE och QUOTE i uppercase.
        /// </summary>
        private static void GetPairCurrencies(string pair6, out string baseCcy, out string quoteCcy)
        {
            baseCcy = "XXX";
            quoteCcy = "YYY";
            if (!string.IsNullOrEmpty(pair6))
            {
                if (pair6.Length >= 3) baseCcy = pair6.Substring(0, 3).ToUpperInvariant();
                if (pair6.Length >= 6) quoteCcy = pair6.Substring(3, 3).ToUpperInvariant();
            }
        }

        /// <summary>
        /// Legacy MM-denominator (dagbas) per valuta.
        /// 360 för de flesta; 365 för bl.a. GBP, AUD, NZD, CAD, HKD, SGD, ZAR, ILS.
        /// </summary>
        private static int MoneyMarketDenomForCcy(string ccy)
        {
            var u = (ccy ?? "").ToUpperInvariant();
            switch (u)
            {
                case "GBP":
                case "AUD":
                case "NZD":
                case "CAD":
                case "HKD":
                case "SGD":
                case "ZAR":
                case "ILS":
                    return 365;
                default:
                    return 360; // USD, EUR, SEK, NOK, DKK, CHF, JPY, ...
            }
        }
    }
}
