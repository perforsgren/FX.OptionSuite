// ============================================================
// SPRINT 1 – STEG 4: AppState (immutabel snapshot) + Store
// Varför:  En källa till sanning som UI kan binda till; event-driven uppdatering.
// Vad:     AppState (data) + AppStateStore (Replace/Update + StateChanged).
// Klar när:UI kan prenumerera och rendera när state uppdateras.
// ============================================================
using System;
using System.Collections.Generic;
using System.Threading;
using FX.Core.Domain;

namespace FX.Services
{
    public sealed class AppState
    {
        public readonly int Version;
        public readonly CurrencyPair ActivePair;                   // kan vara null innan valt
        public readonly double Spot;                               // NaN om okänt
        public readonly double Rd;                                 // NaN om okänt
        public readonly double Rf;                                 // NaN om okänt
        public readonly string SurfaceId;                          // null om okänd
        public readonly PricerResult LastPricerResult;             // kan vara null
        public readonly IReadOnlyList<BookedTrade> Blotter;        // tom lista om inga trades

        public AppState(
            int version,
            CurrencyPair activePair,
            double spot,
            double rd,
            double rf,
            string surfaceId,
            PricerResult lastPricerResult,
            IReadOnlyList<BookedTrade> blotter)
        {
            Version = version;
            ActivePair = activePair;
            Spot = spot;
            Rd = rd;
            Rf = rf;
            SurfaceId = surfaceId;
            LastPricerResult = lastPricerResult;
            Blotter = blotter ?? new List<BookedTrade>();
        }

        public static AppState Empty()
        {
            return new AppState(
                version: 0,
                activePair: null,
                spot: double.NaN,
                rd: double.NaN,
                rf: double.NaN,
                surfaceId: null,
                lastPricerResult: null,
                blotter: new List<BookedTrade>()
            );
        }

        // Små "With"-hjälpare för enklare uppdateringar (skapar ny snapshot)
        public AppState WithActivePair(CurrencyPair p)
            => new AppState(Version + 1, p, Spot, Rd, Rf, SurfaceId, LastPricerResult, Blotter);

        public AppState WithMarket(double spot, double rd, double rf)
            => new AppState(Version + 1, ActivePair, spot, rd, rf, SurfaceId, LastPricerResult, Blotter);

        public AppState WithSurface(string surfaceId)
            => new AppState(Version + 1, ActivePair, Spot, Rd, Rf, surfaceId, LastPricerResult, Blotter);

        public AppState WithPricerResult(PricerResult r)
            => new AppState(Version + 1, ActivePair, Spot, Rd, Rf, SurfaceId, r, Blotter);

        public AppState WithBlotter(IReadOnlyList<BookedTrade> trades)
            => new AppState(Version + 1, ActivePair, Spot, Rd, Rf, SurfaceId, LastPricerResult, trades);
    }

    public sealed class AppStateStore
    {
        private AppState _current = AppState.Empty();
        private readonly object _gate = new object();

        public AppState Current
        {
            get { return Volatile.Read(ref _current); }
        }

        /// <summary>Event som UI kan lyssna på för att rendera om.</summary>
        public event Action<AppState> StateChanged;

        /// <summary>Ersätter hela state-atomen med ett nytt snapshot och noterar prenumeranter.</summary>
        public void Replace(AppState next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            Interlocked.Exchange(ref _current, next);
            var h = StateChanged;
            if (h != null) h(next);
        }

        /// <summary>Trådsäker funktionell uppdatering: next = updater(Current).</summary>
        public void Update(Func<AppState, AppState> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            lock (_gate)
            {
                var curr = _current;
                var next = updater(curr);
                Replace(next);
            }
        }
    }
}
