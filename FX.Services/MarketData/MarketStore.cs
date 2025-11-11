// FX.Services/MarketData/MarketStore.cs
// C# 7.3
using System;
using System.Globalization;
using FX.Core.Domain.MarketData;
using static FX.Core.Domain.MarketData.IMarketStore;

namespace FX.Services.MarketData
{
    /// <summary>
    /// MarketStore – håller ett "current" MarketSnapshot och mergar feed/user enligt deterministiska regler.
    /// - SPOT: som baseline (override per sida, viewmode, monotoni, stale, version).
    /// - RD/RF PER LEG: samma regler som SPOT, med Changed-reasons per leg.
    /// </summary>
    public sealed class MarketStore : IMarketStore
    {
        public event EventHandler<MarketChangedEventArgs> Changed;

        private MarketSnapshot _current;
        public MarketSnapshot Current => _current;

        // === Batchning av Changed ===
        private readonly object _chgGate = new object();
        private System.Threading.Timer _chgTimer;
        private const int ChangedDebounceMs = 30; // 20–50 ms är vanligtvis lagom
        private int _chgRdCount, _chgRfCount, _chgSpotCount, _chgFwdCount, _chgOtherCount;


        public MarketStore()
        {
            // Seed: tomt EURSEK-snapshot så Current aldrig är null.
            var now = DateTime.UtcNow;
            var spot = new MarketField<double>(
                value: new TwoWay<double>(0d, 0d),
                source: MarketSource.Feed,
                viewMode: ViewMode.FollowFeed,
                ov: OverrideMode.None,
                tsUtc: now,
                version: 0,
                stale: true);

            _current = new MarketSnapshot("EURSEK", spot);
        }

        #region Forward Pricing Mode

        // Default-läge = Full (tvåväg i visning). Detta påverkar inte engine – endast UI-policy.
        private ForwardPricingMode _forwardMode = ForwardPricingMode.Full;

        /// <summary>
        /// Hämtar aktuellt forward-läge (MID/FULL/NET) för den aktuella dealen/fliken.
        /// Används av Presenter/View för att rendera RD/RF/Forward konsekvent.
        /// </summary>
        public ForwardPricingMode GetForwardPricingMode()
        {
            return _forwardMode;
        }

        /// <summary>
        /// Sätter forward-läge (MID/FULL/NET). Om värdet ändras fire:as ett debouncat Changed-event
        /// med reason “ForwardMode”. Engine/Runtime läser inte denna flagga; de konsumerar Spot/RD/RF
        /// enligt respektive ViewMode som Presentern redan ställer.
        /// </summary>
        /// <param name="pair6">Valutapar (t.ex. "EURSEK") – används endast för logg/diagnostik.</param>
        /// <param name="mode">Nytt forward-läge.</param>
        /// <param name="nowUtc">Tidsstämpel (UTC) för ändringen.</param>
        public void SetForwardPricingMode(string pair6, ForwardPricingMode mode, DateTime nowUtc)
        {
            if (_forwardMode == mode)
                return;

            _forwardMode = mode;

            // Debounced batch-event; konsumenter kan läsa Current samt GetForwardPricingMode().
            RaiseChanged("ForwardMode");
        }

        #endregion



        #region Spot

