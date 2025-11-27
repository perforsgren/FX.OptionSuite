using System;

namespace FX.Core.Domain
{
    /// <summary>
    /// Representerar en "effektiv" ATM-rad per tenor från v_atm_effective_latest:
    /// - Innehåller både bas-ATM (anchor) + offset och färdigräknad effektiv ATM mid/bid/ask.
    /// - Används som read-modell i Volatility Manager för att visa ATM-kedjan (ankare → offset → effektiv).
    /// </summary>
    public sealed class EffectiveAtmRow
    {
        /// <summary>
        /// Valutaparet, t.ex. "EUR/USD" eller "USD/SEK".
        /// </summary>
        public string PairSymbol { get; set; }

        /// <summary>
        /// Tenor-kod, t.ex. "1W", "1M", "3M".
        /// </summary>
        public string TenorCode { get; set; }

        /// <summary>
        /// Antal dagar som används för sortering (kan vara null).
        /// </summary>
        public int? DaysForSort { get; set; }

        /// <summary>
        /// Källa/typ för raden, t.ex. "DIRECT" eller "ANCHORED".
        /// Exakt innehåll styrs av v_atm_effective_latest.source_kind.
        /// </summary>
        public string SourceKind { get; set; }

        /// <summary>
        /// Ankarets valutapar om raden är ankrad (kan vara null).
        /// </summary>
        public string AnchorPairSymbol { get; set; }

        /// <summary>
        /// Tidsstämpel (UTC) för bas-ATM (från snapshot eller ankarets yta).
        /// </summary>
        public DateTime BaseTimestampUtc { get; set; }

        /// <summary>
        /// Bas-ATM mid (från ankare eller direkt yta). Kan vara null om inget basvärde finns.
        /// </summary>
        public decimal? BaseAtmMid { get; set; }

        /// <summary>
        /// Offset mot bas-ATM (tenorspecifik), enligt vol_anchor_atm_rule.offset_mid.
        /// </summary>
        public decimal? OffsetMid { get; set; }

        /// <summary>
        /// Total ATM-spread (Bid/Ask-bredd) enligt effektiva regler.
        /// </summary>
        public decimal? SpreadTotal { get; set; }

        /// <summary>
        /// Effektiv ATM mid (base + offset), dvs värdet som ska användas i prissättning.
        /// </summary>
        public decimal? AtmMidEffective { get; set; }

        /// <summary>
        /// Effektiv ATM bid.
        /// </summary>
        public decimal? AtmBidEffective { get; set; }

        /// <summary>
        /// Effektiv ATM ask.
        /// </summary>
        public decimal? AtmAskEffective { get; set; }

        /// <summary>
        /// Offset som används i ankrat par (Mid = BaseAtmMid + AtmOffset).
        /// </summary>
        public decimal? AtmOffset { get; set; }


    }
}
