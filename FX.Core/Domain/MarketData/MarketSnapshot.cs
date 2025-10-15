using System;
using System.Collections.Generic;
using System.Linq;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Snapshot av marknadsdata per valutapar.
    /// Innehåller Spot (two-way + metadata) samt rd/rf per leg (two-way + metadata).
    /// 
    /// Design:
    /// - Snapshot är en "read model" som uppdateras av MarketStore.
    /// - Hjälpmetoderna muterar INTE snapshot.
    /// - Per-leg-kollektioner är case-insensitive.
    /// </summary>
    public sealed class MarketSnapshot
    {
        /// <summary>”EURSEK”, ”USDJPY”, etc. Alltid utan snedstreck och i upper.</summary>
        public string Pair6 { get; }

        /// <summary>
        /// Spot two-way + metadata (källa, viewmode, override, staleness, tidsstämplar).
        /// OBS: MarketField&lt;double&gt; bär en TwoWay&lt;double&gt; internt.
        /// </summary>
        public MarketField<double> Spot { get; }

        /// <summary>Domestic rates (rd) per leg. Nyckel = legId (t.ex. "A", "B"...).</summary>
        public Dictionary<string, MarketField<double>> RdByLeg { get; }

        /// <summary>Foreign rates (rf) per leg. Nyckel = legId (t.ex. "A", "B"...).</summary>
        public Dictionary<string, MarketField<double>> RfByLeg { get; }

        /// <summary>
        /// Skapar snapshot för ett valutapar med givet Spot-fält.
        /// Rd/Rf-initieras som tomma per-leg-mappar (fylls via MarketStore).
        /// </summary>
        public MarketSnapshot(string pair6, MarketField<double> spot)
        {
            if (spot == null) throw new ArgumentNullException(nameof(spot));

            Pair6 = NormalizePair6(pair6);
            Spot = spot;

            RdByLeg = new Dictionary<string, MarketField<double>>(StringComparer.OrdinalIgnoreCase);
            RfByLeg = new Dictionary<string, MarketField<double>>(StringComparer.OrdinalIgnoreCase);
        }

        // =========================
        // Hjälpmetoder (läsning)
        // =========================

        /// <summary>
        /// Normaliserar ett valutapar till "ABCDEF" (utan snedstreck, upper).
        /// Tom/ogiltig input ger "".
        /// </summary>
        public static string NormalizePair6(string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6)) return "";
            return pair6.Replace("/", "").Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Säkert uppslag i en per-leg-karta.
        /// Returnerar null om leg saknas (muterar inte snapshot).
        /// </summary>
        public static MarketField<double> TryGet(
            IDictionary<string, MarketField<double>> map, string legId)
        {
            if (map == null || string.IsNullOrEmpty(legId)) return null;

            MarketField<double> mf;
            return map.TryGetValue(legId, out mf) ? mf : null;
        }

        /// <summary>Hämtar rd-fältet för ett leg om det finns, annars null.</summary>
        public MarketField<double> TryGetRd(string legId)
        {
            return TryGet(RdByLeg, legId);
        }

        /// <summary>Hämtar rf-fältet för ett leg om det finns, annars null.</summary>
        public MarketField<double> TryGetRf(string legId)
        {
            return TryGet(RfByLeg, legId);
        }

        /// <summary>True om rd finns lagrat för legId.</summary>
        public bool HasRd(string legId)
        {
            if (string.IsNullOrEmpty(legId)) return false;
            return RdByLeg.ContainsKey(legId);
        }

        /// <summary>True om rf finns lagrat för legId.</summary>
        public bool HasRf(string legId)
        {
            if (string.IsNullOrEmpty(legId)) return false;
            return RfByLeg.ContainsKey(legId);
        }

        /// <summary>
        /// Returnerar unionen av alla legId som förekommer i rd eller rf (case-insensitive).
        /// Hjälper UI att rendera "rad per leg".
        /// </summary>
        public IReadOnlyList<string> AllLegIds()
        {
            if (RdByLeg.Count == 0 && RfByLeg.Count == 0) return Array.Empty<string>();

            var set = new HashSet<string>(RdByLeg.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in RfByLeg.Keys) set.Add(k);

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
