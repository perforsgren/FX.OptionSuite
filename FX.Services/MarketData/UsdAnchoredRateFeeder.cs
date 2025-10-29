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



        #region === Pricing ===

        /// <summary>
        /// ERSÄTT: Härleder och skriver RD/RF som TwoWay (bid/ask) till MarketStore.
        /// - Använder USD-kurvans parrate (SOFR) som ankare på MID mellan Spot→Settlement.
        /// - För USD-par (USD som base eller quote): ett ben räcker.
        /// - För korspar: RF från BASE↔USD-ben, RD från USD↔QUOTE-ben.
        /// - F_bid/F_mid/F_ask hämtas via FxSwapPoints.ForwardAtAllSides(...) (points→outright).
        /// - S (spot) tas från respektive bens egen SpotMid (ingen common spot i v1).
        /// - TTL-stale markeras men triggar ingen implicit reload här.
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

            // 1) USD parrate (mid) Spot→Settlement, no-reload-on-stale
            EnsureUsdCurveCachedRelaxed(forceRefresh);
            var usdCurve = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");

            double dfSpot = Clamp01(usdCurve.DiscountFactor(spotDate));
            double dfSet = Clamp01(usdCurve.DiscountFactor(settlement));
            double T_usd = YearFracMm(spotDate, settlement, "USD");
            double r_usd = (1.0 / Math.Max(1e-12, (dfSet / Math.Max(1e-12, dfSpot))) - 1.0) / Math.Max(1e-12, T_usd);

            bool usdStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);

            // Lokala hjälpare med KORREKT signatur (5 inparametrar: S, Fb, Fm, Fa, T)
            Func<double, double, double, double, double, (double bid, double mid, double ask)> SolveRfFromBaseUsd =
                (S, Fb, Fm, Fa, T) =>
                {
                    double onePlus = 1.0 + r_usd * Math.Max(T, 1e-12);
                    // BASEUSD (USD som quote) -> rf = ((1+r_usd*T)*S/F - 1)/T
                    double rf_bid = ((onePlus * Math.Max(S, 1e-12) / Math.Max(Fb, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    double rf_mid = ((onePlus * Math.Max(S, 1e-12) / Math.Max(Fm, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    double rf_ask = ((onePlus * Math.Max(S, 1e-12) / Math.Max(Fa, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    return (ClampRate(rf_bid), ClampRate(rf_mid), ClampRate(rf_ask));
                };

            Func<double, double, double, double, double, (double bid, double mid, double ask)> SolveRdFromUsdQuote =
                (S, Fb, Fm, Fa, T) =>
                {
                    double onePlus = 1.0 + r_usd * Math.Max(T, 1e-12);
                    // USDQUOTE (USD som base) -> rd = ((1+r_usd*T)*F/S - 1)/T
                    double rd_bid = ((onePlus * Math.Max(Fb, 1e-12) / Math.Max(S, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    double rd_mid = ((onePlus * Math.Max(Fm, 1e-12) / Math.Max(S, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    double rd_ask = ((onePlus * Math.Max(Fa, 1e-12) / Math.Max(S, 1e-12)) - 1.0) / Math.Max(T, 1e-12);
                    return (ClampRate(rd_bid), ClampRate(rd_mid), ClampRate(rd_ask));
                };

            // 2) USD-par
            if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                DateTime loadUtc;
                FxSwapPoints leg;

                if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
                    leg = GetFxLegCached("USD" + quoteCcy, quoteCcy + "USD", forceRefresh, out loadUtc);
                else
                    leg = GetFxLegCached(baseCcy + "USD", "USD" + baseCcy, forceRefresh, out loadUtc);

                bool legStale = IsStaleByTtl(loadUtc, FxLegTtl);

                // F_bid/mid/ask från points→outright
                var (Fb, Fm, Fa) = leg.ForwardAtAllSides(settlement, leg.Pair6, null);
                double S_leg = leg.SpotMid;

                if (Fb <= 0 || Fm <= 0 || Fa <= 0 || S_leg <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Guardrail] USD leg unusable (spot/fwd). Skip write.");
                    return;
                }

                // Årsfaktor i den icke-USD-valutan
                string other = string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ? quoteCcy : baseCcy;
                double T_other = YearFracMm(spotDate, settlement, other);

                double rd_bid, rd_mid, rd_ask, rf_bid, rf_mid, rf_ask;

                if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    // USD/QUOTE → RD från USDQUOTE-ben, RF = r_usd(mid)
                    var rd = SolveRdFromUsdQuote(S_leg, Fb, Fm, Fa, T_other);
                    rd_bid = rd.bid; rd_mid = rd.mid; rd_ask = rd.ask;
                    rf_bid = rf_mid = rf_ask = ClampRate(r_usd);
                }
                else
                {
                    // BASE/USD → RF från BASEUSD-ben, RD = r_usd(mid)
                    var rf = SolveRfFromBaseUsd(S_leg, Fb, Fm, Fa, T_other);
                    rf_bid = rf.bid; rf_mid = rf.mid; rf_ask = rf.ask;
                    rd_bid = rd_mid = rd_ask = ClampRate(r_usd);
                }

                // Monotoni: ask ≥ bid
                if (rd_bid > rd_ask) { var t = rd_bid; rd_bid = rd_ask; rd_ask = t; }
                if (rf_bid > rf_ask) { var t = rf_bid; rf_bid = rf_ask; rf_ask = t; }

                var now = DateTime.UtcNow;
                bool stale = usdStale || legStale;

                _store.SetRdFromFeed(pair6, legId, new TwoWay<double>(rd_bid, rd_ask), now, stale);
                _store.SetRfFromFeed(pair6, legId, new TwoWay<double>(rf_bid, rf_ask), now, stale);

                System.Diagnostics.Debug.WriteLine(
                    $"[RD/RF USD] pair={pair6} leg={leg.Pair6} S={S_leg:F6} F(b/m/a)=({Fb:F6}/{Fm:F6}/{Fa:F6}) T={T_other:F6}  " +
                    $"rd(b/m/a)=({rd_bid:P4}/{rd_mid:P4}/{rd_ask:P4})  rf(b/m/a)=({rf_bid:P4}/{rf_mid:P4}/{rf_ask:P4})  stale={stale}");
                return;
            }

            // 3) Korspar: BASE↔USD + USD↔QUOTE
            {
                DateTime loadedBoth;
                bool fromCache;
                FxSwapPoints legBaseUsd, legUsdQuote;

                GetCrossLegsPreferCacheThenAtomic(baseCcy, quoteCcy, forceRefresh,
                    out legBaseUsd, out legUsdQuote, out loadedBoth, out fromCache);

                bool legsStale = IsStaleByTtl(loadedBoth, FxLegTtl);

                // BASEUSD-ben (rf)
                bool baseUsd_isBaseUsd = legBaseUsd.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase) == false
                                      && legBaseUsd.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase); // "BASEUSD"
                double S_b = legBaseUsd.SpotMid;
                var (Fb_b, Fm_b, Fa_b) = legBaseUsd.ForwardAtAllSides(settlement,
                    baseUsd_isBaseUsd ? (baseCcy + "USD") : ("USD" + baseCcy), null);

                // USDQUOTE-ben (rd)
                bool usdQuote_isUsdQuote = legUsdQuote.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase); // "USDQUOTE"
                double S_q = legUsdQuote.SpotMid;
                var (Fb_q, Fm_q, Fa_q) = legUsdQuote.ForwardAtAllSides(settlement,
                    usdQuote_isUsdQuote ? ("USD" + quoteCcy) : (quoteCcy + "USD"), null);

                if (S_b <= 0 || S_q <= 0 || Fb_b <= 0 || Fm_b <= 0 || Fa_b <= 0 || Fb_q <= 0 || Fm_q <= 0 || Fa_q <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Guardrail] Cross legs unusable (spot/fwd). Skip write.");
                    return;
                }

                double Tb = YearFracMm(spotDate, settlement, baseCcy);
                double Tq = YearFracMm(spotDate, settlement, quoteCcy);

                // rf från BASEUSD (USD som quote)
                var rf = SolveRfFromBaseUsd(S_b, Fb_b, Fm_b, Fa_b, Tb);

                // rd från USDQUOTE (USD som base)
                var rd = SolveRdFromUsdQuote(S_q, Fb_q, Fm_q, Fa_q, Tq);

                double rd_bid = rd.bid, rd_mid = rd.mid, rd_ask = rd.ask;
                double rf_bid = rf.bid, rf_mid = rf.mid, rf_ask = rf.ask;

                if (rd_bid > rd_ask) { var t = rd_bid; rd_bid = rd_ask; rd_ask = t; }
                if (rf_bid > rf_ask) { var t = rf_bid; rf_bid = rf_ask; rf_ask = t; }

                var now = DateTime.UtcNow;
                bool stale = usdStale || legsStale;

                _store.SetRdFromFeed(pair6, legId, new TwoWay<double>(rd_bid, rd_ask), now, stale);
                _store.SetRfFromFeed(pair6, legId, new TwoWay<double>(rf_bid, rf_ask), now, stale);

                System.Diagnostics.Debug.WriteLine(
                    $"[RD/RF XCCY] pair={pair6} baseLeg={legBaseUsd.Pair6} quoteLeg={legUsdQuote.Pair6}  " +
                    $"Sb={S_b:F6} F_b(b/m/a)=({Fb_b:F6}/{Fm_b:F6}/{Fa_b:F6}) Tb={Tb:F6}  " +
                    $"Sq={S_q:F6} F_q(b/m/a)=({Fb_q:F6}/{Fm_q:F6}/{Fa_q:F6}) Tq={Tq:F6}  " +
                    $"rd(b/m/a)=({rd_bid:P4}/{rd_mid:P4}/{rd_ask:P4})  rf(b/m/a)=({rf_bid:P4}/{rf_mid:P4}/{rf_ask:P4})  stale={stale}");
            }
        }



        /// <summary>
        /// Säkerställ RD/RF i store för givet par/leg och datumintervall.
        /// Stabil och minimal variant för Steg 1 (POINTS):
        /// - USD-kurva via relaxed cache (ingen auto-reload på stale om forceRefresh=false).
        /// - ForwardAt anropas alltid med explicit spotOverride = leg.SpotMid och DF-ratio avstängt,
        ///   vilket matchar OLD-beteendet och undviker edge-cases.
        /// - I övrigt oförändrad logik; ForwardAt kan nu använda punkter (S+P) om tillgängligt.
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

            // USD parrate (SOFR) — relaxed cache
            EnsureUsdCurveCachedRelaxed(forceRefresh);
            var usdCurve = s_cachedUsdCurve ?? throw new InvalidOperationException("USD curve cache is null.");

            var dfSpot = Clamp01(usdCurve.DiscountFactor(spotDate));
            var dfSet = Clamp01(usdCurve.DiscountFactor(settlement));
            var T_usd = YearFracMm(spotDate, settlement, "USD");
            var r_usd = (1.0 / Math.Max(1e-12, (dfSet / Math.Max(1e-12, dfSpot))) - 1.0) / Math.Max(1e-12, T_usd);

            bool usdStale = IsStaleByTtl(s_usdLoadedUtc, UsdCurveTtl);

            double rd_mid = 0.0, rf_mid = 0.0;
            bool baseLegStale = false, quoteLegStale = false;

            // USD-par
            if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                DateTime loadUtc;
                FxSwapPoints leg;

                if (string.Equals(baseCcy, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    // USD/QUOTE
                    leg = GetFxLegCached("USD" + quoteCcy, quoteCcy + "USD", forceRefresh, out loadUtc);
                    quoteLegStale = IsStaleByTtl(loadUtc, FxLegTtl);
                }
                else
                {
                    // BASE/USD
                    leg = GetFxLegCached(baseCcy + "USD", "USD" + baseCcy, forceRefresh, out loadUtc);
                    baseLegStale = IsStaleByTtl(loadUtc, FxLegTtl);
                }

                double S = leg.SpotMid;
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

            // Korspar: cache-först, atomisk vid behov (båda USD-ben)
            {
                DateTime loadBothUtc;
                bool fromCache;
                FxSwapPoints legBaseUsd, legUsdQuote;

                GetCrossLegsPreferCacheThenAtomic(
                    baseCcy, quoteCcy, forceRefresh,
                    out legBaseUsd, out legUsdQuote,
                    out loadBothUtc, out fromCache);

                bool legsStale = IsStaleByTtl(loadBothUtc, FxLegTtl);

                // BASE↔USD → rf
                {
                    bool usdIsQuote = legBaseUsd.Pair6.EndsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsQuote ? (baseCcy + "USD") : ("USD" + baseCcy);

                    double S_b = legBaseUsd.SpotMid;
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

                // USD↔QUOTE → rd
                {
                    bool usdIsBase = legUsdQuote.Pair6.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
                    string required = usdIsBase ? ("USD" + quoteCcy) : (quoteCcy + "USD");

                    double S_q = legUsdQuote.SpotMid;
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

            //System.Diagnostics.Debug.WriteLine(
            //    $"[AtomicCross][OK] base={baseCcy} quote={quoteCcy}  legs=({legBaseUsd.PairTicker}) & ({legUsdQuote.PairTicker})  loadedUtc={loadedUtc:yyyy-MM-dd HH:mm:ss}");
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
