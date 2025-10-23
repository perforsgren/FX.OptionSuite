// FX.Services/MarketData/UsdAnchoredRateFeeder.cs
// C# 7.3
using System;
using System.Collections.Generic;
using System.Linq;
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
        //private UsdSofrCurve _usdCurve;

        // Cache per dag (ValDate). Vi håller det superenkelt och dag-baserat.
        private static readonly object s_cacheLock = new object();
        private static DateTime s_cacheValDate = DateTime.MinValue;
        private static UsdSofrCurve s_cachedUsdCurve;          // senast laddade USD-kurva (för dagen)
        private static DateTime s_usdLoadedUtc = DateTime.MinValue; // När USD-kurvan senast laddades

        // FX-leg cache: ticker → (ben, lastLoadedUtc)
        private static readonly Dictionary<string, FxSwapPoints> s_fxLegCache = new Dictionary<string, FxSwapPoints>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> s_fxLegLoadedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // TTL (”stale”-trösklar). (kan ligga kvar som static readonly)
        private static readonly TimeSpan UsdCurveTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan FxLegTtl = TimeSpan.FromMinutes(3);

        /// <summary>
        /// (NY) BGNE-spot per Pair6 som ”ska användas” vid forwardberäkning.
        /// Fylls i samband med atomisk kors-laddning; används som override-param i ForwardAt.
        /// </summary>
        private readonly Dictionary<string, double> _bgneSpotOverride = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);


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
        public void EnsureRdRfForOLD(
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



        #region === Pricing ===

        /// <summary>
        /// (ERSÄTT) Säkerställ RD/RF i store för ett valutapar (A/B) vid givet settlement.
        /// Ny funktionalitet i denna version:
        /// 1) **No-reload-on-stale**: USD-kurvan och FX-ben laddas **inte om** när cache är stale
        ///    (om inte <paramref name="forceRefresh"/> är true eller policy säger annat). Detta gör att
        ///    byte av enbart expiry använder cache (framåtberäkning ur cached kurva) istället för ny feed-hit.
        /// 2) **Cache-först korspar**: För korspar hämtas båda USD-benen först från cache; om något saknas
        ///    (eller om explicit refresh) laddas båda ben **atomiskt** i EN BLP-request via routing.
        /// 3) **Common spot (valfritt)**: Om flaggan UseCommonSpotForCross är true trianguleras S_common =
        ///    S(BASE→USD) * S(USD→QUOTE) och används som spot i båda ForwardAt(...)-anropen för att minska rf-brus.
        /// Övrigt (SOFR→USD-parrate, formler, store-skrivning) är oförändrat.
        /// </summary>
        /// <param name="pair6">Valutapar i 6 tecken, t.ex. "EURSEK". "/" ignoreras.</param>
        /// <param name="legId">Leg-ID som används i store.</param>
        /// <param name="today">Dagens datum (valdate).</param>
        /// <param name="spotDate">Spotdatum (USD T+2 enligt dina kalendrar).</param>
        /// <param name="settlement">Leveransdatum.</param>
        /// <param name="forceRefresh">True för att tvinga ny hämtning från källa; annars cache-först utan reload på stale.</param>
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

            // --- USD parrate (SOFR) — relaxed cache ---
            EnsureUsdCurveCachedRelaxed(forceRefresh);
            var usdCurve = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");

            var dfSpot = Clamp01(usdCurve.DiscountFactor(spotDate));
            var dfSet = Clamp01(usdCurve.DiscountFactor(settlement));
            var T_usd = YearFracMm(spotDate, settlement, "USD");
            var r_usd = (1.0 / Math.Max(1e-12, (dfSet / Math.Max(1e-12, dfSpot))) - 1.0) / Math.Max(1e-12, T_usd);

            // Endast markering (ingen implicit reload)
            bool usdStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);

            double rd_mid = 0.0, rf_mid = 0.0;
            bool baseLegStale = false, quoteLegStale = false;

            // --- USD-par ---
            if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                DateTime loadUtc;
                FxSwapPoints leg;

                if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    // USD/QUOTE → kandidater "USD{Q}" och "{Q}USD"
                    leg = GetFxLegCached("USD" + quoteCcy, quoteCcy + "USD", forceRefresh, out loadUtc);
                    quoteLegStale = IsStaleByTtl(loadUtc, FxLegTtl);
                }
                else
                {
                    // BASE/USD → kandidater "{B}USD" och "USD{B}"
                    leg = GetFxLegCached(baseCcy + "USD", "USD" + baseCcy, forceRefresh, out loadUtc);
                    baseLegStale = IsStaleByTtl(loadUtc, FxLegTtl);
                }

                double S = leg.SpotMid; // benets egen spot (BGN)
                                        // Viktigt: explicit spotOverride och DF-ratio AV, som i OLD
                double F = leg.ForwardAt(settlement, leg.Pair6, S, dfProvider: null, useDfRatioIfAvailable: false);

                if (!(S > 0.0) || !(F > 0.0) || double.IsNaN(S) || double.IsNaN(F) || double.IsInfinity(S) || double.IsInfinity(F))
                {
                    System.Diagnostics.Debug.WriteLine("[Guardrail] USD leg unusable (spot/fwd). Skip write.");
                    return;
                }

                var other = string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ? quoteCcy : baseCcy;
                double T = YearFracMm(spotDate, settlement, other);
                double onePlusUsdT = 1.0 + r_usd * Math.Max(T, 1e-12);

                if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    // USD/QUOTE → lös rd (quote), rf = r_usd
                    rd_mid = ClampRate(((onePlusUsdT * Math.Max(F, 1e-12) / Math.Max(S, 1e-12)) - 1.0) / Math.Max(T, 1e-12));
                    rf_mid = ClampRate(r_usd);
                }
                else
                {
                    // BASE/USD → lös rf (base), rd = r_usd
                    rf_mid = ClampRate(((onePlusUsdT * Math.Max(S, 1e-12) / Math.Max(F, 1e-12)) - 1.0) / Math.Max(T, 1e-12));
                    rd_mid = ClampRate(r_usd);
                }

                var nowUsd = DateTime.UtcNow;
                bool staleMarkUsd = usdStale || baseLegStale || quoteLegStale;
                _store.SetRdFromFeed(pair6, legId, new TwoWay<double>(rd_mid, rd_mid), nowUsd, staleMarkUsd);
                _store.SetRfFromFeed(pair6, legId, new TwoWay<double>(rf_mid, rf_mid), nowUsd, staleMarkUsd);
                return;
            }

            // --- KORSPAR: cache-först; vid behov atomisk multi-load. Använd benens egna spots. ---
            {
                DateTime loadBothUtc;
                bool fromCache;
                FxSwapPoints legBaseUsd, legUsdQuote;

                GetCrossLegsPreferCacheThenAtomic(
                    baseCcy, quoteCcy, forceRefresh,
                    out legBaseUsd, out legUsdQuote,
                    out loadBothUtc, out fromCache);

                bool legsStale = IsStaleByTtl(loadBothUtc, FxLegTtl);

                // ---- rf (BASE) ur BASE↔USD + r_USD ----
                {
                    bool usdIsQuote = legBaseUsd.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsQuote ? (baseCcy + "USD") : ("USD" + baseCcy);

                    double S_b = legBaseUsd.SpotMid; // benets egen spot
                    double F_b = legBaseUsd.ForwardAt(settlement, required, S_b, dfProvider: null, useDfRatioIfAvailable: false);

                    if (!(S_b > 0.0) || !(F_b > 0.0) || double.IsNaN(S_b) || double.IsNaN(F_b) || double.IsInfinity(S_b) || double.IsInfinity(F_b))
                    {
                        System.Diagnostics.Debug.WriteLine("[Guardrail] BASE→USD unusable (spot/fwd). Skip write.");
                        return;
                    }

                    double Tb = YearFracMm(spotDate, settlement, baseCcy);
                    double onePlus = 1.0 + r_usd * Math.Max(Tb, 1e-12);

                    double rf_calc = usdIsQuote
                        ? ((onePlus * Math.Max(S_b, 1e-12) / Math.Max(F_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12)
                        : ((onePlus * F_b / Math.Max(S_b, 1e-12)) - 1.0) / Math.Max(Tb, 1e-12);

                    rf_mid = ClampRate(rf_calc);
                }

                // ---- rd (QUOTE) ur USD↔QUOTE + r_USD ----
                {
                    bool usdIsBase = legUsdQuote.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsBase ? ("USD" + quoteCcy) : (quoteCcy + "USD");

                    double S_q = legUsdQuote.SpotMid; // benets egen spot
                    double F_q = legUsdQuote.ForwardAt(settlement, required, S_q, dfProvider: null, useDfRatioIfAvailable: false);

                    if (!(S_q > 0.0) || !(F_q > 0.0) || double.IsNaN(S_q) || double.IsNaN(F_q) || double.IsInfinity(S_q) || double.IsInfinity(F_q))
                    {
                        System.Diagnostics.Debug.WriteLine("[Guardrail] USD→QUOTE unusable (spot/fwd). Skip write.");
                        return;
                    }

                    double Tq = YearFracMm(spotDate, settlement, quoteCcy);
                    double onePlus = 1.0 + r_usd * Math.Max(Tq, 1e-12);

                    double rd_calc = usdIsBase
                        ? ((onePlus * F_q / Math.Max(S_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12)
                        : ((onePlus * Math.Max(S_q, 1e-12) / Math.Max(F_q, 1e-12)) - 1.0) / Math.Max(Tq, 1e-12);

                    rd_mid = ClampRate(rd_calc);
                }

                var now = DateTime.UtcNow;
                bool staleMark = usdStale || legsStale;
                _store.SetRdFromFeed(pair6, legId, new TwoWay<double>(rd_mid, rd_mid), now, staleMark);
                _store.SetRfFromFeed(pair6, legId, new TwoWay<double>(rf_mid, rf_mid), now, staleMark);
            }
        }

        /// <summary>
        /// (NY) Guardrail: kontrollera att S och F är >0 och ej NaN/∞ innan de används/skribas.
        /// </summary>
        private static bool IsLegUsable(double spot, double fwd)
        {
            if (!(spot > 0.0)) return false;
            if (!(fwd > 0.0)) return false;
            if (double.IsNaN(spot) || double.IsInfinity(spot)) return false;
            if (double.IsNaN(fwd) || double.IsInfinity(fwd)) return false;
            return true;
        }

        #endregion

        #region USD curve cache helpers

        /// <summary>
        /// (NY) Säkerställ USD-kurvan i cache utan att automatiskt ladda om när den är stale.
        /// - Vid första körning (null): laddar.
        /// - Vid forceRefresh: laddar.
        /// - Vid stale men !forceRefresh och ReloadOnStale==false: återanvänder cachen som den är.
        /// </summary>
        /// <param name="forceRefresh">True = ladda oavsett.</param>
        private void EnsureUsdCurveCachedRelaxed(bool forceRefresh)
        {
            // Första gång (saknas cache) → ladda.
            if (s_cachedUsdCurve == null)
            {
                EnsureUsdCurveCached(true);
                return;
            }

            // Explicit force → ladda.
            if (forceRefresh)
            {
                EnsureUsdCurveCached(true);
                return;
            }

            // Stale? Ladda endast om policyn kräver det.
            var isStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);
            if (isStale && ReloadOnStale)
                EnsureUsdCurveCached(true);

            // Annars gör vi inget: återanvänder cached USD-kurva som kan vara stale.
        }

        #endregion


        #region === Bloomberg & helpers ===

        private void EnsureBloombergSession()
        {
            if (_bbgSession != null) return;
            var opts = new SessionOptions { ServerHost = "localhost", ServerPort = 8194 };
            _bbgSession = new Session(opts);
            if (!_bbgSession.Start()) throw new Exception("BLP session start failed.");
            if (!_bbgSession.OpenService("//blp/refdata")) throw new Exception("Open //blp/refdata failed.");
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
        /// (ERSÄTT) Laddar korsparens två ben atomiskt (skew ≈ 0s) enligt route-dictionaryn:
        /// - Bygger en lista med kandidater (BASE→USD + USD→QUOTE) per dina routes.
        /// - Skickar EN ReferenceDataRequest med alla kandidater (äkta snapshot).
        /// - Väljer per kategori (BASE→USD / USD→QUOTE) den första giltiga som fanns i svaret.
        /// - Uppdaterar cachen för de VALDA tickers med SAMMA loadedUtc.
        /// Fix: undviker CS1628 genom att INTE captura out-parametern 'loadedUtc' i en lokal funktion.
        /// </summary>
        /// <param name="baseCcy">BASE, t.ex. "EUR".</param>
        /// <param name="quoteCcy">QUOTE, t.ex. "SEK".</param>
        /// <param name="legBaseUsd">Ut: valt BASE→USD-ben.</param>
        /// <param name="legUsdQuote">Ut: valt USD→QUOTE-ben.</param>
        /// <param name="loadedUtc">Ut: gemensam lasttid (UTC) för båda.</param>
        private void LoadCrossLegsAtomicByRoute(
            string baseCcy,
            string quoteCcy,
            out FxSwapPoints legBaseUsd,
            out FxSwapPoints legUsdQuote,
            out DateTime loadedUtc)
        {
            if (string.IsNullOrWhiteSpace(baseCcy)) throw new ArgumentNullException(nameof(baseCcy));
            if (string.IsNullOrWhiteSpace(quoteCcy)) throw new ArgumentNullException(nameof(quoteCcy));

            EnsureBloombergSession();

            // 1) Hämta route för paret
            var route = GetRouteOrDefault(baseCcy, quoteCcy);

            // 2) Samla alla kandidater (unika, bevarad ordning)
            var all = new System.Collections.Generic.List<string>(8);
            void AddRangeUnique(string[] arr)
            {
                if (arr == null) return;
                foreach (var t in arr)
                {
                    var s = (t ?? "").Trim();
                    if (s.Length == 0) continue;
                    // OBS: List<T>.Contains saknar comparer-overload i .NET → använd Exists + StringComparer
                    if (!all.Exists(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
                        all.Add(s);
                }
            }
            AddRangeUnique(route.BaseUsdCandidates);
            AddRangeUnique(route.UsdQuoteCandidates);

            // 3) Atomisk multiläsning (EN request)
            var dict = FxSwapPoints.LoadMultipleFromBloombergAtomic(
                _bbgSession,
                all,
                ResolveSpotDateForPair); // din befintliga tenor->datum fallback

            // 4) Välj första "giltiga" för BASE→USD respektive USD→QUOTE
            FxSwapPoints FirstValid(string[] candidates)
            {
                if (candidates == null) return null;
                foreach (var c in candidates)
                {
                    var key = c?.Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    if (dict.TryGetValue(key, out var leg) && FxSwapPoints.IsValidLeg(leg))
                        return leg;
                }
                return null;
            }

            legBaseUsd = FirstValid(route.BaseUsdCandidates);
            legUsdQuote = FirstValid(route.UsdQuoteCandidates);

            if (legBaseUsd == null || legUsdQuote == null)
            {
                string missB = legBaseUsd == null ? string.Join(" | ", route.BaseUsdCandidates ?? new string[0]) : "OK";
                string missQ = legUsdQuote == null ? string.Join(" | ", route.UsdQuoteCandidates ?? new string[0]) : "OK";
                throw new InvalidOperationException("Atomic cross load: missing leg(s). BASE→USD candidates: " + missB + "  USD→QUOTE candidates: " + missQ);
            }

            // 5) Gemensam lasttid och cache-uppdatering
            loadedUtc = DateTime.UtcNow;

            // Viktigt: skicka tidsstämpeln in som PARAMETER till hjälparen
            void PutCache(FxSwapPoints leg, string overrideTicker, DateTime ts)
            {
                var tk = (overrideTicker ?? leg.PairTicker ?? "").ToUpperInvariant();
                if (tk.Length == 0) return;
                lock (s_cacheLock)
                {
                    s_fxLegCache[tk] = leg;
                    s_fxLegLoadedUtc[tk] = ts;   // ← INTE 'loadedUtc' från ytter-scope
                }
            }

            PutCache(legBaseUsd, legBaseUsd.PairTicker, loadedUtc);
            PutCache(legUsdQuote, legUsdQuote.PairTicker, loadedUtc);

            System.Diagnostics.Debug.WriteLine(
                $"[AtomicCross][OK] base={baseCcy} quote={quoteCcy}  legs=({legBaseUsd.PairTicker}) & ({legUsdQuote.PairTicker})  loadedUtc={loadedUtc:yyyy-MM-dd HH:mm:ss}");
        }

        /// <summary>
        /// (NY) För korspar: Försök hämta båda USD-benen från cache om de är färska (TTL).
        /// Om forceRefresh==true, eller om något ben saknas/är stale → hämta atomiskt i EN request
        /// via routing (<see cref="LoadCrossLegsAtomicByRoute"/>). Returnerar även om legs kom
        /// från cache eller inte.
        /// </summary>
        /// <param name="baseCcy">BASE, t.ex. "EUR".</param>
        /// <param name="quoteCcy">QUOTE, t.ex. "SEK".</param>
        /// <param name="forceRefresh">true = hoppa över cache och ladda atomiskt.</param>
        /// <param name="legBaseUsd">Ut: valt BASE→USD-ben.</param>
        /// <param name="legUsdQuote">Ut: valt USD→QUOTE-ben.</param>
        /// <param name="loadedUtc">
        /// Ut: tidsstämpel. Vid cache-väg: min(loadBase, loadQuote). Vid atomisk väg: gemensam tid.
        /// </param>
        /// <param name="fromCache">Ut: true om båda ben togs från cache och var färska.</param>
        private void GetCrossLegsPreferCacheThenAtomic(
            string baseCcy,
            string quoteCcy,
            bool forceRefresh,
            out FxSwapPoints legBaseUsd,
            out FxSwapPoints legUsdQuote,
            out DateTime loadedUtc,
            out bool fromCache)
        {
            legBaseUsd = null;
            legUsdQuote = null;
            loadedUtc = default(DateTime);
            fromCache = false;

            if (forceRefresh)
            {
                LoadCrossLegsAtomicByRoute(baseCcy, quoteCcy, out legBaseUsd, out legUsdQuote, out loadedUtc);
                return;
            }

            var route = GetRouteOrDefault(baseCcy, quoteCcy);

            FxSwapPoints pickFromCache(string[] candidates, out DateTime ts)
            {
                ts = default(DateTime);
                if (candidates == null || candidates.Length == 0) return null;
                lock (s_cacheLock)
                {
                    foreach (var c in candidates)
                    {
                        var key = (c ?? "").Trim().ToUpperInvariant();
                        if (key.Length == 0) continue;

                        if (s_fxLegCache.TryGetValue(key, out var leg) &&
                            s_fxLegLoadedUtc.TryGetValue(key, out var tstamp) &&
                            FxSwapPoints.IsValidLeg(leg))
                        {
                            ts = tstamp;
                            return leg;
                        }
                    }
                }
                return null;
            }

            DateTime tsB, tsQ;
            var b = pickFromCache(route.BaseUsdCandidates, out tsB);
            var q = pickFromCache(route.UsdQuoteCandidates, out tsQ);

            // NY policy: om båda finns i cache → använd dem alltid när ReloadOnStale=false (expiry change = cache)
            if (b != null && q != null && !ReloadOnStale)
            {
                legBaseUsd = b;
                legUsdQuote = q;
                loadedUtc = (tsB <= tsQ ? tsB : tsQ);
                fromCache = true;
                System.Diagnostics.Debug.WriteLine("[CrossLegs] cache-hit: återanvänder (stale tillåtet).");
                return;
            }

            // Saknas något → atomisk multi-load
            if (b == null || q == null)
            {
                LoadCrossLegsAtomicByRoute(baseCcy, quoteCcy, out legBaseUsd, out legUsdQuote, out loadedUtc);
                return;
            }

            // (Fallback när ReloadOnStale=true): välj cache endast om båda är färska, annars atomisk.
            var bStale = IsStaleByTtl(tsB, FxLegTtl);
            var qStale = IsStaleByTtl(tsQ, FxLegTtl);
            if (!bStale && !qStale)
            {
                legBaseUsd = b;
                legUsdQuote = q;
                loadedUtc = (tsB <= tsQ ? tsB : tsQ);
                fromCache = true;
                System.Diagnostics.Debug.WriteLine("[CrossLegs] cache-hit: båda FRESH.");
                return;
            }

            LoadCrossLegsAtomicByRoute(baseCcy, quoteCcy, out legBaseUsd, out legUsdQuote, out loadedUtc);
        }


        #endregion



        #region === Config & routes ===

        /// <summary>
        /// (NY) Beskriver vilka Bloomberg-tickers som ska användas för ett korspar:
        /// - BASE→USD (primära + alternativa)
        /// - USD→QUOTE (primära + alternativa)
        /// Poängen: vi väljer rätt riktning från början och slipper invertera.
        /// </summary>
        internal sealed class CrossRoute
        {
            /// <summary>Tickers i den ordning de ska prövas för BASE→USD, t.ex. "EURUSD BGN Curncy" eller "USDEUR BGN Curncy".</summary>
            public string[] BaseUsdCandidates { get; set; }

            /// <summary>Tickers i den ordning de ska prövas för USD→QUOTE, t.ex. "USDSEK BGN Curncy" eller "SEKUSD BGN Curncy".</summary>
            public string[] UsdQuoteCandidates { get; set; }
        }




        /// <summary>
        /// (NY) Global policy: om false (default) ska vi INTE ladda om när cache är stale.
        /// Endast explicit forceRefresh triggar omladdning.
        /// Sätt true om du vill återgå till "reload on stale".
        /// </summary>
        private static readonly bool ReloadOnStale = false;



        /// <summary>
        /// (NY) Routing-tabell för korspar. Lägg in par du bryr dig om – resten faller tillbaka
        /// på en generisk default (BASEUSD / USDQUOTE).
        /// </summary>
        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, CrossRoute> s_crossRoutes =
            new System.Collections.Generic.Dictionary<string, CrossRoute>(System.StringComparer.OrdinalIgnoreCase)
            {
                // Exempel (rekommenderat att börja med dessa):
                ["EURSEK"] = new CrossRoute
                {
                    BaseUsdCandidates = new[] { "EURUSD BGN Curncy" },
                    UsdQuoteCandidates = new[] { "USDSEK BGN Curncy" }
                },
                ["CHFSEK"] = new CrossRoute
                {
                    // Marknaden handlar oftare USDCHF än CHFUSD ⇒ välj det först
                    BaseUsdCandidates = new[] { "USDCHF BGN Curncy" },
                    UsdQuoteCandidates = new[] { "USDSEK BGN Curncy" }
                },
                ["NOKSEK"] = new CrossRoute
                {
                    BaseUsdCandidates = new[] {  "USDNOK BGN Curncy" }, // välj vad som finns i din miljö
                    UsdQuoteCandidates = new[] { "USDSEK BGN Curncy" }
                },
                // Lägg till fler par här vid behov...
            };

        /// <summary>
        /// (NY) Hämtar route för ett korspar (BASE/QUOTE). Om inget explicit finns i tabellen
        /// returneras en default som prövar BASEUSD samt USDQUOTE som första kandidater.
        /// </summary>
        private static CrossRoute GetRouteOrDefault(string baseCcy, string quoteCcy)
        {
            var key = (baseCcy + quoteCcy);
            if (s_crossRoutes.TryGetValue(key, out var route) && route != null)
                return route;

            // Generisk fallback: pröva "BASEUSD" och "USDQUOTE" först, därefter omvänd riktning
            return new CrossRoute
            {
                BaseUsdCandidates = new[] { baseCcy + "USD BGN Curncy", "USD" + baseCcy + " BGN Curncy" },
                UsdQuoteCandidates = new[] { "USD" + quoteCcy + " BGN Curncy", quoteCcy + "USD BGN Curncy" }
            };
        }

        #endregion

        #region === Diagnostics ===

        /// <summary>
        /// (ERSÄTT) Skriver ett diagnostik-snapshot för RF/RD.
        /// - Använder samma benpolicy som Ensure (cache-först; atomisk multi-load vid behov).
        /// - USD-kurvan säkras med "relaxed" policy: ingen automatisk reload på stale om <paramref name="forceRefresh"/> är false.
        /// - För USD-par dumpas ett ben; för korspar dumpas båda ben (BASE→USD och USD→QUOTE).
        /// - Ändrar ingen state; returnerar även den formatterade texten som skrivs till Debug.
        /// </summary>
        /// <param name="baseCcy">Tre bokstäver för base, t.ex. "EUR".</param>
        /// <param name="quoteCcy">Tre bokstäver för quote, t.ex. "SEK".</param>
        /// <param name="spotDate">Spotdatum (din pipeline’s spot).</param>
        /// <param name="settlement">Leveransdatum.</param>
        /// <param name="useAtomicRoute">
        /// true = hämta korsparens ben via cache-först/atomisk helper (rekommenderas);
        /// false = fallback: två separata cache-hämtningar.
        /// </param>
        /// <param name="forceRefresh">true för att tvinga omladdning; annars cache-först.</param>
        /// <returns>Formatterad diagnostiksträng som även skrivs till Debug.</returns>
        public string DumpCrossRfSnapshot(
            string baseCcy,
            string quoteCcy,
            DateTime spotDate,
            DateTime settlement,
            bool useAtomicRoute = true,
            bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(baseCcy)) throw new ArgumentNullException(nameof(baseCcy));
            if (string.IsNullOrWhiteSpace(quoteCcy)) throw new ArgumentNullException(nameof(quoteCcy));

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(1024);

            // 1) USD parrate från SOFR — RELAXED (ingen reload på stale om forceRefresh=false)
            EnsureUsdCurveCachedRelaxed(forceRefresh);
            var usdCurve = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");
            double dfSpot = Clamp01(usdCurve.DiscountFactor(spotDate));
            double dfSet = Clamp01(usdCurve.DiscountFactor(settlement));
            double T_usd = YearFracMm(spotDate, settlement, "USD");
            double r_usd = (1.0 / Math.Max(1e-12, (dfSet / Math.Max(1e-12, dfSpot))) - 1.0) / Math.Max(1e-12, T_usd);

            // 2) Rubrik
            sb.AppendLine("[Cross RF Snapshot]");
            sb.AppendLine("Pair: " + (baseCcy + quoteCcy) +
                          "   Spot=" + spotDate.ToString("yyyy-MM-dd", inv) +
                          "   Settle=" + settlement.ToString("yyyy-MM-dd", inv));
            sb.AppendLine("USD par (Spot→Settle): T=" + T_usd.ToString("F6", inv) +
                          "   r_usd=" + (r_usd * 100.0).ToString("F3", inv) + " %");

            // 3) USD-par?
            bool isUsdPair =
                string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase);

            // Hjälpare
            bool IsBad(double s, double f)
            {
                return !(s > 0.0) || !(f > 0.0) ||
                       double.IsNaN(s) || double.IsNaN(f) ||
                       double.IsInfinity(s) || double.IsInfinity(f);
            }

            if (isUsdPair)
            {
                // --- Ett ben ---
                DateTime load;
                var leg = GetFxLegCached(baseCcy, quoteCcy, forceRefresh, out load);

                double S = leg.SpotMid;
                double F = leg.ForwardAt(settlement, leg.Pair6, S);
                bool bad = IsBad(S, F);

                sb.AppendLine("Leg: " + (string.IsNullOrEmpty(leg.PairTicker) ? (leg.Pair6 + " BGN Curncy") : leg.PairTicker) +
                              "   loadUtc=" + load.ToString("yyyy-MM-dd HH:mm:ss", inv));
                sb.AppendLine("S_native=" + S.ToString("F6", inv) + "   F_native=" + F.ToString("F6", inv) + (bad ? "   [BAD]" : ""));

                double rd = double.NaN, rf = double.NaN;
                if (!bad)
                {
                    bool usdIsBase = leg.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                    bool usdIsQuote = leg.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase);
                    double T_local = YearFracMm(spotDate, settlement, usdIsBase ? quoteCcy : baseCcy);
                    double onePlus = 1.0 + r_usd * Math.Max(T_local, 1e-12);

                    if (usdIsBase) { rd = ((onePlus * F / Math.Max(S, 1e-12)) - 1.0) / Math.Max(T_local, 1e-12); rf = r_usd; }
                    if (usdIsQuote) { rf = ((onePlus * Math.Max(S, 1e-12) / Math.Max(F, 1e-12)) - 1.0) / Math.Max(T_local, 1e-12); rd = r_usd; }
                }

                sb.AppendLine(!double.IsNaN(rd) && !double.IsNaN(rf)
                    ? "Solved: rd=" + (rd * 100.0).ToString("F3", inv) + " %   rf=" + (rf * 100.0).ToString("F3", inv) + " %"
                    : "Solved: n/a (bad leg)");
            }
            else
            {
                // --- Korspar: använd samma policy som Ensure ---
                FxSwapPoints legB = null, legQ = null;
                DateTime loadedUtc;

                if (useAtomicRoute)
                {
                    bool fromCache;
                    GetCrossLegsPreferCacheThenAtomic(baseCcy, quoteCcy, forceRefresh, out legB, out legQ, out loadedUtc, out fromCache);
                }
                else
                {
                    // Fallback: två separata cache-hämtningar (använd helst atomic/cached via helpern ovan)
                    DateTime tsB, tsQ;
                    legB = GetFxLegCached(baseCcy, "USD", forceRefresh, out tsB);
                    legQ = GetFxLegCached("USD", quoteCcy, forceRefresh, out tsQ);
                    loadedUtc = (tsB <= tsQ ? tsB : tsQ);
                }

                var loadB = loadedUtc; var loadQ = loadedUtc;

                bool usdIsQuote_B = legB.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase);
                string reqB = usdIsQuote_B ? (baseCcy + "USD") : ("USD" + baseCcy);
                double S_b = legB.SpotMid;
                double F_b = legB.ForwardAt(settlement, reqB, S_b);
                double T_b = YearFracMm(spotDate, settlement, baseCcy);
                double onePlus_b = 1.0 + r_usd * Math.Max(T_b, 1e-12);

                bool usdIsBase_Q = legQ.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                string reqQ = usdIsBase_Q ? ("USD" + quoteCcy) : (quoteCcy + "USD");
                double S_q = legQ.SpotMid;
                double F_q = legQ.ForwardAt(settlement, reqQ, S_q);
                double T_q = YearFracMm(spotDate, settlement, quoteCcy);
                double onePlus_q = 1.0 + r_usd * Math.Max(T_q, 1e-12);

                bool badB = IsBad(S_b, F_b);
                bool badQ = IsBad(S_q, F_q);

                sb.AppendLine("BASE→USD: " + (string.IsNullOrEmpty(legB.PairTicker) ? (legB.Pair6 + " BGN Curncy") : legB.PairTicker) +
                              "   loadUtc=" + loadB.ToString("yyyy-MM-dd HH:mm:ss", inv));
                sb.AppendLine("S_native=" + S_b.ToString("F6", inv) + "   F_native=" + F_b.ToString("F6", inv) + (badB ? "   [BAD]" : ""));
                sb.AppendLine("USD→QUOTE: " + (string.IsNullOrEmpty(legQ.PairTicker) ? (legQ.Pair6 + " BGN Curncy") : legQ.PairTicker) +
                              "   loadUtc=" + loadQ.ToString("yyyy-MM-dd HH:mm:ss", inv));
                sb.AppendLine("S_native=" + S_q.ToString("F6", inv) + "   F_native=" + F_q.ToString("F6", inv) + (badQ ? "   [BAD]" : ""));

                if (!badB && !badQ)
                {
                    double rf = usdIsQuote_B
                        ? ((onePlus_b * Math.Max(S_b, 1e-12) / Math.Max(F_b, 1e-12)) - 1.0) / Math.Max(T_b, 1e-12)
                        : ((onePlus_b * F_b / Math.Max(S_b, 1e-12)) - 1.0) / Math.Max(T_b, 1e-12);

                    double rd = usdIsBase_Q
                        ? ((onePlus_q * F_q / Math.Max(S_q, 1e-12)) - 1.0) / Math.Max(T_q, 1e-12)
                        : ((onePlus_q * Math.Max(S_q, 1e-12) / Math.Max(F_q, 1e-12)) - 1.0) / Math.Max(T_q, 1e-12);

                    sb.AppendLine("Solved (native): rd=" + (rd * 100.0).ToString("F3", inv) + " %   rf=" + (rf * 100.0).ToString("F3", inv) + " %");
                }
                else
                {
                    sb.AppendLine("Solved (native): n/a (minst ett ben ogiltigt)");
                }
            }

            var text = sb.ToString();
            System.Diagnostics.Debug.WriteLine(text);
            return text;
        }

        #endregion

    }
}