        /// <summary>
        /// User → Spot. wasMid=true låser Mid (bid=ask=mid) och sätter Override=Mid; annars Override=Both i TwoWay-läge.
        /// Skapar nytt Spot-snapshot och BEVARAR RD/RF för samma valutapar.
        /// Triggar Changed (batchas) med reason "UserSpot".
        /// </summary>
        public void SetSpotFromUser(string pair6, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var ov = wasMid ? OverrideMode.Mid : OverrideMode.Both; // mid låser båda sidor
            var tw = NormalizeMonotone(value);

            // Spara föregående snapshot för att bevara RD/RF (om samma pair)
            var prev = _current;
            var samePair = prev != null && string.Equals(prev.Pair6, p6, StringComparison.OrdinalIgnoreCase);

            var prevVersion = samePair ? prev.Spot.Version : 0;
            var mf = new MarketField<double>(tw, MarketSource.User, viewMode, ov, nowUtc, prevVersion + 1, stale: false);

            // Bygg nytt snapshot för spot men kopiera över RD/RF om vi är kvar på samma pair
            var next = new MarketSnapshot(p6, mf);

            if (samePair && prev.RdByLeg != null)
                foreach (var kv in prev.RdByLeg)
                    next.RdByLeg[kv.Key] = kv.Value;

            if (samePair && prev.RfByLeg != null)
                foreach (var kv in prev.RfByLeg)
                    next.RfByLeg[kv.Key] = kv.Value;

            _current = next;
            RaiseChanged("UserSpot");
        }

        /// <summary>
        /// Skriver spot från feed och skapar nytt snapshot för Spot.
        /// Bevarar befintliga RD/RF-tabeller när paret är oförändrat, så att en spot-refresh inte tappar kurvor.
        /// Respekterar Override/ViewMode och kan markera stale.
        /// Triggar Changed (batchas) med reason "FeedSpot".
        /// </summary>
        public void SetSpotFromFeed(string pair6, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var tw = NormalizeMonotone(value);

            // Se till att vi har ett snapshot för rätt par (skapar tomt om nytt par)
            EnsureSnapshotPair(p6);

            // Håll en referens till "före"-snapshot så vi kan bevara rd/rf
            var prev = _current;

            // Utgå från nuvarande spot-field (finns alltid efter EnsureSnapshotPair)
            var cur = prev.Spot;
            var eff = cur.Effective;

            switch (cur.Override)
            {
                case OverrideMode.Mid:
                case OverrideMode.Both:
                    // låst – ignorera feed helt
                    break;

                case OverrideMode.Bid:
                    // håll bid, uppdatera ask
                    eff = eff.WithAsk(tw.Ask);
                    cur.Replace(eff, cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.Ask:
                    // håll ask, uppdatera bid
                    eff = eff.WithBid(tw.Bid);
                    cur.Replace(eff, cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.None:
                    // skriv båda
                    cur.Replace(tw, MarketSource.Feed, cur.ViewMode, OverrideMode.None, nowUtc);
                    break;
            }

            if (isStale) cur.MarkStale(true, nowUtc);

            // Bygg NYTT snapshot för Spot – men BEVARA rd/rf från prev
            var next = new MarketSnapshot(p6, cur);

            if (prev?.RdByLeg != null)
                foreach (var kv in prev.RdByLeg)
                    next.RdByLeg[kv.Key] = kv.Value;

            if (prev?.RfByLeg != null)
                foreach (var kv in prev.RfByLeg)
                    next.RfByLeg[kv.Key] = kv.Value;

            _current = next;

            RaiseChanged("FeedSpot");
        }

        /// <summary>
        /// Uppdaterar endast Spot.ViewMode (Mid/TwoWay), behåller Effective/Source/Override.
        /// Skapar nytt Spot-snapshot och BEVARAR RD/RF för samma valutapar (vid parbyte initieras tomt).
        /// Triggar Changed (batchas) med reason "SpotViewMode".
        /// </summary>
        public void SetSpotViewMode(string pair6, ViewMode viewMode, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "").Replace("/", "").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(p6) || p6.Length != 6) return;

            // Om vi redan har snapshot för samma pair
            if (_current != null && string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase))
            {
                var prev = _current;
                var cur = prev.Spot;

                if (cur.ViewMode == viewMode) return;

                // Uppdatera endast view-mode; värdet (Effective) är oförändrat
                cur.Replace(cur.Effective, cur.Source, viewMode, cur.Override, nowUtc);

                // Bygg nytt snapshot men BEVARA RD/RF
                var next = new MarketSnapshot(p6, cur);

                if (prev.RdByLeg != null)
                    foreach (var kv in prev.RdByLeg)
                        next.RdByLeg[kv.Key] = kv.Value;

                if (prev.RfByLeg != null)
                    foreach (var kv in prev.RfByLeg)
                        next.RfByLeg[kv.Key] = kv.Value;

                _current = next;
            }
            else
            {
                // Nytt pair eller inget snapshot ännu → skapa “tom” spot (stale)
                var mf = new MarketField<double>(
                    value: new TwoWay<double>(0d, 0d),
                    source: MarketSource.Feed,
                    viewMode: viewMode,
                    ov: OverrideMode.None,
                    tsUtc: nowUtc,
                    version: 0,
                    stale: true);

                // Vid parbyte: kopiera INTE RD/RF från tidigare par
                _current = new MarketSnapshot(p6, mf);
            }

