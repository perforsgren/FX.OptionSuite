using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Kontrakt för en enkel in-memory store för marknadsdata.
    /// - Håller senaste snapshotet (Spot + per-leg Rd/Rf).
    /// - Mergar feed/user enligt deterministiska regler (implementationen ansvarar).
    /// - Emitterar Changed-event när något uppdateras.
    /// </summary>
    public interface IMarketStore
    {
        /// <summary>Event som avfyras vid ändring (inkl. fullständigt snapshot).</summary>
        event EventHandler<MarketChangedEventArgs> Changed;

        /// <summary>Aktuellt snapshot (aldrig null efter konstruktion).</summary>
        MarketSnapshot Current { get; }

        // ================================
        // SPOT (oförändrat)
        // ================================

        /// <summary>
        /// Sätt Spot från användare (mid eller two-way) med explicit visningsläge.
        /// <paramref name="wasMid"/> = true betyder att användaren matade mid (bid=ask) och
        /// implementationen bör sätta Override=Mid för fältet.
        /// </summary>
        void SetSpotFromUser(string pair6, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt Spot från feed (alltid two-way). Kan markera stale.
        /// </summary>
        void SetSpotFromFeed(string pair6, TwoWay<double> value, DateTime nowUtc, bool isStale = false);

        /// <summary>
        /// Sätt enbart visnings-/tolkningsläge för Spot (Mid/TwoWay). Ändrar inte värdena.
        /// Triggar Changed. Kan kallas både vid start och vid användarens toggle.
        /// </summary>
        void SetSpotViewMode(string pair6, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt override-läget för Spot (None/Mid/Bid/Ask/Both). Triggar Changed.
        /// Använd t.ex. för att rensa Mid-lås när UI växlar till TwoWay.
        /// </summary>
        void SetSpotOverride(string pair6, OverrideMode ov, DateTime nowUtc);


        // ================================
        // PER-LEG RD
        // ================================

        /// <summary>
        /// Sätt Rd (inhemsk/quote-ccy) för ett specifikt leg från användare.
        /// - <paramref name="legId"/> är benets nyckel (t.ex. "A", "B" ...).
        /// - <paramref name="value"/> kan vara mid (bid=ask) eller two-way.
        /// - <paramref name="wasMid"/> = true betyder att inputen var mid och att Override=Mid bör gälla.
        /// - <paramref name="viewMode"/> styr hur UI tolkar/visar fältet (Mid/TwoWay); lagringen är alltid two-way.
        /// - Triggar Changed med Reason som bör indikera "UserRd" och legId.
        /// </summary>
        void SetRdFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt Rd för ett specifikt leg från feed (alltid two-way).
        /// - <paramref name="isStale"/> markerar om värdet är stalet.
        /// - Triggar Changed med Reason som bör indikera "FeedRd" och legId.
        /// </summary>
        void SetRdFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false);

        /// <summary>
        /// Sätt enbart visningsläge (Mid/TwoWay) för Rd på ett specifikt leg.
        /// Ändrar inte värdena. Triggar Changed.
        /// </summary>
        void SetRdViewMode(string pair6, string legId, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt override-läge (None/Mid/Bid/Ask/Both) för Rd på ett specifikt leg.
        /// Triggar Changed. Använd för att låsa upp/låsa sidor eller rensa Mid-lås.
        /// </summary>
        void SetRdOverride(string pair6, string legId, OverrideMode ov, DateTime nowUtc);

        // ================================
        // PER-LEG RF
        // ================================

        /// <summary>
        /// Sätt Rf (utländsk/base-ccy) för ett specifikt leg från användare.
        /// Samma regler som <see cref="SetRdFromUser"/> men för Rf.
        /// Triggar Changed (t.ex. Reason "UserRf" + legId).
        /// </summary>
        void SetRfFromUser(string pair6, string legId, TwoWay<double> value, bool wasMid, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt Rf för ett specifikt leg från feed (alltid two-way).
        /// Triggar Changed (t.ex. Reason "FeedRf" + legId).
        /// </summary>
        void SetRfFromFeed(string pair6, string legId, TwoWay<double> value, DateTime nowUtc, bool isStale = false);

        /// <summary>
        /// Sätt enbart visningsläge (Mid/TwoWay) för Rf på ett specifikt leg.
        /// Ändrar inte värdena. Triggar Changed.
        /// </summary>
        void SetRfViewMode(string pair6, string legId, ViewMode viewMode, DateTime nowUtc);

        /// <summary>
        /// Sätt override-läge (None/Mid/Bid/Ask/Both) för Rf på ett specifikt leg.
        /// Triggar Changed.
        /// </summary>
        void SetRfOverride(string pair6, string legId, OverrideMode ov, DateTime nowUtc);

    }




    /// <summary>Event-args som bär med sig nytt snapshot och orsak.</summary>
    public sealed class MarketChangedEventArgs : EventArgs
    {
        public MarketSnapshot Snapshot { get; }
        public string Reason { get; }

        public MarketChangedEventArgs(MarketSnapshot snap, string reason)
        {
            Snapshot = snap;
            Reason = reason;
        }
    }
}
