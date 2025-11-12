using System;

namespace FX.Core.Domain
{
    /// <summary>
    /// Header-information för ett vol-snapshot hämtat från vol_surface_snapshot.
    /// Innehåller endast metadata (konventioner, tidsstämpel, källa), inte tenor-rader.
    /// </summary>
    public sealed class VolSurfaceSnapshotHeader
    {
        /// <summary>Snapshot-id i vol_surface_snapshot.</summary>
        public long SnapshotId { get; set; }

        /// <summary>Valutapar (t.ex. "USD/SEK").</summary>
        public string PairSymbol { get; set; }

        /// <summary>Tidsstämpel (UTC) för snapshotet.</summary>
        public DateTime TsUtc { get; set; }

        /// <summary>Delta-konvention enligt DB (SPOT/FWD).</summary>
        public string DeltaConvention { get; set; }

        /// <summary>Premium-adjusted flagga (1/0 i DB → bool här).</summary>
        public bool PremiumAdjusted { get; set; }

        /// <summary>Källa för snapshotet (om ifylld i DB).</summary>
        public string Source { get; set; }

        /// <summary>Valfri notering/kommentar (om ifylld i DB).</summary>
        public string Note { get; set; }
    }
}
