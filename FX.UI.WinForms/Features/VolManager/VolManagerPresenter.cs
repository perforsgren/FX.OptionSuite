using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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

        #region Persistens (UI-state))

        /// <summary>
        /// Laddar UI-state för VolManager (pinned, recent, view, tileCols) från en JSON-fil i %APPDATA%.
        /// Saknas fil returneras tomma listor och standardlägen.
        /// </summary>
        public (List<string> Pinned, List<string> Recent, string View, string TileColumns) LoadUiState()
        {
            try
            {
                var path = GetUiStatePath();
                if (!File.Exists(path))
                    return (new List<string>(), new List<string>(), "Tabs", "Compact");

                using (var fs = File.OpenRead(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(UiStateDto));
                    var dto = (UiStateDto)ser.ReadObject(fs);
                    return (
                        dto?.Pinned ?? new List<string>(),
                        dto?.Recent ?? new List<string>(),
                        string.IsNullOrWhiteSpace(dto?.View) ? "Tabs" : dto.View,
                        string.IsNullOrWhiteSpace(dto?.TileColumns) ? "Compact" : dto.TileColumns
                    );
                }
            }
            catch
            {
                // Fail safe
                return (new List<string>(), new List<string>(), "Tabs", "Compact");
            }
        }

        /// <summary>
        /// Sparar UI-state (pinned, recent, view, tileCols) till en JSON-fil i %APPDATA%.
        ///</summary>
        public void SaveUiState(List<string> pinned, List<string> recent, string view, string tileColumns)
        {
            try
            {
                var path = GetUiStatePath();
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir ?? ".");

                var dto = new UiStateDto
                {
                    Pinned = (pinned ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Recent = (recent ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    View = string.IsNullOrWhiteSpace(view) ? "Tabs" : view,
                    TileColumns = string.IsNullOrWhiteSpace(tileColumns) ? "Compact" : tileColumns
                };

                using (var fs = File.Create(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(UiStateDto));
                    ser.WriteObject(fs, dto);
                }
            }
            catch
            {
                // Best effort – inga throw i UI-flöde.
            }
        }

        /// <summary>
        /// Fullständig sökväg till UI-state-filen, per användare.
        /// %APPDATA%\FX.OptionSuite\VolManager\session_ui_state.json
        /// </summary>
        private string GetUiStatePath()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "FX.OptionSuite", "VolManager", "session_ui_state.json");
        }

        [DataContract]
        private sealed class UiStateDto
        {
            [DataMember] public List<string> Pinned { get; set; }
            [DataMember] public List<string> Recent { get; set; }
            [DataMember] public string View { get; set; }          // "Tabs" | "Tiles"
            [DataMember] public string TileColumns { get; set; }   // "AtmOnly" | "Compact"
        }



        #endregion

        #region Public API – Refresh & Load

        /// <summary>
        /// Hämtar senaste volyta för ett valutapar med enhetlig cache-policy.
        /// Delegerar till den befintliga laddaren (LoadLatestWithHeaderTagged).
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD".</param>
        /// <param name="force">
        /// true = bypassa mjuk cache och hämta från repo nu (t.ex. F5).
        /// false = tillåt cache enligt presenter-policy.
        /// </param>
        /// <returns>
        /// (FromCache, SnapshotId, Header, Rows) – Rows är tom lista om inget data finns.
        /// </returns>
        public (bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)
            RefreshPair(string pairSymbol, bool force = false)
        {
            // Normalisera input
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return (false, null, null, new List<VolSurfaceRow>());

            pairSymbol = pairSymbol.Trim();

            // Delegera till din existerande laddare som redan hanterar cache/taggning
            return LoadLatestWithHeaderTagged(pairSymbol, force);
        }

        /// <summary>
        /// Async-wrapper för <see cref="RefreshPair(string, bool)"/>.
        /// Implementeras via Task.Run i detta steg (kan bytas till äkta async repo senare).
        /// </summary>
        public System.Threading.Tasks.Task<(bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)>
            RefreshPairAsync(string pairSymbol, bool force = false)
        {
            return System.Threading.Tasks.Task.Run(() => RefreshPair(pairSymbol, force));
        }

        /// <summary>
        /// Hämtar senaste volyta för flera valutapar, med deduplicering och samma cache-policy
        /// som <see cref="RefreshPair(string, bool)"/>.
        /// </summary>
        /// <param name="pairs">Uppräknare av valutapar. Null/whitespace filtreras bort. Duplicat ignoreras (case-insensitivt).</param>
        /// <param name="force">true för att bypassa cache; annars presenter-policy.</param>
        /// <returns>
        /// Dictionary per par:
        ///   Key = pairSymbol,
        ///   Value = (FromCache, SnapshotId, Header, Rows).
        /// </returns>
        public Dictionary<string, (bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)>
            RefreshPinned(IEnumerable<string> pairs, bool force = false)
        {
            var result = new Dictionary<string, (bool, long?, VolSurfaceSnapshotHeader, List<VolSurfaceRow>)>(StringComparer.OrdinalIgnoreCase);
            if (pairs == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in pairs)
            {
                var key = (p ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!seen.Add(key)) continue; // dedupe

                result[key] = LoadLatestWithHeaderTagged(key, force);
            }

            return result;
        }

        /// <summary>
        /// Async-wrapper för <see cref="RefreshPinned(IEnumerable{string}, bool)"/>.
        /// Implementeras via Task.Run i detta steg.
        /// </summary>
        public System.Threading.Tasks.Task<Dictionary<string, (bool FromCache, long? SnapshotId, VolSurfaceSnapshotHeader Header, List<VolSurfaceRow> Rows)>>
            RefreshPinnedAsync(IEnumerable<string> pairs, bool force = false)
        {
            return System.Threading.Tasks.Task.Run(() => RefreshPinned(pairs, force));
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
