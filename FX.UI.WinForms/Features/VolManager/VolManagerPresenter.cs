using System;
using System.Collections.Generic;
using System.Linq;
using FX.Core.Domain;
using FX.Core.Interfaces;

namespace FX.UI.WinForms.Features.VolManager
{
    /// <summary>
    /// Presenter för att läsa volytor från databasen via IVolRepository.
    /// Den hanterar endast läsning: "senaste snapshot" och dess tenor-rader.
    /// Ingen koppling till prismotorn i detta steg.
    /// </summary>
    public sealed class VolManagerPresenter
    {

        #region Fields

        private readonly IVolRepository _repo;

        #endregion

        #region Constructor & Init

        /// <summary>
        /// Skapar en ny presenter för volytehantering.
        /// </summary>
        /// <param name="repo">Repository som läser från fxvol-schemat.</param>
        public VolManagerPresenter(IVolRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        #endregion

        #region Caching

        /// <summary>
        /// Enkel per-pair cache för senaste laddade volyta (lättviktig, inga beroenden).
        /// Lagrar samma named tuple som LoadLatestWithHeader returnerar.
        /// </summary>
        private sealed class CachedSurface
        {
            public string Pair { get; set; }
            public DateTime CacheTimeUtc { get; set; }

            public (long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows) Result { get; set; }
        }


        /// <summary>
        /// Cache per valutapar (case-insensitivt).
        /// </summary>
        private readonly Dictionary<string, CachedSurface> _cache =
            new Dictionary<string, CachedSurface>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mjuk TTL för cache (undvik onödiga DB-hämtningar vid snabb tab-växling).
        /// </summary>
        private readonly TimeSpan _softTtl = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Töm hela cache (kan användas vid publish eller global invalidation).
        /// </summary>
        public void ClearAllCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Töm cache för ett specifikt valutapar.
        /// </summary>
        public void ClearCacheForPair(string pair)
        {
            if (string.IsNullOrWhiteSpace(pair)) return;
            _cache.Remove(pair);
        }


        #endregion

        /// <summary>
        /// Hämtar senaste snapshot-id för angivet valutapar och laddar samtliga tenor-rader.
        /// Returnerar både snapshot-id och en sorterad lista av rader för enkel databindning i UI.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD" eller "USD/SEK".</param>
        /// <returns>
        /// Tuple där Item1 = snapshot-id (kan vara null om saknas) och Item2 = lista med tenor-rader (kan vara tom).
        /// </returns>
        public (long? SnapshotId, List<VolSurfaceRow> Rows) LoadLatest(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return (null, new List<VolSurfaceRow>());

            var sid = _repo.GetLatestVolSnapshotId(pairSymbol);
            if (sid == null)
                return (null, new List<VolSurfaceRow>());

            var rows = _repo.GetVolExpiries(sid.Value)?.ToList() ?? new List<VolSurfaceRow>();
            return (sid, rows);
        }

        /// <summary>
        /// Hämtar senaste snapshot-id, dess header och tenor-rader för ett valutapar.
        /// Returnerar null/empty om snapshot saknas. Använder mini-cache med mjuk TTL.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD" eller "USD/SEK".</param>
        public (long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)
            LoadLatestWithHeader(string pairSymbol)
        {
            return LoadLatestWithHeader(pairSymbol, forceReload: false);
        }

        /// <summary>
        /// Hämtar senaste snapshot-id, dess header och tenor-rader för ett valutapar,
        /// med möjlighet att forcera bypass av cache.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD" eller "USD/SEK".</param>
        /// <param name="forceReload">True = bypass cache och hämta från DB.</param>
        public (long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)
            LoadLatestWithHeader(string pairSymbol, bool forceReload)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return (null, null, new List<VolSurfaceRow>());

            var key = pairSymbol.ToUpperInvariant();

            // 1) Cache-träff inom mjuk TTL om vi inte forcerar
            if (!forceReload && _cache.TryGetValue(key, out var cached))
            {
                var age = DateTime.UtcNow - cached.CacheTimeUtc;
                if (age <= _softTtl)
                    return cached.Result;
            }

            // 2) Hämta från repo (din befintliga logik)
            var sid = _repo.GetLatestVolSnapshotId(key);
            if (sid == null)
            {
                var empty = (null as long?, null as VolSurfaceSnapshotHeader, new List<VolSurfaceRow>());
                _cache[key] = new CachedSurface { Pair = key, CacheTimeUtc = DateTime.UtcNow, Result = empty };
                return empty;
            }

            var header = _repo.GetSnapshotHeader(sid.Value);
            var rows = _repo.GetVolExpiries(sid.Value)?.ToList() ?? new List<VolSurfaceRow>();

            var fresh = (sid as long?, header, rows);

            // 3) Uppdatera cache
            _cache[key] = new CachedSurface
            {
                Pair = key,
                CacheTimeUtc = DateTime.UtcNow,
                Result = fresh
            };

            return fresh;
        }

        /// <summary>
        /// Hämtar senaste snapshot-id, dess header och tenor-rader för ett valutapar,
        /// samt indikerar om resultatet kom från cache (soft-TTL) eller färskt från DB.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD".</param>
        public (bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)
            LoadLatestWithHeaderTagged(string pairSymbol)
        {
            return LoadLatestWithHeaderTagged(pairSymbol, forceReload: false);
        }

        /// <summary>
        /// Hämtar senaste snapshot-id, dess header och tenor-rader för ett valutapar,
        /// med möjlighet att forcera bypass av cache, samt flagga om resultatet kom från cache.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD".</param>
        /// <param name="forceReload">True = bypass cache och hämta från DB.</param>
        public (bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)
            LoadLatestWithHeaderTagged(string pairSymbol, bool forceReload)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return (false, null, null, new List<VolSurfaceRow>());

            var key = pairSymbol.ToUpperInvariant();

            // 1) Cache-träff inom mjuk TTL om vi inte forcerar
            if (!forceReload && _cache.TryGetValue(key, out var cached))
            {
                var age = DateTime.UtcNow - cached.CacheTimeUtc;
                if (age <= _softTtl)
                    return (true, cached.Result.SnapshotId, cached.Result.Header, cached.Result.Rows);
            }

            // 2) Hämta från repo (som i din befintliga LoadLatestWithHeader)
            var sid = _repo.GetLatestVolSnapshotId(key);
            if (sid == null)
            {
                var emptyTuple = (false, null as long?, null as VolSurfaceSnapshotHeader, new List<VolSurfaceRow>());
                _cache[key] = new CachedSurface { Pair = key, CacheTimeUtc = DateTime.UtcNow, Result = (emptyTuple.Item2, emptyTuple.Item3, emptyTuple.Item4) };
                return emptyTuple;
            }

            var header = _repo.GetSnapshotHeader(sid.Value);
            var rows = _repo.GetVolExpiries(sid.Value)?.ToList() ?? new List<VolSurfaceRow>();

            var freshCore = (sid as long?, header, rows);

            // 3) Uppdatera cache
            _cache[key] = new CachedSurface
            {
                Pair = key,
                CacheTimeUtc = DateTime.UtcNow,
                Result = freshCore
            };

            return (false, freshCore.Item1, freshCore.Item2, freshCore.Item3);
        }



    }
}