            RaiseChanged("SpotViewMode");
        }

        /// <summary>
        /// Uppdaterar endast Spot.Override (None/Mid/Bid/Ask/Both), behåller Effective/Source/ViewMode.
        /// Skapar nytt Spot-snapshot och BEVARAR RD/RF för samma valutapar.
        /// Triggar Changed (batchas) med reason "SpotOverride".
        /// </summary>
        public void SetSpotOverride(string pair6, OverrideMode ov, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "").Replace("/", "").ToUpperInvariant();
            if (_current == null || !string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase)) return;

            var cur = _current.Spot;
            if (cur.Override == ov) return; // idempotens

            cur.Replace(cur.Effective, cur.Source, cur.ViewMode, ov, nowUtc);

            var next = new MarketSnapshot(p6, cur);

            // BEVARA RD/RF
            if (_current.RdByLeg != null)
                foreach (var kv in _current.RdByLeg)
                    next.RdByLeg[kv.Key] = kv.Value;

            if (_current.RfByLeg != null)
                foreach (var kv in _current.RfByLeg)
                    next.RfByLeg[kv.Key] = kv.Value;

            _current = next;
            RaiseChanged("SpotOverride");
        }

        #endregion
        #region RD & RF

        /// <summary>
        /// Skriver rd (domestic rate) från feed för angivet ben.
        /// - Kvantiserar bid/ask till 5 d.p. i decimal (≈ 3 d.p. i %) innan normalisering/lagring,
        ///   så att Store:s baseline exakt matchar UI:s precision.
        /// - Idempotent mot Effective-värden (ingen ändring/Changed om oförändrat).
        /// - Respekterar Override-läge. Loggar write om DebugFlags.RatesWrite=true. Triggar Changed (batchas).
        /// </summary>
        public void SetRdFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();

            // --- Kvantisering till UI-precision: 5 d.p. i decimal ---
            const int DEC = 5;
            var vq = new TwoWay<double>(
                bid: Math.Round(value.Bid, DEC, MidpointRounding.AwayFromZero),
                ask: Math.Round(value.Ask, DEC, MidpointRounding.AwayFromZero)
            );

            // Normalisera (säkerställa monotoni) på de kvantiserade värdena
            var tw = NormalizeMonotone(vq);

            EnsureSnapshotPair(p6);

            const double EPS = 1e-10;

            var cur = MarketSnapshot.TryGet(_current.RdByLeg, legId);
            if (cur == null)
            {
                //var fld = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);

                //----
                var vm = MapForwardPricingModeToRateViewMode(_forwardMode); // Mid→Mid, Full/Net→TwoWay
                var fld = new MarketField<double>(
                    value: tw,
                    source: MarketSource.Feed,
                    viewMode: vm,
                    ov: OverrideMode.None,
                    tsUtc: nowUtc,
                    version: 0,
                    stale: isStale
                );
                //----

                _current.RdByLeg[legId] = fld;

                RaiseChanged("FeedRd:" + legId);
                return;
            }

            var eff = cur.Effective; // TwoWay<double>
            bool same;
            switch (cur.Override)
            {
                case OverrideMode.Bid:
                    same = Math.Abs(eff.Ask - tw.Ask) <= EPS;
                    if (same) return;
                    cur.Replace(eff.WithAsk(tw.Ask), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.Ask:
                    same = Math.Abs(eff.Bid - tw.Bid) <= EPS;
                    if (same) return;
                    cur.Replace(eff.WithBid(tw.Bid), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.Mid:
                case OverrideMode.Both:
                    // Låst – ingen feedpåverkan
                    return;

                case OverrideMode.None:
                default:
                    same = Math.Abs(eff.Bid - tw.Bid) <= EPS && Math.Abs(eff.Ask - tw.Ask) <= EPS;
                    if (same) return;
                    cur.Replace(tw, MarketSource.Feed, cur.ViewMode, OverrideMode.None, nowUtc);
                    break;
            }

            cur.MarkStale(isStale, nowUtc);

            RaiseChanged("FeedRd:" + legId);
        }


        /// <summary>
        /// Skriver rf (foreign rate) från feed för angivet ben.
        /// - Kvantiserar bid/ask till 5 d.p. i decimal (≈ 3 d.p. i %) innan normalisering/lagring,
        ///   så att Store:s baseline exakt matchar UI:s precision.
        /// - Idempotent mot Effective-värden (ingen ändring/Changed om oförändrat).
        /// - Respekterar Override-läge. Loggar write om DebugFlags.RatesWrite=true. Triggar Changed (batchas).
        /// </summary>
        public void SetRfFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();

            // --- Kvantisering till UI-precision: 5 d.p. i decimal ---   ////////////////////////////////   ska vi göra detta?
            const int DEC = 5;
            var vq = new TwoWay<double>(
                bid: Math.Round(value.Bid, DEC, MidpointRounding.AwayFromZero),
                ask: Math.Round(value.Ask, DEC, MidpointRounding.AwayFromZero)
            );

            // Normalisera (säkerställa monotoni) på de kvantiserade värdena
            var tw = NormalizeMonotone(vq);

            EnsureSnapshotPair(p6);

            const double EPS = 1e-10;

            var cur = MarketSnapshot.TryGet(_current.RfByLeg, legId);
            if (cur == null)
            {
                //var fld = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);


                //----
                var vm = MapForwardPricingModeToRateViewMode(_forwardMode); // Mid→Mid, Full/Net→TwoWay
                var fld = new MarketField<double>(
                    value: tw,
                    source: MarketSource.Feed,
                    viewMode: vm,
                    ov: OverrideMode.None,
                    tsUtc: nowUtc,
                    version: 0,
                    stale: isStale
                );
                //----


                _current.RfByLeg[legId] = fld;

                RaiseChanged("FeedRf:" + legId);
                return;
            }

            var eff = cur.Effective;
            bool same;
            switch (cur.Override)
            {
                case OverrideMode.Bid:
                    same = Math.Abs(eff.Ask - tw.Ask) <= EPS;
                    if (same) return;
                    cur.Replace(eff.WithAsk(tw.Ask), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.Ask:
                    same = Math.Abs(eff.Bid - tw.Bid) <= EPS;
                    if (same) return;
                    cur.Replace(eff.WithBid(tw.Bid), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                    break;

                case OverrideMode.Mid:
                case OverrideMode.Both:
                    // Låst – ingen feedpåverkan
                    return;

                case OverrideMode.None:
                default:
                    same = Math.Abs(eff.Bid - tw.Bid) <= EPS && Math.Abs(eff.Ask - tw.Ask) <= EPS;
                    if (same) return;
                    cur.Replace(tw, MarketSource.Feed, cur.ViewMode, OverrideMode.None, nowUtc);
                    break;
            }

            cur.MarkStale(isStale, nowUtc);

            RaiseChanged("FeedRf:" + legId);
        }

        /// <summary>
        /// User → rd. wasMid=true ⇒ lås Mid (bid=ask=mid). wasMid=false ⇒ lås båda sidor (Override=Both).
        /// ViewMode påverkar visning. Skriver Source=User och triggar Changed("UserRd:<legId>").
        /// NYTT: Kvantisera alltid till UI-precision (5 d.p. i decimal) innan lagring så Store == UI.
        /// </summary>
        public void SetRdFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            EnsureSnapshotPair(p6);

            const int DEC = 5;

            // Kvantisera först till UI-precision
            var vq = new TwoWay<double>(
                Math.Round(value.Bid, DEC, MidpointRounding.AwayFromZero),
                Math.Round(value.Ask, DEC, MidpointRounding.AwayFromZero)
            );

            TwoWay<double> val;
            var ov = wasMid ? OverrideMode.Mid : OverrideMode.Both;

            if (wasMid)
            {
                // Lås mid (kvantiserad)
                var midQ = Math.Round(0.5 * (vq.Bid + vq.Ask), DEC, MidpointRounding.AwayFromZero);
                val = new TwoWay<double>(midQ, midQ);
            }
            else
            {
                // Two-way: säkerställ monotoni efter kvantisering
                val = NormalizeMonotone(vq);
            }

            _current.RdByLeg[legId] = new MarketField<double>(
                value: val,
                source: MarketSource.User,
                viewMode: viewMode,
                ov: ov,
                tsUtc: nowUtc,
                version: 0,
                stale: false
            );

            RaiseChanged("UserRd:" + legId);
        }


        /// <summary>
        /// User → rf. Samma regler som rd.
        /// NYTT: Kvantisera alltid till UI-precision (5 d.p. i decimal) innan lagring så Store == UI.
        /// </summary>
        public void SetRfFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            EnsureSnapshotPair(p6);

            const int DEC = 5;

            // Kvantisera först till UI-precision
            var vq = new TwoWay<double>(
                Math.Round(value.Bid, DEC, MidpointRounding.AwayFromZero),
                Math.Round(value.Ask, DEC, MidpointRounding.AwayFromZero)
            );

            TwoWay<double> val;
            var ov = wasMid ? OverrideMode.Mid : OverrideMode.Both;

            if (wasMid)
            {
                // Lås mid (kvantiserad)
                var midQ = Math.Round(0.5 * (vq.Bid + vq.Ask), DEC, MidpointRounding.AwayFromZero);
                val = new TwoWay<double>(midQ, midQ);
            }
            else
            {
                // Two-way: säkerställ monotoni efter kvantisering
                val = NormalizeMonotone(vq);
            }

            _current.RfByLeg[legId] = new MarketField<double>(
                value: val,
                source: MarketSource.User,
                viewMode: viewMode,
                ov: ov,
                tsUtc: nowUtc,
                version: 0,
                stale: false
            );

            RaiseChanged("UserRf:" + legId);
        }



        public void SetRdViewMode(string pair6, string legId, ViewMode viewMode, DateTime nowUtc)
        {
            var cur = MarketSnapshot.TryGet(_current?.RdByLeg, legId);
            if (cur == null) return;

            cur.Replace(cur.Effective, cur.Source, viewMode, cur.Override, nowUtc);
            RaiseChanged("RdViewMode:" + legId);
        }

        public void SetRdOverride(string pair6, string legId, OverrideMode ov, DateTime nowUtc)
        {
            var cur = MarketSnapshot.TryGet(_current?.RdByLeg, legId);
            if (cur == null) return;

            cur.Replace(cur.Effective, cur.Source, cur.ViewMode, ov, nowUtc);
            RaiseChanged("RdOverride:" + legId);
        }

        public void SetRfViewMode(string pair6, string legId, ViewMode viewMode, DateTime nowUtc)
        {
            var cur = MarketSnapshot.TryGet(_current?.RfByLeg, legId);
            if (cur == null) return;

            cur.Replace(cur.Effective, cur.Source, viewMode, cur.Override, nowUtc);
            RaiseChanged("RfViewMode:" + legId);
        }

        public void SetRfOverride(string pair6, string legId, OverrideMode ov, DateTime nowUtc)
        {
            var cur = MarketSnapshot.TryGet(_current?.RfByLeg, legId);
            if (cur == null) return;

            cur.Replace(cur.Effective, cur.Source, cur.ViewMode, ov, nowUtc);
            RaiseChanged("RfOverride:" + legId);
        }

        /// <summary>
        /// Ogiltigförklarar RD/RF för ett specifikt ben så att nästa prisning
        /// måste re-derivera kurvorna från cache (ingen fresh-hämtning här).
        /// Tar bort befintliga fält i snapshotets RdByLeg/RfByLeg och triggar Changed (batchas).
        /// Skriver debug (om <c>DebugFlags.RatesWrite</c> är på) om vad som togs bort.
        /// </summary>
        public void InvalidateRatesForLeg(string pair6, string legId, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();

            if (string.IsNullOrEmpty(legId))
            {
                System.Diagnostics.Debug.WriteLine($"[Store.InvalidateRates][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] pair={p6} leg=(null) SKIP – legId saknas");
                return;
            }

            // Säkerställ att vi har ett snapshot för paret (skapar tomt vid behov)
            EnsureSnapshotPair(p6);

            bool hadRd = _current.RdByLeg != null && _current.RdByLeg.ContainsKey(legId);
            bool hadRf = _current.RfByLeg != null && _current.RfByLeg.ContainsKey(legId);

            //if (DebugFlags.RatesWrite)
            //{
            //    System.Diagnostics.Debug.WriteLine(
            //        $"[Store.InvalidateRates][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] pair={p6} leg={legId} before: hadRd={hadRd} hadRf={hadRf}");
            //}

            if (hadRd)
            {
                _current.RdByLeg.Remove(legId);
                RaiseChanged("InvalidateRd:" + legId);
            }

            if (hadRf)
            {
                _current.RfByLeg.Remove(legId);
                RaiseChanged("InvalidateRf:" + legId);
            }

            //if (DebugFlags.RatesWrite)
            //{
            //    System.Diagnostics.Debug.WriteLine(
            //        $"[Store.InvalidateRates][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] pair={p6} leg={legId} after: removedRd={hadRd} removedRf={hadRf}");
            //    if (!hadRd && !hadRf)
            //    {
            //        System.Diagnostics.Debug.WriteLine(
            //            $"[Store.InvalidateRates][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] pair={p6} leg={legId} nothing to invalidate");
            //    }
            //}
        }



        #endregion

        #region Helpers

        private static TwoWay<double> NormalizeMonotone(TwoWay<double> tw)
        {
            var bid = tw.Bid;
            var ask = tw.Ask;
            if (ask < bid) { var t = bid; bid = ask; ask = t; }
            return new TwoWay<double>(bid, ask);
        }

        private void EnsureSnapshotPair(string p6)
        {
            if (_current != null && string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase))
                return;

            var now = DateTime.UtcNow;
            var spot = new MarketField<double>(
                value: new TwoWay<double>(0d, 0d),
                source: MarketSource.Feed,
                viewMode: ViewMode.FollowFeed,
                ov: OverrideMode.None,
                tsUtc: now,
                version: 0,
                stale: true);
            _current = new MarketSnapshot(p6, spot);
        }

        private static bool TryParseLegReason(string reason, string prefix, out string legId)
        {
            legId = null;
            if (reason == null) return false;
            if (!reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            legId = reason.Substring(prefix.Length);
            return !string.IsNullOrWhiteSpace(legId);
        }

        private static string FmtTW(TwoWay<double> tw) =>
            string.Format(CultureInfo.InvariantCulture, "{0:F6}/{1:F6}", tw.Bid, tw.Ask);


        /// <summary>
        /// Samlar inkommande förändringsorsaker (reason) och (re)startar en kort debounce-timer.
        /// När timern löper ut skickas ett enda Changed-event med aggregerad reason (Batch:Rd=…;Rf=…;Spot=…;Other=…).
        /// Minskar event-storm och ger lugnare UI/prisflöde.
        /// </summary>
        private void RaiseChanged(string reason)
        {
            lock (_chgGate)
            {
                // Klassificera reason och öka rätt räknare
                if (!string.IsNullOrEmpty(reason))
                {
                    if (reason.StartsWith("FeedRd:", StringComparison.OrdinalIgnoreCase) ||
                        reason.StartsWith("UserRd:", StringComparison.OrdinalIgnoreCase))
                    {
                        _chgRdCount++;
                    }
                    else if (reason.StartsWith("FeedRf:", StringComparison.OrdinalIgnoreCase) ||
                             reason.StartsWith("UserRf:", StringComparison.OrdinalIgnoreCase))
                    {
                        _chgRfCount++;
                    }
                    else if (string.Equals(reason, "SpotViewMode", StringComparison.OrdinalIgnoreCase) ||
                             reason.StartsWith("FeedSpot", StringComparison.OrdinalIgnoreCase) ||
                             reason.StartsWith("UserSpot", StringComparison.OrdinalIgnoreCase))
                    {
                        _chgSpotCount++;
                    }
                    else if (string.Equals(reason, "ForwardMode", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ny särskild kategori för forward-läget (MID/FULL/NET)
                        _chgFwdCount++;
                    }
                    else
                    {
                        _chgOtherCount++;
                    }
                }
                else
                {
                    _chgOtherCount++;
                }

                // (Re)starta debounce-timer
                if (_chgTimer == null)
                {
                    _chgTimer = new System.Threading.Timer(ChangedTimerCallback, null, ChangedDebounceMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    _chgTimer.Change(ChangedDebounceMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// Timer-callback för batchevent: nollställer räknare, bygger aggregerad reason
        /// och fire:ar Changed med senaste snapshot. Loggar batch om DebugFlags.StoreBatch=true.
        /// </summary>
        private void ChangedTimerCallback(object state)
        {
            int rd, rf, spot, fwd, other;
            MarketSnapshot snap;

            lock (_chgGate)
            {
                rd = _chgRdCount; _chgRdCount = 0;
                rf = _chgRfCount; _chgRfCount = 0;
                spot = _chgSpotCount; _chgSpotCount = 0;
                fwd = _chgFwdCount; _chgFwdCount = 0;
                other = _chgOtherCount; _chgOtherCount = 0;

                snap = _current;
            }

            var aggReason = $"Batch:Rd={rd};Rf={rf};Spot={spot};Forward={fwd};Other={other}";

            //if (DebugFlags.StoreBatch)
            //{
            //    System.Diagnostics.Debug.WriteLine(
            //        $"[MarketStore.Changed(Batch)][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] {aggReason} pair={snap?.Pair6}"
            //    );
            //}

            var h = Changed;
            if (h != null)
                h(this, new MarketChangedEventArgs(snap, aggReason));
        }

        /// <summary>
        /// Mappar ForwardPricingMode till start-ViewMode för RD/RF-fält när de skapas från feed.
        /// Mid → ViewMode.Mid (singel-vy); Full/Net → ViewMode.TwoWay (bid/ask i underliggande).
        /// </summary>
        private static ViewMode MapForwardPricingModeToRateViewMode(ForwardPricingMode fpm)
        {
            switch (fpm)
            {
                case ForwardPricingMode.Mid:
                    return ViewMode.Mid;
                case ForwardPricingMode.Full:
                case ForwardPricingMode.Net:
                default:
                    return ViewMode.TwoWay;
            }
        }

        #endregion

    }
}
