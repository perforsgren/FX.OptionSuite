// FX.Services/MarketData/MarketStore.cs
// C# 7.3
using System;
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
        /// </summary>
        public void SetSpotFromUser(string pair6, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var ov = wasMid ? OverrideMode.Mid : OverrideMode.Both; // mid låser båda sidor
            var tw = NormalizeMonotone(value);

            var prevVersion = _current != null &&
                              string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase)
                              ? _current.Spot.Version : 0;

            var mf = new MarketField<double>(tw, MarketSource.User, viewMode, ov, nowUtc, prevVersion + 1, stale: false);
            _current = new MarketSnapshot(p6, mf);
            RaiseChanged("UserSpot");
        }

        /// <summary>
        /// Feed → Spot (two-way). Respekterar per-sida override.
        /// </summary>
        public void SetSpotFromFeed(string pair6, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var tw = NormalizeMonotone(value);

            if (_current != null && string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase))
            {
                var cur = _current.Spot;
                var eff = cur.Effective;

                switch (cur.Override)
                {
                    case OverrideMode.Mid:
                    case OverrideMode.Both:
                        // låst – ignorera feed
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
                _current = new MarketSnapshot(p6, cur);
            }
            else
            {
                var mf = new MarketField<double>(tw, MarketSource.Feed, ViewMode.FollowFeed, OverrideMode.None, nowUtc, 1, isStale);
                _current = new MarketSnapshot(p6, mf);
            }

            RaiseChanged("FeedSpot");
        }

        /// <summary>Uppdaterar enbart view-mode för Spot (Mid/TwoWay). Värdet lämnas orört.</summary>
        public void SetSpotViewMode(string pair6, ViewMode viewMode, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "").Replace("/", "").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(p6) || p6.Length != 6) return;

            if (_current != null && string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase))
            {
                var cur = _current.Spot;
                if (cur.ViewMode == viewMode) return;
                cur.Replace(cur.Effective, cur.Source, viewMode, cur.Override, nowUtc);
                _current = new MarketSnapshot(p6, cur);
            }
            else
            {
                var mf = new MarketField<double>(
                    value: new TwoWay<double>(0d, 0d),
                    source: MarketSource.Feed,
                    viewMode: viewMode,
                    ov: OverrideMode.None,
                    tsUtc: nowUtc,
                    version: 0,
                    stale: true);
                _current = new MarketSnapshot(p6, mf);
            }

            RaiseChanged("SpotViewMode");
        }

        /// <summary>Uppdaterar enbart override (None/Mid/Bid/Ask/Both) för Spot.</summary>
        public void SetSpotOverride(string pair6, OverrideMode ov, DateTime nowUtc)
        {
            var p6 = (pair6 ?? "").Replace("/", "").ToUpperInvariant();
            if (_current == null || !string.Equals(_current.Pair6, p6, StringComparison.OrdinalIgnoreCase)) return;

            var cur = _current.Spot;
            if (cur.Override == ov) return;
            cur.Replace(cur.Effective, cur.Source, cur.ViewMode, ov, nowUtc);
            _current = new MarketSnapshot(p6, cur);
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

            var cur = MarketSnapshot.TryGet(_current.RdByLeg, legId);
            if (cur == null)
            {
                _current.RdByLeg[legId] = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);
            }
            else
            {
                switch (cur.Override)
                {
                    case OverrideMode.Mid:
                    case OverrideMode.Both:
                        break; // låst
                    case OverrideMode.Bid:
                        cur.Replace(cur.Effective.WithAsk(tw.Ask), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                        break;
                    case OverrideMode.Ask:
                        cur.Replace(cur.Effective.WithBid(tw.Bid), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                        break;
                    case OverrideMode.None:
                        cur.Replace(tw, MarketSource.Feed, cur.ViewMode, OverrideMode.None, nowUtc);
                        break;
                }
                cur.MarkStale(isStale, nowUtc);
            }

            RaiseChanged("FeedRd:" + legId);
        }

        /// <summary>Feed → rf för ett leg. Samma regler som rd.</summary>
        public void SetRfFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false)
        {
            if (string.IsNullOrEmpty(legId)) throw new ArgumentNullException(nameof(legId));
            var p6 = (pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var tw = NormalizeMonotone(value);

            EnsureSnapshotPair(p6);

            var cur = MarketSnapshot.TryGet(_current.RfByLeg, legId);
            if (cur == null)
            {
                _current.RfByLeg[legId] = new MarketField<double>(tw, MarketSource.Feed, ViewMode.TwoWay, OverrideMode.None, nowUtc, 0, isStale);
            }
            else
            {
                switch (cur.Override)
                {
                    case OverrideMode.Mid:
                    case OverrideMode.Both:
                        break;
                    case OverrideMode.Bid:
                        cur.Replace(cur.Effective.WithAsk(tw.Ask), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                        break;
                    case OverrideMode.Ask:
                        cur.Replace(cur.Effective.WithBid(tw.Bid), cur.Source, cur.ViewMode, cur.Override, nowUtc);
                        break;
                    case OverrideMode.None:
                        cur.Replace(tw, MarketSource.Feed, cur.ViewMode, OverrideMode.None, nowUtc);
                        break;
                }
                cur.MarkStale(isStale, nowUtc);
            }

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

        private void RaiseChanged(string reason)
        {
            var snap = _current;
            var h = Changed;
            if (h != null) h(this, new MarketChangedEventArgs(snap, reason));
        }
    }
}
