using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>Källa för ett fältvärde: Användare eller Feed.</summary>
    public enum MarketSource { User = 0, Feed = 1 }

    /// <summary>
    /// UI-visningsläge per fält.
    /// - FollowFeed: visa uppdateringar från feed (om ingen override).
    /// - Mid: visa/editeras som mid (under huven lagras alltid two-way).
    /// - TwoWay: visa/editeras som bid/ask.
    /// </summary>
    public enum ViewMode { FollowFeed = 0, Mid = 1, TwoWay = 2 }

    /// <summary>
    /// Override-läge per fält (vilka sidor är låsta av användaren).
    /// - None: inga overrides, feed kan skriva om vid FollowFeed.
    /// - Mid: användaren håller både bid och ask lika (bid=ask).
    /// - Bid/Ask: användaren låser ena sidan; feed får uppdatera den andra.
    /// - Both: användaren låser båda sidor.
    /// </summary>
    public enum OverrideMode { None = 0, Mid = 1, Bid = 2, Ask = 3, Both = 4 }

    /// <summary>
    /// Ett marknadsfält (Spot/Rd/Rf/Vol-punkt) med two-way-värde och metadata
    /// (källa, visningsläge, override, tidsstämpel, version, stale).
    /// </summary>
    public sealed class MarketField<T> where T : struct, IComparable<T>
    {
        /// <summary>Effektivt two-way-värde som skickas till pricern.</summary>
        public TwoWay<T> Effective { get; private set; }

        /// <summary>Senaste källan som skrev värdet (User/Feed).</summary>
        public MarketSource Source { get; private set; }

        /// <summary>UI-visningsläge (Mid/TwoWay/FollowFeed).</summary>
        public ViewMode ViewMode { get; private set; }

        /// <summary>Aktivt override-läge (Mid/Bid/Ask/Both/None).</summary>
        public OverrideMode Override { get; private set; }

        /// <summary>Tidsstämpel i UTC när fältet senast uppdaterades.</summary>
        public DateTime TimestampUtc { get; private set; }

        /// <summary>Monoton version som ökas varje gång fältet ändras.</summary>
        public long Version { get; private set; }

        /// <summary>True om värdet anses vara stalet (t.ex. uteblivna ticks).</summary>
        public bool IsStale { get; private set; }

        public MarketField(TwoWay<T> value, MarketSource source, ViewMode viewMode,
                           OverrideMode ov, DateTime tsUtc, long version, bool stale = false)
        {
            Effective = value;
            Source = source;
            ViewMode = viewMode;
            Override = ov;
            TimestampUtc = tsUtc;
            Version = version;
            IsStale = stale;
        }

        /// <summary>Markera fältet som stale eller fräscht (bumpa version).</summary>
        public void MarkStale(bool stale, DateTime nowUtc)
        {
            IsStale = stale;
            TimestampUtc = nowUtc;
            Version++;
        }

        /// <summary>Byt ut hela innehållet (värde + metadata). Bumpa version.</summary>
        public void Replace(TwoWay<T> tw, MarketSource src, ViewMode vm, OverrideMode ov, DateTime nowUtc)
        {
            Effective = tw;
            Source = src;
            ViewMode = vm;
            Override = ov;
            TimestampUtc = nowUtc;
            Version++;
        }
    }
}
