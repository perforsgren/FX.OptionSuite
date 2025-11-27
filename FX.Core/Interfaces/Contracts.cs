// FX.Core.Interfaces/Contracts.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FX.Core.Domain;

namespace FX.Core.Interfaces
{
    // ============================
    // PRICE ENGINE (tvåvägs-vol)
    // ============================
    // Domän-API: tar FX.Core.Domain.PricingRequest (med VolQuote i DECIMAL)
    // och returnerar FX.Core.Domain.TwoSidedPriceResult (Bid/Mid/Ask + greker på Mid).
    public interface IPriceEngine
    {
        Task<TwoSidedPriceResult> PriceAsync(PricingRequest request, CancellationToken ct = default(CancellationToken));
    }

    // ============================
    // VOLATILITET
    // ============================
    public interface IVolService
    {
        double GetVol(string pair6, DateTime expiry, double strike, bool strikeIsDelta);
    }

    public interface IVolInterpolator
    {
        double Interpolate(FX.Core.VolSurface surface, DateTime expiry, double strike, bool strikeIsDelta);
    }

    // ============================
    // BUS / PUB-SUB
    // ============================
    public interface IMessageBus
    {
        IDisposable Subscribe<T>(Action<T> handler);
        void Publish<T>(T message);
    }

    // ============================
    // MARKET DATA
    // ============================
    public interface IMarketDataService
    {
        // MVP: synkront räcker – vi mockar i minne i steg 8
        double GetSpot(string pair6);
        double GetRd(string pair6);
        double GetRf(string pair6);
    }

    // ============================
    // BLOTTER
    // ============================
    public interface IBlotterService
    {
        void Book(object tradeDto);              // TradeDto definieras i Steg 8
        IReadOnlyList<object> GetAll();          // byts till IReadOnlyList<TradeDto>
    }

    // ============================
    // KALENDRAR & DATUM
    // ============================
    /// <summary>Mappar "EURSEK" → lista av kalender-namn (t.ex. ["TARGET","STO"]).</summary>
    public interface ICalendarResolver
    {
        string[] CalendarsForPair(string pair6);
    }

    /// <summary>Business day-API över en eller flera kalendrar.</summary>
    public interface IBusinessCalendar
    {
        bool IsBusinessDay(string[] calendars, DateTime date);
        DateTime AddBusinessDays(string[] calendars, DateTime start, int n);
        int CountBusinessDaysForward(string[] calendars, DateTime start, DateTime end);
    }

    /// <summary>Beräknar spot- och settlementdatum enligt FX-konventioner.</summary>
    public interface ISpotSetDateService
    {
        FX.Core.SpotSetDates Compute(string pair6, DateTime today, DateTime expiry);
    }




    /// <summary>
    /// Repository-kontrakt för att läsa volytor ur databasen, frikopplat från UI och motor.
    /// </summary>
    public interface IVolRepository
    {
        /// <summary>
        /// Hämtar snapshot-id för senaste volyta för valt par.
        /// </summary>
        long? GetLatestVolSnapshotId(string pairSymbol);

        /// <summary>
        /// Hämtar tenor-rader (ATM+RR/BF mid) för snapshot-id från vol_surface_expiry.
        /// </summary>
        IEnumerable<FX.Core.Domain.VolSurfaceRow> GetVolExpiries(long snapshotId);

        /// <summary>
        /// Hämtar header (konventioner + tidsstämpel + källa) för ett snapshot-id.
        /// </summary>
        FX.Core.Domain.VolSurfaceSnapshotHeader GetSnapshotHeader(long snapshotId);

        /// <summary>
        /// Hämtar effektiva ATM-rader per tenor för ett par från v_atm_effective_latest.
        /// </summary>
        IEnumerable<FX.Core.Domain.EffectiveAtmRow> GetEffectiveAtmRows(string pairSymbol);

        /// <summary>
        /// Returnerar true och sätter anchorPairSymbol om target-paret är ankrat enligt vol_anchor_map.
        /// </summary>
        bool TryGetAnchorPair(string targetPairSymbol, out string anchorPairSymbol);

        /// <summary>
        /// Läser current policy för ankrat par från fxvol.vol_anchor_atm_policy (alla tenorer).
        /// </summary>
        IEnumerable<AnchorAtmPolicyRow> GetAnchorAtmPolicy(string targetPairSymbol);

    }

    /// <summary>
    /// Skriv-interface för publicering av vol-rader. Valfritt (stubbar om null).
    /// </summary>
    public interface IVolWriteRepository
    {
        /// <summary>
        /// Upsert av ändrade rader för ett par vid given tidsstämpel.
        /// Returnerar t.ex. audit-id/snapshot-id (kan vara valfritt).
        /// </summary>
        Task<long> UpsertSurfaceRowsAsync(
            string user,
            string pair,
            DateTime tsUtc,
            IEnumerable<VolPublishRow> rows,
            CancellationToken ct);
    }

    /// <summary>
    /// Representerar en publicerbar ändring för en tenor i volytan.
    /// Modellen stödjer både ATM mid och ATM spread/offset.
    /// Spread används för icke-ankrade par.
    /// Offset används för ankrade par.
    /// RR/BF publiceras endast som mid-värden.
    /// </summary>
    public sealed class VolPublishRow
    {
        /// <summary>
        /// Tenorkod, t.ex. "1W" eller "1M".
        /// </summary>
        public string TenorCode { get; set; }

        /// <summary>
        /// Det nya ATM mid-värdet efter draft/ändring.
        /// Om null: ATM mid ändrades inte av användaren.
        /// </summary>
        public decimal? AtmMid { get; set; }

        /// <summary>
        /// ATM spread för icke-ankrade par.
        /// När spread finns -> atm_bid/atm_ask räknas ut från AtmMid ± (spread/2).
        /// </summary>
        public decimal? AtmSpread { get; set; }

        /// <summary>
        /// ATM offset för ankrade par.
        /// När offset används -> atm_mid = anchor_atm_mid + offset.
        /// Bid/Ask = Mid (dvs noll spread).
        /// </summary>
        public decimal? AtmOffset { get; set; }

        /// <summary>
        /// RR25 mid (ändrat värde).
        /// </summary>
        public decimal? Rr25Mid { get; set; }

        /// <summary>
        /// RR10 mid (ändrat värde).
        /// </summary>
        public decimal? Rr10Mid { get; set; }

        /// <summary>
        /// BF25 mid (ändrat värde).
        /// </summary>
        public decimal? Bf25Mid { get; set; }

        /// <summary>
        /// BF10 mid (ändrat värde).
        /// </summary>
        public decimal? Bf10Mid { get; set; }


    }

    /// <summary>
    /// Policy för ankrat par: per tenor lagras offset (mot anchor-mid) och ATM-spread.
    /// </summary>
    public sealed class AnchorAtmPolicyRow
    {
        /// <summary>Tenor-kod (ex. "1M").</summary>
        public string TenorCode { get; set; }

        /// <summary>Offset i volpunkter: Mid = AnchorMid + Offset.</summary>
        public decimal? OffsetMid { get; set; }

        /// <summary>ATM total spread i volpunkter.</summary>
        public decimal? SpreadTotal { get; set; }

        /// <summary>Gällande från (UTC) för denna policy-rad.</summary>
        public DateTime EffectiveFromUtc { get; set; }
    }


}
