// FX.Services/MarketData/UsdAnchoredRateFeeder.cs
// C# 7.3
using System;
using Bloomberglp.Blpapi;        // dina importer använder detta
using FX.Core.Domain.MarketData; // TwoWay<double>, IMarketStore, View/Override/Source-typer

namespace FX.Services.MarketData
{
    /// <summary>
    /// Bygger lokala MM-räntor (rd/rf) för ett par A/B (A=base, B=quote) genom USD-ankare:
    ///  - Laddar USD-kurvan (SOFR) och upp till två FX-ben mot USD (A↔USD, USD↔B).
    ///  - Beräknar simple MM-räntor på SPOT→SETTLEMENT och matar in dem i MarketStore som feed.
    ///  - För USD-par (BASE=USD eller QUOTE=USD) används USD-kurvan direkt för den USD-sidan.
    ///
    /// Användning:
    ///   using (var feeder = new UsdAnchoredRateFeeder(store))
    ///       feeder.EnsureRdRfFor(pair6, legId, today, spotDate, settlement);
    /// </summary>
    public sealed class UsdAnchoredRateFeeder : IDisposable
    {
        private readonly IMarketStore _store;
        private Session _bbgSession;  // hanteras lokalt här
        private UsdSofrCurve _usdCurve;

        // Cache per dag (ValDate). Vi håller det superenkelt och dag-baserat.
        private static readonly object s_cacheLock = new object();
        private static DateTime s_cacheValDate = DateTime.MinValue;
        private static UsdSofrCurve s_cachedUsdCurve;          // senast laddade USD-kurva (för dagen)
        private static DateTime s_usdLoadedUtc = DateTime.MinValue; // När USD-kurvan senast laddades

        // FX-leg cache: ticker → (ben, lastLoadedUtc)
        private static readonly System.Collections.Generic.Dictionary<string, FxSwapPoints> s_fxLegCache
            = new System.Collections.Generic.Dictionary<string, FxSwapPoints>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> s_fxLegLoadedUtc
            = new System.Collections.Generic.Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // TTL (”stale”-trösklar). (kan ligga kvar som static readonly)
        private static readonly TimeSpan UsdCurveTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan FxLegTtl = TimeSpan.FromMinutes(3);


        public UsdAnchoredRateFeeder(IMarketStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Säkerställ att rd/rf finns för valt par/leg. Hämtar data från Bloomberg vid behov
        /// och skriver in som feed i MarketStore (mid ⇒ bid=ask).
        /// </summary>
        public void EnsureRdRfFor(string pair6, string legId, DateTime today, DateTime spotDate, DateTime settlement)
        {
            // Standardpolicy när denna kallas: använd cache om möjligt
            EnsureRdRfFor(pair6, legId, today, spotDate, settlement, forceRefresh: false);
        }

        /// <summary>
        /// Bygger och matar in RD/RF för par A/B (A=base, B=quote) i <see cref="IMarketStore"/>.
        /// - Använder gemensam (statisk) cache + TTL (SOFR 15 min, FX-swap 3 min) för stale-indikatorn.
        /// - <paramref name="forceRefresh"/>=true ignorerar cache och laddar alltid om från källa (Bloomberg).
        /// - Vid enbart tenor/datum-byten: kalla med <paramref name="forceRefresh"/>=false (återanvänd cache; UI ser IsStale).
        /// - Vid parbyte eller explicit ”Refresh”: <paramref name="forceRefresh"/>=true.
        /// - Viktigt: <see cref="EnsureUsdCurveCached(bool)"/> ansvarar för att starta Bloomberg-session lazily
        ///   och uppdatera den statiska cachen endast när det behövs.
        /// </summary>
        public void EnsureRdRfFor(
            string pair6,
            string legId,
            DateTime today,
            DateTime spotDate,
            DateTime settlement,
            bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(pair6)) throw new ArgumentNullException(nameof(pair6));
            if (string.IsNullOrWhiteSpace(legId)) throw new ArgumentNullException(nameof(legId));

            pair6 = NormalizePair(pair6);
            var baseCcy = pair6.Substring(0, 3);
            var quoteCcy = pair6.Substring(3, 3);

            // 1) Säkerställ att USD-kurvan finns i den STATISKA cachen (hämtar fresh vid behov/forceRefresh)
            //    OBS: EnsureUsdCurveCached() ska internt hantera sessionstart när cache saknas/stale.
            EnsureUsdCurveCached(forceRefresh);

            // 2) USD par-MM (simple) SPOT→SETTLEMENT från cachead kurva
            var usdCurve = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");
            var dfUsdSpot = Clamp01(usdCurve.DiscountFactor(spotDate));
            var dfUsdSet = Clamp01(usdCurve.DiscountFactor(settlement));
            var T_usd = YearFracMm(spotDate, settlement, "USD");
            var r_usd_par = ParMmRate(dfUsdSpot, dfUsdSet, T_usd);

            // Stale för USD utifrån TTL (statisk tidsstämpel)
            bool usdStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);

