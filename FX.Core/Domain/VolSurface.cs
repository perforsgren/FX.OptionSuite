using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FX.Core.Domain
{
    /// <summary>Volytta för ett valutapar (okomplicerad container i steg 2).</summary>
    public sealed class VolSurface
    {
        public CurrencyPair Pair { get; }
        public ReadOnlyCollection<VolNode> Nodes { get; }

        public VolSurface(CurrencyPair pair, IList<VolNode> nodes)
        {
            Pair = pair ?? throw new ArgumentNullException(nameof(pair));
            nodes = nodes ?? new List<VolNode>();
            Nodes = new ReadOnlyCollection<VolNode>(nodes);
        }

        public bool TryGetVol(string tenor, string label, out double vol)
        {
            vol = 0.0;
            if (Nodes == null || Nodes.Count == 0) return false;
            var t = (tenor ?? "").ToUpperInvariant();
            var l = (label ?? "").ToUpperInvariant();
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (n.Tenor == t && n.Label == l)
                {
                    vol = n.Volatility;
                    return true;
                }
            }
            return false;
        }
    }
}
