
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FX.Core.Domain;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed partial class LegacyPriceEngineAdapter : IPriceEngine
    {

        private readonly FX.Core.Interfaces.ISpotSetDateService _spotSvc;


        public LegacyPriceEngineAdapter(IVolService vols, FX.Core.Interfaces.ISpotSetDateService spotSvc)
        {
            _spotSvc = spotSvc;
        }


        /// <summary>
        /// Tvåvägsvol: kör tre prissättningar (Mid obligatoriskt, Bid/Ask om de finns).
        /// Greker returneras på Mid. Prissätter första benet i requestet.
        /// Premium returneras "per unit" (dvs. ej multiplicerat med notional).
        /// </summary>
        public async Task<TwoSidedPriceResult> PriceAsync(PricingRequest request, CancellationToken ct = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Vol == null) throw new ArgumentException("PricingRequest saknar Vol.", nameof(request));
            if (request.Legs == null || request.Legs.Count == 0)
                throw new ArgumentException("PricingRequest saknar legs.", nameof(request));


            // --- 0) Plocka första benet och extrahera basdata ---
            var leg = request.Legs[0]; // TODO: utöka till loop/summering vid flerben
            ExtractLegCore(request, leg,
                out var pair6, out var isCall, out var isBuy, out var K, out var expiryDate);


            // Idag/spot- & settlement-datum via service om du har den kopplad (annars helg-MVP)
            var today = DateTime.Today;
            if (expiryDate <= today)
            {
                // Utgång: 0-premium om gammalt förfall (som i din legacy)
                return new TwoSidedPriceResult(0, 0, 0, 0, 0, 0, 0);
            }

            var dates = _spotSvc.Compute(pair6, today, expiryDate);
            var spotDate = dates.SpotDate;
            var settlement = dates.SettlementDate;


            // Räntedagsbas per valuta (quote/domestic resp. base/foreign)
            string baseCcy = SafeBase(pair6);
            string quoteCcy = SafeQuote(pair6);
            int denomDom = MoneyMarketDenomForCcy(quoteCcy);
            int denomFor = MoneyMarketDenomForCcy(baseCcy);

            // Tider
            double Topt = Math.Max(1e-10, (expiryDate - today).TotalDays / 365.0);
            double sqrtT = Math.Sqrt(Topt);

            // === 1) Diskonteringsfaktorer ===
            double TdfDom_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomDom;
            double TdfFor_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomFor;

            double TdfDom_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomDom;
            double TdfFor_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomFor;

            // Exponentiell diskontering (legacy default)
            double DFd_exp = Math.Exp(-request.Rd * TdfDom_exp);
            double DFf_exp = Math.Exp(-request.Rf * TdfFor_exp);

            double DFd_fwd = Math.Exp(-request.Rd * TdfDom_fwd);
            double DFf_fwd = Math.Exp(-request.Rf * TdfFor_fwd);


            // --- Forward-ratio (oförändrat) ---
            double fwdRatio = DFf_fwd / Math.Max(1e-12, DFd_fwd);
            // --- Välj spot för BID/MID/ASK enligt CALL/PUT-regeln ---
            double sBid = request.SpotBid;
            double sAsk = request.SpotAsk;
            double sMid = 0.5 * (sBid + sAsk);

            double F_mid = sMid * fwdRatio;

            // ASK: Call → högre spot, Put → lägre spot
            double F_forAsk = (isCall ? sAsk : sBid) * fwdRatio;

            // BID: Call → lägre spot, Put → högre spot
            double F_forBid = (isCall ? sBid : sAsk) * fwdRatio;


            // === 2) Beräkna MID/BID/ASK ===
            // Mid
            var mid = await PriceOneSigmaCoreAsync(
                F_mid, K, isCall, request.Vol.Mid, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);

            // Välj sigma för sidorna: specifik om satt, annars mid
            double sigmaBid = request.Vol.Bid ?? request.Vol.Mid;
            double sigmaAsk = request.Vol.Ask ?? request.Vol.Mid;

            // BID – alltid räkna på F_forBid
            var bidRes = await PriceOneSigmaCoreAsync(
                F_forBid, K, isCall, sigmaBid, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);
            double bidPremium = bidRes.Premium;

            // ASK – alltid räkna på F_forAsk
            var askRes = await PriceOneSigmaCoreAsync(
                F_forAsk, K, isCall, sigmaAsk, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);
            double askPremium = askRes.Premium;

            // === 3) Greker på MID ===
            return new TwoSidedPriceResult(
                premiumBid: bidPremium,
                premiumMid: mid.Premium,
                premiumAsk: askPremium,
                delta: mid.Delta,
                gamma: mid.Gamma,
                vega: mid.Vega,
                theta: mid.Theta
            );
        }

        /// <summary>
        /// Kärnberäkning för EN sigma (din gamla logik, men frikopplad till rena parametrar).
        /// Returnerar premium per unit (positivt) och greker per unit. Theta=0 som i legacy.
        /// </summary>
        private static Task<_OneSigmaResult> PriceOneSigmaCoreAsync(
            double F, double K, bool isCall, double sigma, double Topt, double sqrtT,
            double DFd_exp, double DFf_exp, CancellationToken ct)
        {
            if (sigma <= 0.0 || Topt <= 0.0)
            {
                // Intrinsic-fallback
                double intrinsic = isCall ? Math.Max(0.0, F - K) : Math.Max(0.0, K - F);
                // OBS: i din gamla kod användes S/K när vol=0; här gör vi forward-baserad intrinsic.
                var fallback = new _OneSigmaResult(
                    premium: intrinsic * DFd_exp, // diskonterad intrinsic
                    delta: 0.0, gamma: 0.0, vega: 0.0, theta: 0.0);
                return Task.FromResult(fallback);
            }

            double s2T = sigma * sigma * Topt;
            double d1 = (Math.Log(Math.Max(1e-300, F / K)) + 0.5 * s2T) / (Math.Max(1e-12, sigma) * sqrtT);
            double d2 = d1 - sigma * sqrtT;

            double Nd1 = CND(d1);
            double Nd2 = CND(d2);
            double Nmd1 = 1.0 - Nd1;
            double Nmd2 = 1.0 - Nd2;

            double call = DFd_exp * (F * Nd1 - K * Nd2);
            double put = DFd_exp * (K * Nmd2 - F * Nmd1);
            double premiumPerUnit = isCall ? call : put;

            double pdf = nPDF(d1);
            double deltaUnit = isCall ? DFf_exp * Nd1 : -DFf_exp * Nmd1;
            double vegaUnit = DFd_exp * F * pdf * Math.Sqrt(Topt) / 100.0; // per vol-punkt
            double gammaUnit = DFd_exp * pdf / (Math.Max(1e-12, F) * Math.Max(1e-12, sigma) * sqrtT);

            var res = new _OneSigmaResult(
                premium: Math.Abs(premiumPerUnit), // positiv premium per unit (som i din legacy)
                delta: deltaUnit,
                gamma: gammaUnit,
                vega: vegaUnit,
                theta: 0.0 // legacy: 0 tills vidare
            );
            return Task.FromResult(res);
        }

        // === Hjälp-typer ===
        private sealed class _OneSigmaResult
        {
            public double Premium { get; }
            public double Delta { get; }
            public double Gamma { get; }
            public double Vega { get; }
            public double Theta { get; }

            public _OneSigmaResult(double premium, double delta, double gamma, double vega, double theta)
            {
                Premium = premium; Delta = delta; Gamma = gamma; Vega = vega; Theta = theta;
            }
        }

        // === Extraktion av leg/strike/expiry med snälla fallbacks ===
        private static void ExtractLegCore(
            PricingRequest req, object leg,
            out string pair6, out bool isCall, out bool isBuy, out double K, out DateTime expiryDate)
        {
            pair6 = PairToPair6(req.Pair) ?? "EURSEK";
            isCall = TryGetString(leg, "Type", out var typeStr)
                     && typeStr.Equals("CALL", StringComparison.OrdinalIgnoreCase);
            isBuy = TryGetString(leg, "Side", out var sideStr)
                     && sideStr.Equals("BUY", StringComparison.OrdinalIgnoreCase);

            // Expiry: leta Expiry.Date eller Expiry.Value eller direkt Date
            if (!TryGetExpiryDate(leg, out expiryDate))
                expiryDate = DateTime.Today.AddMonths(1); // fallback 1M

            // Strike: försök läsa numerisk; om delta/okänt → ATM≈F (sätt numerisk senare i kärnan)
            if (!TryGetNumericStrike(leg, out K))
                K = 0.5 * (req.SpotBid + req.SpotAsk); // temporär; i kärnan används K≈F när vol>0
        }

        private static string PairToPair6(object pair)
        {
            if (pair == null) return null;
            // Försök läsa Base/Quote egenskaper
            if (TryGetString(pair, "Base", out var b) && TryGetString(pair, "Quote", out var q))
                return (b + q).ToUpperInvariant();
            // Annars .ToString()
            return pair.ToString()?.ToUpperInvariant();
        }

        private static bool TryGetExpiryDate(object leg, out DateTime expiry)
        {
            expiry = default(DateTime);
            // leg.Expiry?.Date / Value
            if (TryGetObject(leg, "Expiry", out var exp))
            {
                if (TryGetDate(exp, "Date", out expiry)) return true;
                if (TryGetDate(exp, "Value", out expiry)) return true;
            }
            // leg.ExpiryIso (yyyy-MM-dd)
            if (TryGetString(leg, "ExpiryIso", out var iso) &&
                DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None, out expiry))
                return true;

            // leg.Expiry (DateTime direkt)
            if (TryGetDate(leg, "Expiry", out expiry)) return true;

            return false;
        }

        private static bool TryGetNumericStrike(object leg, out double K)
        {
            K = 0.0;
            // leg.Strike?.Value (double)
            if (TryGetObject(leg, "Strike", out var s))
            {
                if (TryGetDouble(s, "Value", out K)) return true;
                if (TryGetDouble(s, "Numeric", out K)) return true;
                // leg.Strike.Raw som "11.00" eller "25D"
                if (TryGetString(s, "Raw", out var raw) &&
                    double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out K))
                    return true;
            }
            // leg.Strike (double direkt)
            if (TryGetDouble(leg, "Strike", out K)) return true;

            // leg.Strike som string "11.00"
            if (TryGetString(leg, "Strike", out var sraw) &&
                double.TryParse(sraw, NumberStyles.Any, CultureInfo.InvariantCulture, out K))
                return true;

            return false;
        }

        // === Små reflection-hjälpare (robusta mot olika domänvarianter) ===
        private static bool TryGetObject(object obj, string name, out object value)
        {
            value = null;
            if (obj == null) return false;
            var prop = obj.GetType().GetProperty(name);
            if (prop == null) return false;
            value = prop.GetValue(obj, null);
            return value != null;
        }

        private static bool TryGetString(object obj, string name, out string value)
        {
            value = null;
            if (!TryGetObject(obj, name, out var o)) return false;
            value = o?.ToString();
            return !string.IsNullOrEmpty(value);
        }

        private static bool TryGetDouble(object obj, string name, out double value)
        {
            value = 0.0;
            if (!TryGetObject(obj, name, out var o)) return false;
            if (o is double d) { value = d; return true; }
            if (o is float f) { value = (double)f; return true; }
            if (o is int i) { value = i; return true; }
            if (o is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            { value = p; return true; }
            return false;
        }

        private static bool TryGetDate(object obj, string name, out DateTime value)
        {
            value = default(DateTime);
            if (!TryGetObject(obj, name, out var o)) return false;
            if (o is DateTime dt) { value = dt.Date; return true; }
            if (o is string s &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var p))
            { value = p.Date; return true; }
            return false;
        }

        // === Hjälp-funktioner (från din gamla kod) ===
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

        private static int MoneyMarketDenomForCcy(string ccy)
        {
            string u = (ccy ?? "").ToUpperInvariant();
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

        private static double CND(double x) { return 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0))); }
        private static double nPDF(double x) { return Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI); }

        // Abramowitz–Stegun 7.1.26
        private static double Erf(double x)
        {
            double t = 1.0 / (1.0 + 0.5 * Math.Abs(x));
            double tau = t * Math.Exp(-x * x - 1.26551223 +
                      1.00002368 * t + 0.37409196 * t * t + 0.09678418 * Math.Pow(t, 3) -
                      0.18628806 * Math.Pow(t, 4) + 0.27886807 * Math.Pow(t, 5) - 1.13520398 * Math.Pow(t, 6) +
                      1.48851587 * Math.Pow(t, 7) - 0.82215223 * Math.Pow(t, 8) + 0.17087277 * Math.Pow(t, 9));
            return x >= 0 ? 1.0 - tau : tau - 1.0;
        }

        private static DateTime AddBusinessDays(DateTime start, int n)
        {
            if (n <= 0) return start;
            int added = 0;
            var d = start;
            while (added < n)
            {
                d = d.AddDays(1);
                if (IsBusinessDay(d)) added++;
            }
            return d;
        }
        private static bool IsBusinessDay(DateTime d)
        {
            var dow = d.DayOfWeek;
            return !(dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday);
        }
    }
}


