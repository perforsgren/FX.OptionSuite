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
        private readonly IVolRepository _repo;

        /// <summary>
        /// Skapar en ny presenter för volytehantering.
        /// </summary>
        /// <param name="repo">Repository som läser från fxvol-schemat.</param>
        public VolManagerPresenter(IVolRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
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
        /// Returnerar null om snapshot saknas.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD" eller "USD/SEK".</param>
        /// <returns>
        /// Tuple där SnapshotId/header kan vara null om inget finns, annars en lista med rader (kan vara tom).
        /// </returns>
        public (long? SnapshotId, FX.Core.Domain.VolSurfaceSnapshotHeader Header, List<FX.Core.Domain.VolSurfaceRow> Rows)
            LoadLatestWithHeader(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return (null, null, new List<FX.Core.Domain.VolSurfaceRow>());

            var sid = _repo.GetLatestVolSnapshotId(pairSymbol);
            if (sid == null)
                return (null, null, new List<FX.Core.Domain.VolSurfaceRow>());

            var header = _repo.GetSnapshotHeader(sid.Value);
            var rows = _repo.GetVolExpiries(sid.Value)?.ToList() ?? new List<FX.Core.Domain.VolSurfaceRow>();

            return (sid, header, rows);
        }

    }
}
