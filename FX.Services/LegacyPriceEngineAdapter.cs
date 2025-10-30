
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
        /// Tvåvägsprissättning (GK på F) där Spot och RD/RF kan vara tvåväg.
        /// Viktigt: Forward per sida använder spot-sidan (S_bid/S_ask) så att Spot-pillen påverkar premien.
        /// - Vi räknar två scenarier:
        ///     scenBid:  S_bid,  DFd_fwd(rdAsk), DFf_fwd(rfBid),  DF till expiry med samma sidning
        ///     scenAsk:  S_ask,  DFd_fwd(rdBid), DFf_fwd(rfAsk),  DF till expiry med samma sidning
        /// - Mappning till Bid/Ask beror på optionstyp:
        ///     * Call: Bid = lägre F, Ask = högre F
        ///     * Put : Bid = högre F, Ask = lägre F
        /// - Greker tas på mid (F_mid, rdMid/rfMid).
        /// </summary>
        public async Task<TwoSidedPriceResult> PriceAsync(PricingRequest request, CancellationToken ct = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Vol == null) throw new ArgumentException("PricingRequest saknar Vol.", nameof(request));
            if (request.Legs == null || request.Legs.Count == 0)
                throw new ArgumentException("PricingRequest saknar legs.", nameof(request));

            // --- 0) Basdata ---
            var leg = request.Legs[0];
            ExtractLegCore(request, leg,
                out var pair6, out var isCall, out var isBuy, out var K, out var expiryDate);

            var today = DateTime.Today;
            if (expiryDate <= today)
                return new TwoSidedPriceResult(0, 0, 0, 0, 0, 0, 0);

            var dates = _spotSvc.Compute(pair6, today, expiryDate);
            var spotDate = dates.SpotDate;
            var settlement = dates.SettlementDate;

            string baseCcy = SafeBase(pair6);
            string quoteCcy = SafeQuote(pair6);
            int denomDom = MoneyMarketDenomForCcy(quoteCcy);
            int denomFor = MoneyMarketDenomForCcy(baseCcy);

            double Topt = Math.Max(1e-10, (expiryDate - today).TotalDays / 365.0);
            double sqrtT = Math.Sqrt(Topt);

            // Year fractions för DF
            double TdfDom_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomDom;
            double TdfFor_exp = Math.Max(0.0, (expiryDate - today).TotalDays) / denomFor;
            double TdfDom_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomDom;
            double TdfFor_fwd = Math.Max(0.0, (settlement - spotDate).TotalDays) / denomFor;

            // 1) Spot tvåväg – använd sidorna (återställt så Spot-pillen påverkar premien)
            double sBid = request.SpotBid;
            double sAsk = request.SpotAsk;
            double sMid = 0.5 * (sBid + sAsk);

            // 2) RD/RF tvåväg
            double rdBid = request.RdBid, rdAsk = request.RdAsk;
            double rfBid = request.RfBid, rfAsk = request.RfAsk;
            double rdMid = 0.5 * (rdBid + rdAsk);
            double rfMid = 0.5 * (rfBid + rfAsk);

            // 3) DF till expiry (mid och scenarier)
            double DFd_exp_mid = Math.Exp(-rdMid * TdfDom_exp);
            double DFf_exp_mid = Math.Exp(-rfMid * TdfFor_exp);

            // scenBid diskonteras mot expiry med rdAsk/rfBid
            double DFd_exp_scenBid = Math.Exp(-rdAsk * TdfDom_exp);
            double DFf_exp_scenBid = Math.Exp(-rfBid * TdfFor_exp);

            // scenAsk diskonteras mot expiry med rdBid/rfAsk
            double DFd_exp_scenAsk = Math.Exp(-rdBid * TdfDom_exp);
            double DFf_exp_scenAsk = Math.Exp(-rfAsk * TdfFor_exp);

            // 4) DF för forwardperiod (för F)
            double DFd_fwd_scenBid = Math.Exp(-rdAsk * TdfDom_fwd);
            double DFf_fwd_scenBid = Math.Exp(-rfBid * TdfFor_fwd);

            double DFd_fwd_scenAsk = Math.Exp(-rdBid * TdfDom_fwd);
            double DFf_fwd_scenAsk = Math.Exp(-rfAsk * TdfFor_fwd);

            // 5) Forward per scenario – NU med spot-sida
            double F_scenBid = sBid * (DFf_fwd_scenBid / Math.Max(1e-12, DFd_fwd_scenBid));
            double F_scenAsk = sAsk * (DFf_fwd_scenAsk / Math.Max(1e-12, DFd_fwd_scenAsk));

            // 6) Mappa scenarier till Bid/Ask beroende på optionstyp
            double F_bid, F_ask;
            double DFd_exp_bid, DFf_exp_bid, DFd_exp_ask, DFf_exp_ask;

            if (isCall)
            {
                // Call: pris stiger med F → lägg lägre F på Bid
                if (F_scenBid <= F_scenAsk)
                {
                    F_bid = F_scenBid; DFd_exp_bid = DFd_exp_scenBid; DFf_exp_bid = DFf_exp_scenBid;
                    F_ask = F_scenAsk; DFd_exp_ask = DFd_exp_scenAsk; DFf_exp_ask = DFf_exp_scenAsk;
                }
                else
                {
                    F_bid = F_scenAsk; DFd_exp_bid = DFd_exp_scenAsk; DFf_exp_bid = DFf_exp_scenAsk;
                    F_ask = F_scenBid; DFd_exp_ask = DFd_exp_scenBid; DFf_exp_ask = DFf_exp_scenBid;
                }
            }
            else
            {
                // Put: pris sjunker med F → lägg högre F på Bid
                if (F_scenBid >= F_scenAsk)
                {
                    F_bid = F_scenBid; DFd_exp_bid = DFd_exp_scenBid; DFf_exp_bid = DFf_exp_scenBid;
                    F_ask = F_scenAsk; DFd_exp_ask = DFd_exp_scenAsk; DFf_exp_ask = DFf_exp_scenAsk;
                }
                else
                {
                    F_bid = F_scenAsk; DFd_exp_bid = DFd_exp_scenAsk; DFf_exp_bid = DFf_exp_scenAsk;
                    F_ask = F_scenBid; DFd_exp_ask = DFd_exp_scenBid; DFf_exp_ask = DFf_exp_scenBid;
                }
            }

            // 7) Mid-forward för greker (behåll mid för stabila greker)
            double DFd_fwd_mid = Math.Exp(-rdMid * TdfDom_fwd);
            double DFf_fwd_mid = Math.Exp(-rfMid * TdfFor_fwd);
            double F_mid = sMid * (DFf_fwd_mid / Math.Max(1e-12, DFd_fwd_mid));

            // 8) Sigma
            double sigmaMid = request.Vol.Mid;
            double sigmaBid = request.Vol.Bid ?? sigmaMid;
            double sigmaAsk = request.Vol.Ask ?? sigmaMid;

            // 9) Prisningar
            var mid = await PriceOneSigmaCoreAsync(F_mid, K, isCall, sigmaMid, Topt, sqrtT, DFd_exp_mid, DFf_exp_mid, ct).ConfigureAwait(false);
            var bidPx = await PriceOneSigmaCoreAsync(F_bid, K, isCall, sigmaBid, Topt, sqrtT, DFd_exp_bid, DFf_exp_bid, ct).ConfigureAwait(false);
            var askPx = await PriceOneSigmaCoreAsync(F_ask, K, isCall, sigmaAsk, Topt, sqrtT, DFd_exp_ask, DFf_exp_ask, ct).ConfigureAwait(false);

            return new TwoSidedPriceResult(
                premiumBid: bidPx.Premium,
                premiumMid: mid.Premium,
                premiumAsk: askPx.Premium,
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



