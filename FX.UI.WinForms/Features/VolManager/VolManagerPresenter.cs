using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Linq;
using FX.Core.Domain;
using FX.Core.Interfaces;
using System.Threading;
using System.Threading.Tasks;


namespace FX.UI.WinForms.Features.VolManager
{
    /// <summary>
    /// Presenter för att läsa volytor från databasen via IVolRepository.
    /// Den hanterar endast läsning: "senaste snapshot" och dess tenor-rader.
    /// </summary>
    public sealed class VolManagerPresenter
    {

        #region Fields

        private readonly IVolRepository _repo;
        // vy-referens för UI-bindningar (BeginInvoke, BindPairSurface/UpdateTile)
        private VolManagerView _view;

        // CTS per valutapar för att kunna avbryta pågående laddningar (debounce)
        private readonly Dictionary<string, CancellationTokenSource> _loadCtsByPair = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// Kopplar vy-instansen till presentern för UI-bindningar och marshalling till UI-tråd.
        /// Måste anropas av sessionen efter att vyn har skapats.
        /// </summary>
        /// <param name="view">Aktuell <see cref="VolManagerView"/> för sessionen.</param>
        public void AttachView(VolManagerView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
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
        /// Hämtar en yta för ett par och binder den i vyn (Tabs/Tiles) med debounce.
        /// Visar busy, hanterar cancel tyst och sätter status (Fresh/Cached). Kompletterar saknade tenorer.
        /// </summary>
        public async Task RefreshPairAndBindAsync(string pairSymbol, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol)) return;
            pairSymbol = pairSymbol.Trim();

            var (cts, token) = ReplaceCts(pairSymbol);

            _view?.BeginInvoke((Action)(() => _view.ShowPairBusy(pairSymbol, true)));