/*


using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FX.Core.Domain;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed partial class LegacyPriceEngineAdapter : IPriceEngine
    {

        private readonly FX.Core.Interfaces.ISpotSetDateService _spotSvc;


        public LegacyPriceEngineAdapter(IVolService vols, FX.Core.Interfaces.ISpotSetDateService spotSvc)
        {
            _spotSvc = spotSvc;
        }


        /// <summary>
        /// Tvåvägsvol: kör tre prissättningar (Mid obligatoriskt, Bid/Ask om de finns).
        /// Greker returneras på Mid. Prissätter första benet i requestet.
        /// Premium returneras "per unit" (dvs. ej multiplicerat med notional).
        /// </summary>
        public async Task<TwoSidedPriceResult> PriceAsync(PricingRequest request, CancellationToken ct = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Vol == null) throw new ArgumentException("PricingRequest saknar Vol.", nameof(request));
            if (request.Legs == null || request.Legs.Count == 0)
                throw new ArgumentException("PricingRequest saknar legs.", nameof(request));


            // --- 0) Plocka första benet och extrahera basdata ---
            var leg = request.Legs[0]; // TODO: utöka till loop/summering vid flerben
            ExtractLegCore(request, leg,
                out var pair6, out var isCall, out var isBuy, out var K, out var expiryDate);


            // Idag/spot- & settlement-datum via service om du har den kopplad (annars helg-MVP)
            var today = DateTime.Today;
            if (expiryDate <= today)
            {
                // Utgång: 0-premium om gammalt förfall (som i din legacy)
                return new TwoSidedPriceResult(0, 0, 0, 0, 0, 0, 0);
            }

            var dates = _spotSvc.Compute(pair6, today, expiryDate);
            var spotDate = dates.SpotDate;
            var settlement = dates.SettlementDate;


            // Räntedagsbas per valuta (quote/domestic resp. base/foreign)
            string baseCcy = SafeBase(pair6);
            string quoteCcy = SafeQuote(pair6);
            int denomDom = MoneyMarketDenomForCcy(quoteCcy);
            int denomFor = MoneyMarketDenomForCcy(baseCcy);

            // Tider
            double Topt = Math.Max(1e-10, (expiryDate - today).TotalDays / 365.0);
            double sqrtT = Math.Sqrt(Topt);

            // === 1) Diskonteringsfaktorer ===
            double TdfDom_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomDom;
            double TdfFor_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomFor;

            double TdfDom_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomDom;
            double TdfFor_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomFor;

            // Exponentiell diskontering (legacy default)
            double DFd_exp = Math.Exp(-request.Rd * TdfDom_exp);
            double DFf_exp = Math.Exp(-request.Rf * TdfFor_exp);

            double DFd_fwd = Math.Exp(-request.Rd * TdfDom_fwd);
            double DFf_fwd = Math.Exp(-request.Rf * TdfFor_fwd);

            // Forward (spot -> settlement)
            double F = request.Spot * (DFf_fwd / Math.Max(1e-12, DFd_fwd));

            // === 2) Beräkna MID/BID/ASK ===
            // Mid
            var mid = await PriceOneSigmaCoreAsync(
                F, K, isCall, request.Vol.Mid, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);

            // Bid (fallback = mid)
            double bidPremium = mid.Premium;
            if (request.Vol.Bid.HasValue)
            {
                var bid = await PriceOneSigmaCoreAsync(
                    F, K, isCall, request.Vol.Bid.Value, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);
                bidPremium = bid.Premium;
            }

            // Ask (fallback = mid)
            double askPremium = mid.Premium;
            if (request.Vol.Ask.HasValue)
            {
                var ask = await PriceOneSigmaCoreAsync(
                    F, K, isCall, request.Vol.Ask.Value, Topt, sqrtT, DFd_exp, DFf_exp, ct).ConfigureAwait(false);
                askPremium = ask.Premium;
            }

            // === 3) Greker på MID ===
            return new TwoSidedPriceResult(
                premiumBid: bidPremium,
                premiumMid: mid.Premium,
                premiumAsk: askPremium,
                delta: mid.Delta,
                gamma: mid.Gamma,
                vega: mid.Vega,
                theta: mid.Theta
            );
        }

        /// <summary>
        /// Kärnberäkning för EN sigma (din gamla logik, men frikopplad till rena parametrar).
        /// Returnerar premium per unit (positivt) och greker per unit. Theta=0 som i legacy.
        /// </summary>
        private static Task<_OneSigmaResult> PriceOneSigmaCoreAsync(
            double F, double K, bool isCall, double sigma, double Topt, double sqrtT,
            double DFd_exp, double DFf_exp, CancellationToken ct)
        {
            if (sigma <= 0.0 || Topt <= 0.0)
            {
                // Intrinsic-fallback
                double intrinsic = isCall ? Math.Max(0.0, F - K) : Math.Max(0.0, K - F);
                // OBS: i din gamla kod användes S/K när vol=0; här gör vi forward-baserad intrinsic.
                var fallback = new _OneSigmaResult(
                    premium: intrinsic * DFd_exp, // diskonterad intrinsic
                    delta: 0.0, gamma: 0.0, vega: 0.0, theta: 0.0);
                return Task.FromResult(fallback);
            }

            double s2T = sigma * sigma * Topt;
            double d1 = (Math.Log(Math.Max(1e-300, F / K)) + 0.5 * s2T) / (Math.Max(1e-12, sigma) * sqrtT);
            double d2 = d1 - sigma * sqrtT;

            double Nd1 = CND(d1);
            double Nd2 = CND(d2);
            double Nmd1 = 1.0 - Nd1;
            double Nmd2 = 1.0 - Nd2;

            double call = DFd_exp * (F * Nd1 - K * Nd2);
            double put = DFd_exp * (K * Nmd2 - F * Nmd1);
            double premiumPerUnit = isCall ? call : put;

            double pdf = nPDF(d1);
            double deltaUnit = isCall ? DFf_exp * Nd1 : -DFf_exp * Nmd1;
            double vegaUnit = DFd_exp * F * pdf * Math.Sqrt(Topt) / 100.0; // per vol-punkt
            double gammaUnit = DFd_exp * pdf / (Math.Max(1e-12, F) * Math.Max(1e-12, sigma) * sqrtT);

            var res = new _OneSigmaResult(
                premium: Math.Abs(premiumPerUnit), // positiv premium per unit (som i din legacy)
                delta: deltaUnit,
                gamma: gammaUnit,
                vega: vegaUnit,
                theta: 0.0 // legacy: 0 tills vidare
            );
            return Task.FromResult(res);
        }

        // === Hjälp-typer ===
        private sealed class _OneSigmaResult
        {
            public double Premium { get; }
            public double Delta { get; }
            public double Gamma { get; }
            public double Vega { get; }
            public double Theta { get; }

            public _OneSigmaResult(double premium, double delta, double gamma, double vega, double theta)
            {
                Premium = premium; Delta = delta; Gamma = gamma; Vega = vega; Theta = theta;
            }
        }

        // === Extraktion av leg/strike/expiry med snälla fallbacks ===
        private static void ExtractLegCore(
            PricingRequest req, object leg,
            out string pair6, out bool isCall, out bool isBuy, out double K, out DateTime expiryDate)
        {
            pair6 = PairToPair6(req.Pair) ?? "EURSEK";
            isCall = TryGetString(leg, "Type", out var typeStr)
                     && typeStr.Equals("CALL", StringComparison.OrdinalIgnoreCase);
            isBuy = TryGetString(leg, "Side", out var sideStr)
                     && sideStr.Equals("BUY", StringComparison.OrdinalIgnoreCase);

            // Expiry: leta Expiry.Date eller Expiry.Value eller direkt Date
            if (!TryGetExpiryDate(leg, out expiryDate))
                expiryDate = DateTime.Today.AddMonths(1); // fallback 1M

            // Strike: försök läsa numerisk; om delta/okänt → ATM≈F (sätt numerisk senare i kärnan)
            if (!TryGetNumericStrike(leg, out K))
                K = req.Spot; // temporär; i kärnan används K≈F när vol>0
        }

        private static string PairToPair6(object pair)
        {
            if (pair == null) return null;
            // Försök läsa Base/Quote egenskaper
            if (TryGetString(pair, "Base", out var b) && TryGetString(pair, "Quote", out var q))
                return (b + q).ToUpperInvariant();
            // Annars .ToString()
            return pair.ToString()?.ToUpperInvariant();
        }

        private static bool TryGetExpiryDate(object leg, out DateTime expiry)
        {
            expiry = default(DateTime);
            // leg.Expiry?.Date / Value
            if (TryGetObject(leg, "Expiry", out var exp))
            {
                if (TryGetDate(exp, "Date", out expiry)) return true;
                if (TryGetDate(exp, "Value", out expiry)) return true;
            }
            // leg.ExpiryIso (yyyy-MM-dd)
            if (TryGetString(leg, "ExpiryIso", out var iso) &&
                DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None, out expiry))
                return true;

            // leg.Expiry (DateTime direkt)
            if (TryGetDate(leg, "Expiry", out expiry)) return true;

            return false;
        }

        private static bool TryGetNumericStrike(object leg, out double K)
        {
            K = 0.0;
            // leg.Strike?.Value (double)
            if (TryGetObject(leg, "Strike", out var s))
            {
                if (TryGetDouble(s, "Value", out K)) return true;
                if (TryGetDouble(s, "Numeric", out K)) return true;
                // leg.Strike.Raw som "11.00" eller "25D"
                if (TryGetString(s, "Raw", out var raw) &&
                    double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out K))
                    return true;
            }
            // leg.Strike (double direkt)
            if (TryGetDouble(leg, "Strike", out K)) return true;

            // leg.Strike som string "11.00"
            if (TryGetString(leg, "Strike", out var sraw) &&
                double.TryParse(sraw, NumberStyles.Any, CultureInfo.InvariantCulture, out K))
                return true;

            return false;
        }

        // === Små reflection-hjälpare (robusta mot olika domänvarianter) ===
        private static bool TryGetObject(object obj, string name, out object value)
        {
            value = null;
            if (obj == null) return false;
            var prop = obj.GetType().GetProperty(name);
            if (prop == null) return false;
            value = prop.GetValue(obj, null);
            return value != null;
        }

        private static bool TryGetString(object obj, string name, out string value)
        {
            value = null;
            if (!TryGetObject(obj, name, out var o)) return false;
            value = o?.ToString();
            return !string.IsNullOrEmpty(value);
        }

        private static bool TryGetDouble(object obj, string name, out double value)
        {
            value = 0.0;
            if (!TryGetObject(obj, name, out var o)) return false;
            if (o is double d) { value = d; return true; }
            if (o is float f) { value = (double)f; return true; }
            if (o is int i) { value = i; return true; }
            if (o is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            { value = p; return true; }
            return false;
        }

        private static bool TryGetDate(object obj, string name, out DateTime value)
        {
            value = default(DateTime);
            if (!TryGetObject(obj, name, out var o)) return false;
            if (o is DateTime dt) { value = dt.Date; return true; }
            if (o is string s &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var p))
            { value = p.Date; return true; }
            return false;
        }

        // === Hjälp-funktioner (från din gamla kod) ===
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

        private static int MoneyMarketDenomForCcy(string ccy)
        {
            string u = (ccy ?? "").ToUpperInvariant();
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

        private static double CND(double x) { return 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0))); }
        private static double nPDF(double x) { return Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI); }

        // Abramowitz–Stegun 7.1.26
        private static double Erf(double x)
        {
            double t = 1.0 / (1.0 + 0.5 * Math.Abs(x));
            double tau = t * Math.Exp(-x * x - 1.26551223 +
                      1.00002368 * t + 0.37409196 * t * t + 0.09678418 * Math.Pow(t, 3) -
                      0.18628806 * Math.Pow(t, 4) + 0.27886807 * Math.Pow(t, 5) - 1.13520398 * Math.Pow(t, 6) +
                      1.48851587 * Math.Pow(t, 7) - 0.82215223 * Math.Pow(t, 8) + 0.17087277 * Math.Pow(t, 9));
            return x >= 0 ? 1.0 - tau : tau - 1.0;
        }

        private static DateTime AddBusinessDays(DateTime start, int n)
        {
            if (n <= 0) return start;
            int added = 0;
            var d = start;
            while (added < n)
            {
                d = d.AddDays(1);
                if (IsBusinessDay(d)) added++;
            }
            return d;
        }
        private static bool IsBusinessDay(DateTime d)
        {
            var dow = d.DayOfWeek;
            return !(dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday);
        }
    }
}

*/