            // Hjälpare: outright forward i ”rätt” riktning
            double ForwardAt(FxSwapPoints leg, string requiredPair6, double spot)
                => leg.ForwardAt(settlement, requiredPair6, spot, dfProvider: null, useDfRatioIfAvailable: false);

            double rd_mid = 0.0;
            double rf_mid = 0.0;
            bool baseLegStale = false;
            bool quoteLegStale = false;

            if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                // BASE = USD → rf från USD-kurvan; rd via USD↔QUOTE-ben
                rf_mid = ClampRate(r_usd_par);

                DateTime loadQ;
                var legUsdQuote = GetFxLegCached("USD" + quoteCcy, quoteCcy + "USD", forceRefresh, out loadQ);
                quoteLegStale = IsStaleByTtl(loadQ, FxLegTtl);

                bool usdIsBase = legUsdQuote.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                string required = usdIsBase ? ("USD" + quoteCcy) : (quoteCcy + "USD");
                double S_q = legUsdQuote.SpotMid;
                double F_q = ForwardAt(legUsdQuote, required, S_q);

                double Tq = YearFracMm(spotDate, settlement, quoteCcy);
                double onePlus_usdT = 1.0 + r_usd_par * Math.Max(Tq, 1e-12);

                double rd_calc = usdIsBase
                    ? ((onePlus_usdT * F_q / Math.Max(S_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12)
                    : ((onePlus_usdT * Math.Max(S_q, 1e-12) / Math.Max(F_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12);

                rd_mid = ClampRate(rd_calc);
            }
            else if (string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                // QUOTE = USD → rd från USD-kurvan; rf via BASE↔USD-ben
                rd_mid = ClampRate(r_usd_par);

                DateTime loadB;
                var legBaseUsd = GetFxLegCached(baseCcy + "USD", "USD" + baseCcy, forceRefresh, out loadB);
                baseLegStale = IsStaleByTtl(loadB, FxLegTtl);

                bool usdIsQuote = legBaseUsd.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase); // "BASEUSD"
                string required = usdIsQuote ? (baseCcy + "USD") : ("USD" + baseCcy);
                double S_b = legBaseUsd.SpotMid;
                double F_b = ForwardAt(legBaseUsd, required, S_b);

                double Tb = YearFracMm(spotDate, settlement, baseCcy);
                double onePlus_usdT = 1.0 + r_usd_par * Math.Max(Tb, 1e-12);

                double rf_calc = usdIsQuote
                    ? ((onePlus_usdT * Math.Max(S_b, 1e-12) / Math.Max(F_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12)
                    : ((onePlus_usdT * F_b / Math.Max(S_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12);

                rf_mid = ClampRate(rf_calc);
            }
            else
            {
                // Korspar: BASE↔USD + USD↔QUOTE
                DateTime loadB, loadQ;
                var legBaseUsd = GetFxLegCached(baseCcy + "USD", "USD" + baseCcy, forceRefresh, out loadB);
                var legUsdQuote = GetFxLegCached("USD" + quoteCcy, quoteCcy + "USD", forceRefresh, out loadQ);
                baseLegStale = IsStaleByTtl(loadB, FxLegTtl);
                quoteLegStale = IsStaleByTtl(loadQ, FxLegTtl);

                // rf (base)
                {
                    bool usdIsQuote = legBaseUsd.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsQuote ? (baseCcy + "USD") : ("USD" + baseCcy);
                    double S_b = legBaseUsd.SpotMid;
                    double F_b = ForwardAt(legBaseUsd, required, S_b);

                    double Tb = YearFracMm(spotDate, settlement, baseCcy);
                    double onePlus_usdT = 1.0 + r_usd_par * Math.Max(Tb, 1e-12);

                    double rf_calc = usdIsQuote
                        ? ((onePlus_usdT * Math.Max(S_b, 1e-12) / Math.Max(F_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12)
                        : ((onePlus_usdT * F_b / Math.Max(S_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12);

                    rf_mid = ClampRate(rf_calc);
                }

                // rd (quote)
                {
                    bool usdIsBase = legUsdQuote.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsBase ? ("USD" + quoteCcy) : (quoteCcy + "USD");
                    double S_q = legUsdQuote.SpotMid;
                    double F_q = ForwardAt(legUsdQuote, required, S_q);

                    double Tq = YearFracMm(spotDate, settlement, quoteCcy);
                    double onePlus_usdT = 1.0 + r_usd_par * Math.Max(Tq, 1e-12);

                    double rd_calc = usdIsBase
                        ? ((onePlus_usdT * F_q / Math.Max(S_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12)
                        : ((onePlus_usdT * Math.Max(S_q, 1e-12) / Math.Max(F_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12);

                    rd_mid = ClampRate(rd_calc);
                }
            }

            // 4) Sätt IsStale om någon underliggande komponent passerat sin TTL
            bool isStale = usdStale || baseLegStale || quoteLegStale;

            var now = DateTime.UtcNow;
            _store.SetRdFromFeed(pair6, legId, new TwoWay<double>(rd_mid, rd_mid), now, isStale);
            _store.SetRfFromFeed(pair6, legId, new TwoWay<double>(rf_mid, rf_mid), now, isStale);
        }




        // ====================== Bloomberg & helpers ======================

        private void EnsureBloombergSession()
        {
            if (_bbgSession != null) return;
            var opts = new SessionOptions { ServerHost = "localhost", ServerPort = 8194 };
            _bbgSession = new Session(opts);
            if (!_bbgSession.Start()) throw new Exception("BLP session start failed.");
            if (!_bbgSession.OpenService("//blp/refdata")) throw new Exception("Open //blp/refdata failed.");
        }

        private void EnsureUsdCurveLoaded()
        {
            if (_usdCurve != null && _usdCurve.ValDate.Date == DateTime.Today) return;
            _usdCurve = new UsdSofrCurve();
            _usdCurve.LoadFromBloomberg(_bbgSession);
        }

        private FxSwapPoints GetFxLeg(string preferredTickerPair6, string altTickerPair6)
        {
            FxSwapPoints TryLoad(string pair6)
            {
                var leg = new FxSwapPoints();
                leg.LoadFromBloomberg(_bbgSession, pair6 + " BGN Curncy", ResolveSpotDateForPair);
                return leg;
            }

            try { return TryLoad(preferredTickerPair6); }
            catch { return TryLoad(altTickerPair6); }
        }

        /// <summary>
        /// Enkel T+2 Following-resolver (räcker för att läsa BGN-ben).
        /// </summary>
        private DateTime ResolveSpotDateForPair(string pair6)
        {
            pair6 = NormalizePair(pair6);

            bool IsBiz(DateTime d) => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;

            DateTime cur = DateTime.Today;
            int added = 0;
            while (added < 2) { cur = cur.AddDays(1); if (IsBiz(cur)) added++; }
            while (!IsBiz(cur)) cur = cur.AddDays(1);
            return cur.Date;
        }

        private static string NormalizePair(string pair6)
        {
            return (pair6 ?? "").Replace("/", "").ToUpperInvariant();
        }

        private static int MoneyMarketDenomForCcy(string ccy)
        {
            switch ((ccy ?? "").ToUpperInvariant())
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

        private static double YearFracMm(DateTime start, DateTime end, string ccy)
        {
            int denom = MoneyMarketDenomForCcy(ccy);
            double days = (end.Date - start.Date).TotalDays;
            if (days < 0) days = 0;
            return days / Math.Max(1.0, denom);
        }

        private static double ParMmRate(double dfStart, double dfEnd, double yearFracMm)
        {
            double dfFwd = Clamp01(dfEnd / Math.Max(1e-12, dfStart));
            double T = yearFracMm > 1e-12 ? yearFracMm : 1e-12;
            return (1.0 / dfFwd - 1.0) / T;
        }

        private static double Clamp01(double x)
        {
            if (x < 1e-12) return 1e-12;
            if (x > 1.0) return 1.0;
            return x;
        }

        private static double ClampRate(double r)
        {
            if (double.IsNaN(r) || double.IsInfinity(r)) return 0.0;
            if (r < -0.99) r = -0.99;   // skydd mot divisionsfel
            if (r > 10.0) r = 10.0;    // orimliga outliers
            return r;
        }

        public void Dispose()
        {
            try { if (_bbgSession != null) _bbgSession.Stop(); }
            catch { /* tyst stängning */ }
            _bbgSession = null;
        }

        /// <summary>
        /// En enkel stale-definition: data är ”färsk” om ValDate == DateTime.Today.
        /// Allt annat = stale.
        /// </summary>
        private static bool IsStale(DateTime valDate) => valDate.Date != DateTime.Today;

        /// <summary>
        /// Returnerar true om datan är ”stale” givet när den laddades och en TTL.
        /// </summary>
        private static bool IsStaleByTtl(DateTime loadedUtc, TimeSpan ttl)
        {
            if (loadedUtc == DateTime.MinValue) return true;
            return (DateTime.UtcNow - loadedUtc) > ttl;
        }

        /// <summary>
        /// Säkerställer att vi har en USD-kurva i cache. 
        /// - Använder cache om forceRefresh=false och TTL ej passerad.
        /// - Laddar om från Bloomberg annars.
        /// - Sätter _usdLoadedUtc för stale-bedömning.
        /// </summary>
        void EnsureUsdCurveCached(bool forceRefresh)
        {
            // 1) Försök använda befintlig cache
            lock (s_cacheLock)
            {
                bool canUseCache =
                    !forceRefresh &&
                    s_cachedUsdCurve != null &&
                    s_cacheValDate.Date == DateTime.Today &&
                    !IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);

                if (canUseCache)
                    return;
            }

            // 2) Behöver fylla/uppdatera cache → säkerställ session lokalt
            EnsureBloombergSession();

            // 3) Ladda färsk USD-kurva och uppdatera cache (låst sektion)
            var fresh = new UsdSofrCurve();
            fresh.LoadFromBloomberg(_bbgSession);

            lock (s_cacheLock)
            {
                s_cachedUsdCurve = fresh;
                s_cacheValDate = DateTime.Today;
                s_usdLoadedUtc = DateTime.UtcNow;
            }
        }



        /// <summary>
        /// Hämtar ett FX-ben (forward/points) ur cache om möjligt (forceRefresh=false och TTL ej passerad).
        /// Annars laddas från Bloomberg. Returnerar även lastLoadedUtc för stale-indikatorn.
        /// Vi försöker 'preferred' pair6 först, annars 'alt'.
        /// </summary>
        private FxSwapPoints GetFxLegCached(string preferredPair6, string altPair6, bool forceRefresh, out DateTime lastLoadedUtc)
        {
            FxSwapPoints LoadFromBbg(string pair6)
            {
                // Sesion endast när vi MÅSTE ladda
                EnsureBloombergSession();
                var leg = new FxSwapPoints();
                leg.LoadFromBloomberg(_bbgSession, pair6 + " BGN Curncy", ResolveSpotDateForPair);
                return leg;
            }

            string keyPreferred = (preferredPair6 + " BGN Curncy").ToUpperInvariant();
            string keyAlt = (altPair6 + " BGN Curncy").ToUpperInvariant();

            // 1) Cache-hit om möjligt
            lock (s_cacheLock)
            {
                if (!forceRefresh && s_fxLegCache.TryGetValue(keyPreferred, out var cachedA) && cachedA != null)
                {
                    var loadedA = s_fxLegLoadedUtc.TryGetValue(keyPreferred, out var tA) ? tA : DateTime.MinValue;
                    if (!IsStaleByTtl(loadedA, FxLegTtl))
                    {
                        lastLoadedUtc = loadedA;
                        return cachedA;
                    }
                }
                if (!forceRefresh && s_fxLegCache.TryGetValue(keyAlt, out var cachedB) && cachedB != null)
                {
                    var loadedB = s_fxLegLoadedUtc.TryGetValue(keyAlt, out var tB) ? tB : DateTime.MinValue;
                    if (!IsStaleByTtl(loadedB, FxLegTtl))
                    {
                        lastLoadedUtc = loadedB;
                        return cachedB;
                    }
                }
            }

            // 2) Cache miss/stale → välj vilken som ska laddas
            FxSwapPoints legLoaded;
            string usedKey;
            try
            {
                legLoaded = LoadFromBbg(preferredPair6);
                usedKey = keyPreferred;
            }
            catch
            {
                legLoaded = LoadFromBbg(altPair6);
                usedKey = keyAlt;
            }

            // 3) Uppdatera cache (tråd-säkert)
            lock (s_cacheLock)
            {
                s_fxLegCache[usedKey] = legLoaded;
                s_fxLegLoadedUtc[usedKey] = DateTime.UtcNow;
                lastLoadedUtc = s_fxLegLoadedUtc[usedKey];
            }
            return legLoaded;
        }


        /// <summary>
        /// (NY) Diagnostik för USD-ränta från SOFR-kurvan på egna datum.
        /// Visar DF(spot), DF(settlement), framåtdiskonteringskvot, ACT/360-årsfaktor,
        /// par MM-ränta (decimal och %) samt jämförelse mot närmsta kurvpelare och ev. 1M-pelaren.
        /// Ändrar ingen state; skriver även rader till Debug.
        /// </summary>
        /// <param name="pair6">Par (t.ex. "USDSEK") – endast för utskrift.</param>
        /// <param name="spotDate">Spot-datum för perioden.</param>
        /// <param name="settlement">Delivery/settlement-datum för perioden.</param>
        /// <param name="forceRefresh">
        /// true = säkerställ färsk USD-kurva (ignorera cache/TTL); false = använd cache om giltig.
        /// </param>
        /// <returns>En färdig-formaterad diagnostiksträng.</returns>
        public string DumpUsdRfDiagnostics(string pair6, DateTime spotDate, DateTime settlement, bool forceRefresh = false)
        {
            // Säkerställ kurvan i cachen (kan starta BLP-session internt vid behov).
            EnsureUsdCurveCached(forceRefresh); // använder klassens cachepolicy. :contentReference[oaicite:0]{index=0}

            var usd = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Basdata för perioden Spot→Settlement
            double dfSpot = Clamp01(usd.DiscountFactor(spotDate));
            double dfSet = Clamp01(usd.DiscountFactor(settlement));
            double Tmm = YearFracMm(spotDate, settlement, "USD");     // ACT/360 för USD. :contentReference[oaicite:1]{index=1}
            double rPar = ParMmRate(dfSpot, dfSet, Tmm);               // enkel MM-parränta. :contentReference[oaicite:2]{index=2}
            double dfFwd = Clamp01(dfSet / Math.Max(1e-12, dfSpot));

            // Hitta närmaste pelare (datum) i kurvan i förhållande till settlement
            DateTime nearestDate = DateTime.MinValue;
            string nearestTenor = "";
            double nearestAbsDays = double.MaxValue;

            var pillars = usd.Pillars;                                   // DataTable: Date, Tenor, ZeroMid, DF. :contentReference[oaicite:3]{index=3}
            if (pillars != null)
            {
                foreach (System.Data.DataRow row in pillars.Rows)
                {
                    var d = System.Convert.ToDateTime(row["Date"], inv).Date;
                    double absDays = Math.Abs((d - settlement.Date).TotalDays);
                    if (absDays < nearestAbsDays)
                    {
                        nearestAbsDays = absDays;
                        nearestDate = d;
                        nearestTenor = (pillars.Columns.Contains("Tenor") ? System.Convert.ToString(row["Tenor"] ?? "", inv) : "");
                    }
                }
            }

            // Par-ränta till närmaste pelardatum räknad med våra SPOT→pelare-datum
            double rNear = double.NaN;
            if (nearestDate != DateTime.MinValue)
            {
                double dfNear = Clamp01(usd.DiscountFactor(nearestDate));
                double Tnear = YearFracMm(spotDate, nearestDate, "USD");
                rNear = ParMmRate(dfSpot, dfNear, Tnear);
            }

            // Försök även läsa ren "1M"-pelare om den existerar i tabellen
            DateTime oneMDate = DateTime.MinValue;
            double rOneM = double.NaN;
            if (pillars != null && pillars.Columns.Contains("Tenor"))
            {
                foreach (System.Data.DataRow row in pillars.Rows)
                {
                    var tn = System.Convert.ToString(row["Tenor"] ?? "", inv);
                    if (string.Equals(tn?.Trim(), "1M", StringComparison.OrdinalIgnoreCase))
                    {
                        oneMDate = System.Convert.ToDateTime(row["Date"], inv).Date;
                        double df1m = Clamp01(usd.DiscountFactor(oneMDate));
                        double T1m = YearFracMm(spotDate, oneMDate, "USD");
                        rOneM = ParMmRate(dfSpot, df1m, T1m);
                        break;
                    }
                }
            }

            // Stale-bedomning från cachetidsstämplar
            bool usdStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);   // :contentReference[oaicite:4]{index=4}

            // Skriv ut
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine("[USD SOFR Diagnostics]");
            sb.AppendLine("Pair: " + (pair6 ?? ""));
            sb.AppendLine("ValDate: " + usd.ValDate.ToString("yyyy-MM-dd", inv));
            sb.AppendLine("Spot: " + spotDate.ToString("yyyy-MM-dd", inv) + "  Settlement: " + settlement.ToString("yyyy-MM-dd", inv));
            sb.AppendLine("ACT/360 (T): " + Tmm.ToString("F6", inv));
            sb.AppendLine("DF(spot): " + dfSpot.ToString("F9", inv) + "   DF(settle): " + dfSet.ToString("F9", inv));
            sb.AppendLine("Fwd DF ratio: " + dfFwd.ToString("F9", inv));
            sb.AppendLine("Par MM rate (decimal): " + rPar.ToString("F6", inv) + "   (" + (rPar * 100.0).ToString("F3", inv) + " %)");
            if (nearestDate != DateTime.MinValue)
            {
                sb.AppendLine("Nearest pillar: " + (string.IsNullOrWhiteSpace(nearestTenor) ? "(n/a)" : nearestTenor)
                    + " @ " + nearestDate.ToString("yyyy-MM-dd", inv)
                    + " → par: " + rNear.ToString("F6", inv) + " (" + (rNear * 100.0).ToString("F3", inv) + " %)");
            }
            if (oneMDate != DateTime.MinValue)
            {
                sb.AppendLine("1M pillar @ " + oneMDate.ToString("yyyy-MM-dd", inv)
                    + " → par: " + rOneM.ToString("F6", inv) + " (" + (rOneM * 100.0).ToString("F3", inv) + " %)");
            }
            sb.AppendLine("Cache: loadedUtc=" + s_usdLoadedUtc.ToString("yyyy-MM-dd HH:mm:ss", inv)
                + "  ttl=" + UsdCurveTtl.TotalMinutes.ToString("F0", inv) + "m  stale=" + (usdStale ? "YES" : "NO"));

            var s = sb.ToString();
            System.Diagnostics.Debug.WriteLine(s);
            return s;
        }



        /// <summary>
        /// (ERSÄTT) Diagnostik för korspar BASE/QUOTE (t.ex. EUR/SEK):
        /// - Hämtar båda USD-benen (BASEUSD och USDQUOTE) med valfri forceRefresh.
        /// - Loggar "native" spot och forward från respektive ben, inkl. deras lasttidstämplar.
        /// - Räknar USD-par (Spot→Settle) ur SOFR-kurvan (forward-konsistent).
        /// - Löser rd och rf från dessa S/F och visar båda i procent.
        /// Obs: Ändrar inget state.
        /// </summary>
        /// <param name="baseCcy">Tre bokstäver (t.ex. "EUR").</param>
        /// <param name="quoteCcy">Tre bokstäver (t.ex. "SEK").</param>
        /// <param name="settlement">Leveransdatum.</param>
        /// <param name="commonSpotOverride">
        /// Valfritt: om satt, används detta som <em>spotOverride</em> i ForwardAt(...) för BÅDA benen
        /// (endast för jämförelse). Använd med försiktighet – numeriskt rätt är egentligen att
        /// ge respektive bens egen spot från samma tidsstämplade källa.
        /// </param>
        /// <param name="forceRefresh">true för att tvinga nyhämtning av båda benen innan logg.</param>
        /// <returns>Formatterad diagnostiksträng som också skrivs till Debug.</returns>
        public string DumpCrossRfSnapshot(string baseCcy, string quoteCcy, DateTime settlement, double? commonSpotOverride = null, bool forceRefresh = false)
        {
            // 1) Hämta båda USD-benen (BASE→USD och USD→QUOTE) med samma stale-policy.
            DateTime loadB, loadQ;
            var legB = GetFxLegCached(baseCcy, "USD", forceRefresh, out loadB);   // BASE→USD
            var legQ = GetFxLegCached("USD", quoteCcy, forceRefresh, out loadQ);  // USD→QUOTE

            // 2) "Native" spots (från respektive BGN-svar)
            double sB_native = legB.SpotMid;
            double sQ_native = legQ.SpotMid;

            // 3) Forward enligt prisning (utan att röra state)
            double fB_native = legB.ForwardAt(settlement, legB.Pair6, sB_native);
            double fQ_native = legQ.ForwardAt(settlement, legQ.Pair6, sQ_native);

            // 4) USD MM-par (Spot→Settlement) enligt SOFR-kurvan (forward-konsistent)
            EnsureUsdCurveCached(false);
            var usd = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Lokal helper: T+2 helg-only (endast för diagnos; ingen extern kalender krävs)
            DateTime SpotFromValDateWeekendOnly(DateTime valDate)
            {
                bool IsBiz(DateTime d) => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;
                var d = valDate.Date; int added = 0;
                while (added < 2) { d = d.AddDays(1); if (IsBiz(d)) added++; }
                return d;
            }

            DateTime val = usd.ValDate.Date;
            DateTime spot = SpotFromValDateWeekendOnly(val);
            double T = Math.Max(0.0, (settlement.Date - spot.Date).TotalDays) / 360.0;
            double dfSpot = Clamp01(usd.DiscountFactor(spot));
            double dfSet = Clamp01(usd.DiscountFactor(settlement));
            double fwdDf = dfSet / Math.Max(1e-12, dfSpot);
            double rUsd = (1.0 / Math.Max(1e-12, fwdDf) - 1.0) / Math.Max(1e-12, T);

            // 5) Lös rd och rf från native S/F
            double rd_native = ((1.0 + rUsd * T) * (fB_native / Math.Max(1e-12, sB_native)) - 1.0) / Math.Max(1e-12, T);
            double rf_native = ((1.0 + rUsd * T) * (sQ_native / Math.Max(1e-12, fQ_native)) - 1.0) / Math.Max(1e-12, T);

            // 6) (Valfritt) gemensam spot-override (samma tal skickas in till båda benen)
            double? rd_common = null, rf_common = null;
            if (commonSpotOverride.HasValue)
            {
                double sCommon = commonSpotOverride.Value;
                double fB_common = legB.ForwardAt(settlement, legB.Pair6, sCommon);
                double fQ_common = legQ.ForwardAt(settlement, legQ.Pair6, sCommon);

                rd_common = ((1.0 + rUsd * T) * (fB_common / Math.Max(1e-12, sCommon)) - 1.0) / Math.Max(1e-12, T);
                rf_common = ((1.0 + rUsd * T) * (sCommon / Math.Max(1e-12, fQ_common)) - 1.0) / Math.Max(1e-12, T);
            }

            // 7) Utskrift
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine("[Cross RF Snapshot]");
            sb.AppendLine("Pair: " + baseCcy + quoteCcy + "   Settle=" + settlement.ToString("yyyy-MM-dd", inv));
            sb.AppendLine("BaseUSD loadUtc=" + loadB.ToString("yyyy-MM-dd HH:mm:ss", inv) + "   USDQuote loadUtc=" + loadQ.ToString("yyyy-MM-dd HH:mm:ss", inv));
            sb.AppendLine("USD MM (Spot→Settle): Spot=" + spot.ToString("yyyy-MM-dd", inv) +
                          "  T=" + T.ToString("F6", inv) + "  rUsd=" + (rUsd * 100.0).ToString("F3", inv) + " %");
            sb.AppendLine("BASEUSD : S_native=" + sB_native.ToString("F6", inv) + "   F_native=" + fB_native.ToString("F6", inv));
            sb.AppendLine("USDQUOTE: S_native=" + sQ_native.ToString("F6", inv) + "   F_native=" + fQ_native.ToString("F6", inv));
            sb.AppendLine("Solved (native): rd=" + (rd_native * 100.0).ToString("F3", inv) + " %   rf=" + (rf_native * 100.0).ToString("F3", inv) + " %");

            if (commonSpotOverride.HasValue)
            {
                sb.AppendLine("CommonSpot=" + commonSpotOverride.Value.ToString("F6", inv) + " → applied to BOTH legs for test");
                sb.AppendLine("Solved (common): rd=" + ((rd_common ?? 0) * 100.0).ToString("F3", inv) + " %   rf=" + ((rf_common ?? 0) * 100.0).ToString("F3", inv) + " %");
            }

            var s = sb.ToString();
            System.Diagnostics.Debug.WriteLine(s);
            return s;
        }


        /// <summary>
        /// (NY) Bekvämlighetswrapper som bara skriver ut korspar-snapshot till Debug.
        /// </summary>
        public void DebugLogCrossRfSnapshot(string baseCcy, string quoteCcy, DateTime settlement, double? commonSpotOverride = null, bool forceRefresh = false)
        {
            DumpCrossRfSnapshot(baseCcy, quoteCcy, settlement, commonSpotOverride, forceRefresh);
        }



    }
}
