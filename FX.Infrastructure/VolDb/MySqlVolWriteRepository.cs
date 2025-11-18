using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FX.Core.Interfaces;

namespace FX.Infrastructure.VolDb
{
    /// <summary>
    /// Minimal skriv-repository för vol-publicering. Denna stub returnerar ett
    /// artificiellt audit-id utan att skriva till DB. Ersätts i senare steg med riktig MySQL-upsert.
    /// </summary>
    public sealed class MySqlVolWriteRepository : IVolWriteRepository
    {
        private readonly string _connectionString;

        /// <summary>Initierar repo med anslutningssträng (sparas endast; ej använd än).</summary>
        public MySqlVolWriteRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string kan inte vara tom.", nameof(connectionString));
            _connectionString = connectionString;
        }

        /// <summary>
        /// Stub för publicering av voländringar. Räknar ut ATM Bid/Ask från AtmMid och AtmSpread
        /// och returnerar ett deterministiskt audit-id. Ingen DB-skrivning görs i denna stub.
        /// När riktig transaktion implementeras ska samma härledning användas i SQL-lagret:
        /// bid = mid - spread/2, ask = mid + spread/2 (med validering att spread ≥ 0).
        /// </summary>
        public Task<long> UpsertSurfaceRowsAsync(
            string user,
            string pair,
            DateTime tsUtc,
            IEnumerable<VolPublishRow> rows,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<long>(ct);

            if (string.IsNullOrWhiteSpace(pair))
                throw new ArgumentException("pair måste anges.", nameof(pair));

            var list = (rows ?? Array.Empty<VolPublishRow>()).ToList();
            if (list.Count == 0)
            {
                // Inget att publicera – returnera ändå ett deterministiskt id för spårbarhet.
                var emptyId = Math.Abs((user ?? "").GetHashCode()) ^ Math.Abs(pair.ToUpperInvariant().GetHashCode()) ^ tsUtc.Date.GetHashCode();
                return Task.FromResult(emptyId == 0 ? 1L : (long)emptyId);
            }

            // Enkel validering + härledning av bid/ask i minnet (endast för kontroll i detta stub-steg)
            foreach (var r in list)
            {
                // Validera tenor
                if (string.IsNullOrWhiteSpace(r.TenorCode))
                    throw new ArgumentException("TenorCode saknas på en rad i publish-paketet.");

                // Validera spread (om satt)
                if (r.AtmSpread.HasValue && r.AtmSpread.Value < 0m)
                    throw new ArgumentException($"ATM Spread negativ för {r.TenorCode}.");

                // Härledning: endast om både mid och spread finns
                if (r.AtmMid.HasValue && r.AtmSpread.HasValue)
                {
                    var mid = r.AtmMid.Value;
                    var spr = r.AtmSpread.Value;

                    // Detta är bara en kontroll i stubben – riktiga värden ska materialiseras i kommande DB-implementation
                    var bid = mid - spr / 2m;
                    var ask = mid + spr / 2m;

                    // (Valfritt) Grundsanity: bid ≤ mid ≤ ask
                    if (bid > mid || ask < mid)
                        throw new InvalidOperationException($"ATM Bid/Ask inkonsistent (tenor {r.TenorCode}).");
                }

                // RR/BF kräver ingen härledning här (mittar redan).
                // AtmOffset används för anchored i upstream; AtmMid som kommer hit ska vara 'effective' redan.
            }

            // Pseudo-audit-id (deterministiskt men >0). När riktig transaktion finns ersätts detta.
            unchecked
            {
                var seed = 1469598103934665603L; // FNV-1a start
                seed ^= (user ?? "").GetHashCode(); seed *= 1099511628211L;
                seed ^= pair.ToUpperInvariant().GetHashCode(); seed *= 1099511628211L;
                seed ^= tsUtc.Date.GetHashCode(); seed *= 1099511628211L;
                seed ^= list.Count.GetHashCode(); seed *= 1099511628211L;

                // Blanda in några fält så id varierar med innehållet
                foreach (var r in list)
                {
                    seed ^= (r.TenorCode ?? "").GetHashCode(); seed *= 1099511628211L;
                    if (r.AtmMid.HasValue) { seed ^= r.AtmMid.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.AtmSpread.HasValue) { seed ^= r.AtmSpread.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.AtmOffset.HasValue) { seed ^= r.AtmOffset.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.Rr25Mid.HasValue) { seed ^= r.Rr25Mid.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.Rr10Mid.HasValue) { seed ^= r.Rr10Mid.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.Bf25Mid.HasValue) { seed ^= r.Bf25Mid.Value.GetHashCode(); seed *= 1099511628211L; }
                    if (r.Bf10Mid.HasValue) { seed ^= r.Bf10Mid.Value.GetHashCode(); seed *= 1099511628211L; }
                }

                var id = Math.Abs(seed);
                if (id == 0) id = 1;
                return Task.FromResult(id);
            }
        }

    }
}