            try
            {
                var r = await Task.Run(() =>
                {
                    if (token.IsCancellationRequested)
                        return default(ValueTuple<bool, long?, VolSurfaceSnapshotHeader, List<VolSurfaceRow>>);

                    var tmp = RefreshPair(pairSymbol, force);

                    if (token.IsCancellationRequested)
                        return default(ValueTuple<bool, long?, VolSurfaceSnapshotHeader, List<VolSurfaceRow>>);

                    return tmp;
                }).ConfigureAwait(false);

                if (token.IsCancellationRequested || !IsCurrentCts(pairSymbol, cts))
                {
                    _view?.BeginInvoke((Action)(() => _view.ShowPairBusy(pairSymbol, false)));
                    return;
                }

                var fromCache = r.Item1;
                var header = r.Item3;
                var rows = r.Item4 ?? new List<VolSurfaceRow>();
                var rowsFull = CompleteWithStandardTenors(rows); // ← NY: alltid visa standard-tenorer
                var tsUtc = header != null ? header.TsUtc : DateTime.UtcNow;

                _view?.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (_view.IsTabsModeActive())
                            _view.BindPairSurface(pairSymbol, tsUtc, rowsFull, fromCache);
                        else if (_view.IsTilesModeActive())
                            _view.UpdateTile(pairSymbol, tsUtc, rowsFull, fromCache);
                    }
                    catch
                    {
                        _view.ShowPairError(pairSymbol, "Bind failed.");
                    }
                    finally
                    {
                        _view.ShowPairBusy(pairSymbol, false);
                    }
                }));
            }
            catch (Exception ex)
            {
                if (IsCurrentCts(pairSymbol, cts))
                {
                    _view?.BeginInvoke((Action)(() =>
                    {
                        try { _view.ShowPairError(pairSymbol, ex.Message); }
                        finally { _view.ShowPairBusy(pairSymbol, false); }
                    }));
                }
            }
            finally
            {
                ClearCtsIfCurrent(pairSymbol, cts);
                try { cts.Dispose(); } catch { /* best effort */ }
            }
        }





        /// <summary>
        /// Hämtar/binder flera par (sekventiellt, enkel och robust). force=true bypassar cachen.
        /// </summary>
        public async System.Threading.Tasks.Task RefreshPinnedAndBindAsync(IEnumerable<string> pairs, bool force = false)
        {
            if (pairs == null) return;

            foreach (var p in pairs.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
                await RefreshPairAndBindAsync(p.Trim(), force).ConfigureAwait(false);
        }

        /// <summary>
        /// Anropas när ett par pinnas (eller reaktiveras). Binder direkt mot aktuell vy.
        /// </summary>
        public void OnPairPinned(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol)) return;
            _ = RefreshPairAndBindAsync(pairSymbol, force: false);
        }

        /// <summary>
        /// Anropas när vy-läget byts (Tabs &lt;→ Tiles). Binder om alla pinned från cache.
        /// </summary>
        public void OnViewModeChanged()
        {
            var pinned = _view?.SnapshotPinnedPairs() ?? Array.Empty<string>();
            _ = RefreshPinnedAndBindAsync(pinned, force: false);
        }

        /// <summary>
        /// Anropas när användaren byter aktiv par-flik i Tabs-läget. Binder om just den.
        /// </summary>
        public void OnActivePairTabChanged(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol)) return;
            _ = RefreshPairAndBindAsync(pairSymbol, force: false);
        }


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

        #endregion

        #region Privata hjälpare

        /// <summary>
        /// Skapa ny CTS för paret och avbryt/avyttra tidigare CTS om den fanns.
        /// </summary>
        private (CancellationTokenSource cts, CancellationToken token) ReplaceCts(string pair)
        {
            CancellationTokenSource old = null;
            var cts = new CancellationTokenSource();
            lock (_loadCtsByPair)
            {
                if (_loadCtsByPair.TryGetValue(pair, out old))
                {
                    try { old.Cancel(); } catch { }
                    try { old.Dispose(); } catch { }
                }
                _loadCtsByPair[pair] = cts;
            }
            return (cts, cts.Token);
        }

        /// <summary>True om angiven CTS fortfarande är den aktiva för paret.</summary>
        private bool IsCurrentCts(string pair, CancellationTokenSource cts)
        {
            lock (_loadCtsByPair)
                return _loadCtsByPair.TryGetValue(pair, out var cur) && ReferenceEquals(cur, cts);
        }

        /// <summary>Rensa CTS för paret om samma instans är aktiv.</summary>
        private void ClearCtsIfCurrent(string pair, CancellationTokenSource cts)
        {
            lock (_loadCtsByPair)
            {
                if (_loadCtsByPair.TryGetValue(pair, out var cur) && ReferenceEquals(cur, cts))
                    _loadCtsByPair.Remove(pair);
            }
        }


        #endregion

        #region Privata hjälpare – transform/merge

        /// <summary>
        /// Returnerar true om paret ska betraktas som ankrat (ATM justeras som Offset).
        /// Just nu endast USD/SEK enligt din setup.
        /// </summary>
        public bool IsAnchoredPair(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol)) return false;
            var p = pairSymbol.Trim().ToUpperInvariant();

            // Tillåt både "USD/SEK" och "USDSEK" för robusthet
            return p == "USD/SEK" || p == "USDSEK";
        }

        /// <summary>
        /// Försöker slå upp ankare för ett target-par. Nu: USD/SEK → EUR/USD.
        /// </summary>
        public bool TryGetAnchorPair(string pairSymbol, out string anchorPair)
        {
            anchorPair = null;
            if (string.IsNullOrWhiteSpace(pairSymbol)) return false;

            var p = pairSymbol.Trim().ToUpperInvariant();
            if (p == "USD/SEK" || p == "USDSEK")
            {
                anchorPair = "EUR/USD";
                return true;
            }
            return false;
        }


        /// <summary>
        /// Standardtenorer som alltid ska visas i UI, även om DB saknar datapunkt.
        /// Ordningen styr hur griden/tiles renderas.
        /// </summary>
        private static readonly string[] _stdTenors = new[]
        {
            "ON","1W","2W","1M","2M","3M","6M","9M","1Y","2Y","3Y"
        };

        /// <summary>
        /// Tar in DB-rader och kompletterar med "tomma" rader för saknade standardtenorer.
        /// Används för att tillåta editering när data inte finns sedan tidigare.
        /// </summary>
        private List<VolSurfaceRow> CompleteWithStandardTenors(IList<VolSurfaceRow> rows)
        {
            var byTenor = new Dictionary<string, VolSurfaceRow>(StringComparer.OrdinalIgnoreCase);
            if (rows != null)
            {
                foreach (var r in rows)
                {
                    var key = (r?.TenorCode ?? "").Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!byTenor.ContainsKey(key)) byTenor[key] = r;
                }
            }

            var merged = new List<VolSurfaceRow>(_stdTenors.Length);
            foreach (var t in _stdTenors)
            {
                if (byTenor.TryGetValue(t, out var have))
                {
                    merged.Add(have);
                }
                else
                {
                    // Skapa "tom" rad för tenorn: editerbar (draft) i UI
                    merged.Add(new VolSurfaceRow
                    {
                        TenorCode = t,
                        TenorDaysNominal = null,
                        AtmBid = null,
                        AtmMid = null,
                        AtmAsk = null,
                        Rr25Mid = null,
                        Bf25Mid = null,
                        Rr10Mid = null,
                        Bf10Mid = null
                    });
                }
            }
            return merged;
        }



        #endregion

    }
}
