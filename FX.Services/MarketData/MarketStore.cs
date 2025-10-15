// FX.Services/MarketData/MarketStore.cs
// C# 7.3
using System;
using System.Globalization;
using FX.Core.Domain.MarketData;

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

        // =========================
        // SPOT
        // =========================

        /// <summary>
        /// User → Spot. wasMid=true ⇒ lås Mid och lagra bid=ask=mid. ViewMode påverkar visning.
        /// Bevarar RD/RF om paret är detsamma.
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
        /// Feed → Spot (two-way). Respekterar per-sida override.
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
        /// Uppdaterar enbart view-mode för Spot (Mid/TwoWay). Värdet lämnas orört.
        /// Bevarar RD/RF om paret är detsamma.
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
        /// Uppdaterar enbart override (None/Mid/Bid/Ask/Both) för Spot.
        /// Bevarar RD/RF om paret är detsamma.
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





        // =========================
        // RD/RF – FEED
        // =========================

        /// <summary>Feed → rd för ett leg. Respekterar per-sida override.</summary>
        public void SetRdFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var tw = NormalizeMonotone(value);

            EnsureSnapshotPair(p6);

            const double EPS = 1e-10;

            var cur = MarketSnapshot.TryGet(_current.RdByLeg, legId);
            if (cur == null)
            {
                _current.RdByLeg[legId] = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);
                RaiseChanged("FeedRd:" + legId);
                return;
            }

            // === Idempotens: om effektivt värde redan är (nästan) samma → no-op ===
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


        /// <summary>Feed → rf för ett leg. Samma regler som rd.</summary>
        public void SetRfFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var tw = NormalizeMonotone(value);

            EnsureSnapshotPair(p6);

            const double EPS = 1e-10;

            var cur = MarketSnapshot.TryGet(_current.RfByLeg, legId);
            if (cur == null)
            {
                _current.RfByLeg[legId] = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);
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


        // =========================
        // RD/RF – USER
        // =========================

        /// <summary>User → rd. wasMid=true ⇒ lås Mid (bid=ask=mid). ViewMode påverkar visning.</summary>
        public void SetRdFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            EnsureSnapshotPair(p6);

            var tw = NormalizeMonotone(value);
            var mid = wasMid ? 0.5 * (tw.Bid + tw.Ask) : (double?)null;
            var val = wasMid ? new TwoWay<double>(mid.Value, mid.Value) : tw;

            _current.RdByLeg[legId] = new MarketField<double>(val, MarketSource.User, viewMode, wasMid ? OverrideMode.Mid : OverrideMode.None, nowUtc, 0, false);
            RaiseChanged("UserRd:" + legId);
        }

        /// <summary>User → rf. Samma regler som rd.</summary>
        public void SetRfFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            EnsureSnapshotPair(p6);

            var tw = NormalizeMonotone(value);
            var mid = wasMid ? 0.5 * (tw.Bid + tw.Ask) : (double?)null;
            var val = wasMid ? new TwoWay<double>(mid.Value, mid.Value) : tw;

            _current.RfByLeg[legId] = new MarketField<double>(val, MarketSource.User, viewMode, wasMid ? OverrideMode.Mid : OverrideMode.None, nowUtc, 0, false);
            RaiseChanged("UserRf:" + legId);
        }

        // =========================
        // RD/RF – VIEWMODE & OVERRIDE
        // =========================

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

        // =========================
        // Hjälpare
        // =========================

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

        private void RaiseChanged(string reason)
        {
            var snap = _current;

            //if (DebugFlags.StoreChanged)
            {
                // --- RD ---
                if (TryParseLegReason(reason, "FeedRd:", out var rdLeg) || TryParseLegReason(reason, "UserRd:", out rdLeg))
                {
                    if (snap != null && snap.RdByLeg != null && snap.RdByLeg.TryGetValue(rdLeg, out var fld) && fld != null)
                    {
                        var eff = fld.Effective; // TwoWay<double>
                        System.Diagnostics.Debug.WriteLine(
                            $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] " +
                            $"reason={reason} pair={snap.Pair6} RD({rdLeg}) eff={FmtTW(eff)} src={fld.Source} vm={fld.ViewMode} ov={fld.Override} stale={fld.IsStale}"
                        );
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] reason={reason} pair={snap?.Pair6} RD({rdLeg})=null"
                        );
                    }
                }
                // --- RF ---
                else if (TryParseLegReason(reason, "FeedRf:", out var rfLeg) || TryParseLegReason(reason, "UserRf:", out rfLeg))
                {
                    if (snap != null && snap.RfByLeg != null && snap.RfByLeg.TryGetValue(rfLeg, out var fld) && fld != null)
                    {
                        var eff = fld.Effective;
                        System.Diagnostics.Debug.WriteLine(
                            $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] " +
                            $"reason={reason} pair={snap.Pair6} RF({rfLeg}) eff={FmtTW(eff)} src={fld.Source} vm={fld.ViewMode} ov={fld.Override} stale={fld.IsStale}"
                        );
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] reason={reason} pair={snap?.Pair6} RF({rfLeg})=null"
                        );
                    }
                }
                // --- SPOT (Feed/User/ViewMode) ---
                else if (string.Equals(reason, "SpotViewMode", StringComparison.OrdinalIgnoreCase)
                      || reason.StartsWith("FeedSpot", StringComparison.OrdinalIgnoreCase)
                      || reason.StartsWith("UserSpot", StringComparison.OrdinalIgnoreCase))
                {
                    var spot = snap?.Spot;
                    var txt = (spot == null) ? "null" : FmtTW(spot.Effective);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] reason={reason} pair={snap?.Pair6} spotEff={txt}"
                    );
                }
                // --- Övrigt ---
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MarketStore.Changed][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] reason={reason} pair={snap?.Pair6}"
                    );
                }
            }

            // Fire event som tidigare
            var h = Changed;
            if (h != null) h(this, new MarketChangedEventArgs(snap, reason));
        }








    }
}
