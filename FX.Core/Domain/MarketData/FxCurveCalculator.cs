using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Compounding-konvention för att konvertera rd/rf → DF.
    /// Hålls minimal för att passa baseline (C# 7.3).
    /// </summary>
    public enum CompoundingConvention
    {
        /// <summary>Enkel ränta: DF = 1 / (1 + r * T)</summary>
        Simple = 0,

        /// <summary>Diskret compounding m ggr/år: DF = 1 / (1 + r/g)^(g*T)</summary>
        Discrete = 1,

        /// <summary>Kontinuerlig compounding: DF = exp(-r*T)</summary>
        Continuous = 2,
    }

    /// <summary>
    /// Byggparametrar för kurvan (används av Build(...)).
    /// UI/överliggande lager matar in YearFraction och compounding per ben.
    /// </summary>
    public sealed class FxCurveBuildSettings
    {
        /// <summary>
        /// Tidsandel i år (year fraction) enligt vald day count (ex. ACT/360). Måste vara ≥ 0.
        /// </summary>
        public double YearFraction { get; set; }

        /// <summary>Compounding-konvention för domestic-räntan (rd).</summary>
        public CompoundingConvention RdCompounding { get; set; } = CompoundingConvention.Simple;

        /// <summary>Compounding-konvention för foreign-räntan (rf).</summary>
        public CompoundingConvention RfCompounding { get; set; } = CompoundingConvention.Simple;

        /// <summary>Antal perioder/år för diskret compounding, om Discrete används (t.ex. 1,2,4,12).</summary>
        public int DiscretePeriodsPerYear { get; set; } = 1;
    }

    /// <summary>
    /// Resultatbehållare från kurvbyggaren:
    /// - Diskonteringsfaktorer (DF) för domestic (d) och foreign (f)
    /// - Forward (F) och Swap points (Swap), var och en som bid/ask/mid där möjligt
    /// </summary>
    public sealed class FxCurveResult
    {
        // Discount factors – domestic (d) och foreign (f)
        public double? DF_d_bid { get; set; }
        public double? DF_d_ask { get; set; }
        public double? DF_d_mid { get; set; }

        public double? DF_f_bid { get; set; }
        public double? DF_f_ask { get; set; }
        public double? DF_f_mid { get; set; }

        // Forward (S × DF_f / DF_d)
        public double? F_bid { get; set; }
        public double? F_ask { get; set; }
        public double? F_mid { get; set; }

        // Swap points = Forward − Spot
        public double? Swap_bid { get; set; }
        public double? Swap_ask { get; set; }
        public double? Swap_mid { get; set; }
    }

    /// <summary>
    /// FxCurveCalculator
    /// -----------------
    /// Ansvar:
    ///  - Bygg DF_d/DF_f (bid/ask/mid) från rd/rf och tid/compounding.
    ///  - Härled F (bid/ask/mid) och Swap (bid/ask/mid) från Spot + DFs.
    ///  - Respektera tvåvägsrelationen för arbitragefrihet:
    ///       F_bid = S × DF_f_bid / DF_d_ask,   F_ask = S × DF_f_ask / DF_d_bid
    ///
    /// Två ingångar:
    ///  1) Build(...): generisk compounding (Simple/Discrete/Continuous) med given YearFraction.
    ///  2) BuildLegacyExp(...): legacy-kompatibel exponential-diskontering med MM-denominator (360/365)
    ///     separerat för expiry samt forwardfönster (spot→settlement), precis som din legacy-prisare.
    ///
    /// Ingen UI-beroende logik. Endast rena härledningar.
    /// </summary>
    public sealed class FxCurveCalculator
    {
        // =====================================================================
        // 1) GENERISK BYGGARE (valfri compounding + given YearFraction)
        // =====================================================================

        /// <summary>
        /// Bygger en kurv-projektion (DFs + F + Swaps) från effektiv payload (sided eller mid-forced)
        /// och generiska bygginställningar (YearFraction + compounding).
        /// Använd när du vill styra compounding explicit.
        /// </summary>
        public FxCurveResult Build(MarketInputs inputs, FxCurveBuildSettings settings)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (settings.YearFraction < 0)
                throw new ArgumentOutOfRangeException(nameof(settings.YearFraction), "YearFraction måste vara ≥ 0.");

            // Säkerställ nödvändig data (defensivt)
            if (inputs.Rd == null) throw new InvalidOperationException("rd saknas i inputs.");
            if (inputs.Rf == null) throw new InvalidOperationException("rf saknas i inputs.");
            if (inputs.Spot == null) throw new InvalidOperationException("Spot saknas i inputs.");

            inputs.Rd.ValidateSidedOrThrow("rd");
            inputs.Rf.ValidateSidedOrThrow("rf");
            inputs.Spot.ValidateSidedOrThrow("Spot");

            // 1) DF_d/DF_f (bid/ask/mid) från rd/rf
            var df_d_bid = RateToDf(inputs.Rd.Bid.Value, settings.YearFraction, settings.RdCompounding, settings.DiscretePeriodsPerYear);
            var df_d_ask = RateToDf(inputs.Rd.Ask.Value, settings.YearFraction, settings.RdCompounding, settings.DiscretePeriodsPerYear);
            var df_f_bid = RateToDf(inputs.Rf.Bid.Value, settings.YearFraction, settings.RfCompounding, settings.DiscretePeriodsPerYear);
            var df_f_ask = RateToDf(inputs.Rf.Ask.Value, settings.YearFraction, settings.RfCompounding, settings.DiscretePeriodsPerYear);

            double? df_d_mid = null;
            double? df_f_mid = null;

            var rd_mid_rate = ResolveMidRate(inputs.Rd);
            var rf_mid_rate = ResolveMidRate(inputs.Rf);
            if (rd_mid_rate.HasValue)
                df_d_mid = RateToDf(rd_mid_rate.Value, settings.YearFraction, settings.RdCompounding, settings.DiscretePeriodsPerYear);
            if (rf_mid_rate.HasValue)
                df_f_mid = RateToDf(rf_mid_rate.Value, settings.YearFraction, settings.RfCompounding, settings.DiscretePeriodsPerYear);

            // 2) Forward (arbitragefri tvåväg för sidor; mid via mid-DFs)
            var S_bid = inputs.Spot.Bid.Value;
            var S_ask = inputs.Spot.Ask.Value;
            var S_mid = ResolveMidSpot(inputs.Spot);

            var F_bid = S_bid * (df_f_bid / df_d_ask);
            var F_ask = S_ask * (df_f_ask / df_d_bid);

            double? F_mid = null;
            if (df_d_mid.HasValue && df_f_mid.HasValue && S_mid.HasValue)
                F_mid = S_mid.Value * (df_f_mid.Value / df_d_mid.Value);

            // 3) Swaps = F − S
            var swap_bid = F_bid - S_bid;
            var swap_ask = F_ask - S_ask;
            double? swap_mid = null;
            if (F_mid.HasValue && S_mid.HasValue)
                swap_mid = F_mid.Value - S_mid.Value;

            return new FxCurveResult
            {
                DF_d_bid = df_d_bid,
                DF_d_ask = df_d_ask,
                DF_d_mid = df_d_mid,

                DF_f_bid = df_f_bid,
                DF_f_ask = df_f_ask,
                DF_f_mid = df_f_mid,

                F_bid = F_bid,
                F_ask = F_ask,
                F_mid = F_mid,

                Swap_bid = swap_bid,
                Swap_ask = swap_ask,
                Swap_mid = swap_mid
            };
        }

        /// <summary>
        /// Ränta → DF enligt vald compounding (Simple/Discrete/Continuous).
        /// </summary>
        private static double RateToDf(double r, double T, CompoundingConvention c, int periods)
        {
            if (T == 0) return 1.0; // DF(0) = 1

            switch (c)
            {
                case CompoundingConvention.Simple:
                    // DF = 1 / (1 + r*T)
                    return 1.0 / (1.0 + r * T);

                case CompoundingConvention.Discrete:
                    // DF = 1 / (1 + r/g)^(g*T)    (g = periods per year)
                    if (periods <= 0) periods = 1;
                    var gT = periods * T;
                    var baseTerm = 1.0 + (r / periods);
                    return Math.Pow(baseTerm, -gT);

                case CompoundingConvention.Continuous:
                    // DF = exp(-r*T)
                    return Math.Exp(-r * T);

                default:
                    throw new ArgumentOutOfRangeException(nameof(c), "Okänd compounding-konvention.");
            }
        }

        // =====================================================================
        // 2) LEGACY-BYGGARE (exponentiell diskontering, MM 360/365, expiry/fwd)
        // =====================================================================

        /// <summary>
        /// Legacy-kompatibel kurvbyggnad (exponentiell diskontering) för FX:
        /// - Dagbas per valuta som i legacy: domestic = quote-ccy, foreign = base-ccy.
        /// - Tider:
        ///     * Expiry:      TdfDom_exp = (expiry - today).Days / denomDom
        ///                    TdfFor_exp = (expiry - today).Days / denomFor
        ///     * Forwardfönster (spot→settlement):
        ///                    TdfDom_fwd = (settlement - spotDate).Days / denomDom
        ///                    TdfFor_fwd = (settlement - spotDate).Days / denomFor
        /// - Diskontering (legacy default): DF = exp(-r * Tdf)
        /// - Forward:
        ///     * Mid:  S_mid × (DFf_fwd_mid / DFd_fwd_mid)
        ///     * Sidor (arbitragefri tvåväg):
        ///         F_bid = S_bid × DFf_fwd_bid / DFd_fwd_ask
        ///         F_ask = S_ask × DFf_fwd_ask / DFd_fwd_bid
        /// - Swaps = F − S (per sida och mid).
        /// </summary>
        public FxCurveResult BuildLegacyExp(
            string pair6,
            DateTime today,
            DateTime expiry,
            DateTime spotDate,
            DateTime settlement,
            MarketInputs inputs)
        {
            if (string.IsNullOrEmpty(pair6)) throw new ArgumentNullException(nameof(pair6));
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (inputs.Spot == null) throw new InvalidOperationException("Spot saknas.");
            if (inputs.Rd == null) throw new InvalidOperationException("rd saknas.");
            if (inputs.Rf == null) throw new InvalidOperationException("rf saknas.");

            // Validera sided-data (defensivt)
            inputs.Spot.ValidateSidedOrThrow("Spot");
            inputs.Rd.ValidateSidedOrThrow("rd");
            inputs.Rf.ValidateSidedOrThrow("rf");

            // 1) Dagbas enligt legacy: domestic = quote, foreign = base
            string baseCcy = SafeBase(pair6);
            string quoteCcy = SafeQuote(pair6);
            int denomDom = MoneyMarketDenomForCcy(quoteCcy);
            int denomFor = MoneyMarketDenomForCcy(baseCcy);

            // 2) Tidsfaktorer (år på money market-bas) för expiry resp. forwardfönster
            double TdfDom_exp = Math.Max(0.0, (expiry - today).TotalDays) / denomDom;
            double TdfFor_exp = Math.Max(0.0, (expiry - today).TotalDays) / denomFor;
            double TdfDom_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomDom;
            double TdfFor_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomFor;

            // 3) DF på sidor och mid (exponentiell diskontering: DF = exp(-r*Tdf))
            //    Expiry-DFs (till payoff-datum)
            double DFd_exp_bid = Math.Exp(-(inputs.Rd.Bid ?? 0.0) * TdfDom_exp);
            double DFd_exp_ask = Math.Exp(-(inputs.Rd.Ask ?? 0.0) * TdfDom_exp);
            double DFf_exp_bid = Math.Exp(-(inputs.Rf.Bid ?? 0.0) * TdfFor_exp);
            double DFf_exp_ask = Math.Exp(-(inputs.Rf.Ask ?? 0.0) * TdfFor_exp);

            double? rd_mid_rate = ResolveMidRate(inputs.Rd);
            double? rf_mid_rate = ResolveMidRate(inputs.Rf);
            double? DFd_exp_mid = rd_mid_rate.HasValue ? (double?)Math.Exp(-rd_mid_rate.Value * TdfDom_exp) : null;
            double? DFf_exp_mid = rf_mid_rate.HasValue ? (double?)Math.Exp(-rf_mid_rate.Value * TdfFor_exp) : null;

            //    Forward-DFs (spot→settlement) – används för forward/points
            double DFd_fwd_bid = Math.Exp(-(inputs.Rd.Bid ?? 0.0) * TdfDom_fwd);
            double DFd_fwd_ask = Math.Exp(-(inputs.Rd.Ask ?? 0.0) * TdfDom_fwd);
            double DFf_fwd_bid = Math.Exp(-(inputs.Rf.Bid ?? 0.0) * TdfFor_fwd);
            double DFf_fwd_ask = Math.Exp(-(inputs.Rf.Ask ?? 0.0) * TdfFor_fwd);

            double? DFd_fwd_mid = rd_mid_rate.HasValue ? (double?)Math.Exp(-rd_mid_rate.Value * TdfDom_fwd) : null;
            double? DFf_fwd_mid = rf_mid_rate.HasValue ? (double?)Math.Exp(-rf_mid_rate.Value * TdfFor_fwd) : null;

            // 4) Forward (arbitragefri tvåväg för sidor; mid via mid-DFs)
            double S_bid = inputs.Spot.Bid.Value;
            double S_ask = inputs.Spot.Ask.Value;
            double? S_mid = ResolveMidSpot(inputs.Spot);

            double F_bid = S_bid * (DFf_fwd_bid / Math.Max(1e-12, DFd_fwd_ask));
            double F_ask = S_ask * (DFf_fwd_ask / Math.Max(1e-12, DFd_fwd_bid));

            double? F_mid = null;
            if (S_mid.HasValue && DFd_fwd_mid.HasValue && DFf_fwd_mid.HasValue)
                F_mid = S_mid.Value * (DFf_fwd_mid.Value / Math.Max(1e-12, DFd_fwd_mid.Value));

            // 5) Swaps = F − S
            double Swap_bid = F_bid - S_bid;
            double Swap_ask = F_ask - S_ask;
            double? Swap_mid = null;
            if (F_mid.HasValue && S_mid.HasValue)
                Swap_mid = F_mid.Value - S_mid.Value;

            // 6) Returnera resultat – expiry-DFs för diskontering; forward-DFs användes för F/Swaps
            return new FxCurveResult
            {
                // Expiry-DFs (används för att diskontera premie/greker till today)
                DF_d_bid = DFd_exp_bid,
                DF_d_ask = DFd_exp_ask,
                DF_d_mid = DFd_exp_mid,

                DF_f_bid = DFf_exp_bid,
                DF_f_ask = DFf_exp_ask,
                DF_f_mid = DFf_exp_mid,

                // Forward/Swaps (byggda från forward-DFs)
                F_bid = F_bid,
                F_ask = F_ask,
                F_mid = F_mid,

                Swap_bid = Swap_bid,
                Swap_ask = Swap_ask,
                Swap_mid = Swap_mid
            };
        }

        // ===== Hjälpmetoder (delas av båda byggena) =====

        private static double? ResolveMidRate(SidedQuote q)
        {
            if (q == null) return null;
            if (q.Mid.HasValue) return q.Mid.Value;
            if (q.Bid.HasValue && q.Ask.HasValue) return 0.5 * (q.Bid.Value + q.Ask.Value);
            return null;
        }

        private static double? ResolveMidSpot(SidedQuote spot)
        {
            if (spot == null) return null;
            if (spot.Mid.HasValue) return spot.Mid.Value;
            if (spot.Bid.HasValue && spot.Ask.HasValue) return 0.5 * (spot.Bid.Value + spot.Ask.Value);
            return null;
        }

        private static string SafeBase(string pair6)
        {
            if (string.IsNullOrEmpty(pair6) || pair6.Length < 3) return "XXX";
            return pair6.Substring(0, 3).ToUpperInvariant();
        }

        private static string SafeQuote(string pair6)
        {
            if (string.IsNullOrEmpty(pair6) || pair6.Length < 6) return "YYY";
            return pair6.Substring(3, 3).ToUpperInvariant();
        }

        /// <summary>
        /// Legacy money-market denominator (dagbas) per valuta.
        /// 360 för de flesta (USD, EUR, SEK, NOK, DKK, CHF, JPY, ...),
        /// 365 för bl.a. GBP, AUD, NZD, CAD, HKD, SGD, ZAR, ILS.
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
                    return 360;
            }
        }
    }
}
