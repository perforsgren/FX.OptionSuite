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
        /// Hämtar snapshot-id för den senaste volytan för ett valutapar (MAX(ts_utc)).
        /// Returnerar null om inget snapshot finns.
        /// </summary>
        /// <param name="pairSymbol">Par som "EUR/USD", "USD/SEK" etc.</param>
        /// <returns>Senaste snapshot-id eller null om saknas.</returns>
        long? GetLatestVolSnapshotId(string pairSymbol);

        /// <summary>
        /// Hämtar samtliga tenor-rader (ATM och RR/BF på mid) för ett givet snapshot.
        /// Resultatet är sorterat på tenor_days_nominal (om finns) och därefter tenor-kod.
        /// </summary>
        /// <param name="snapshotId">Id från vol_surface_snapshot.</param>
        /// <returns>Enumerable av VolSurfaceRow.</returns>
        IEnumerable<VolSurfaceRow> GetVolExpiries(long snapshotId);

        /// <summary>
        /// Hämtar header (konventioner + tidsstämpel + källa) för ett snapshot-id.
        /// </summary>
        /// <param name="snapshotId">Id från vol_surface_snapshot.</param>
        /// <returns>Header-objekt, eller null om snapshot saknas.</returns>
        VolSurfaceSnapshotHeader GetSnapshotHeader(long snapshotId);
    }


}
