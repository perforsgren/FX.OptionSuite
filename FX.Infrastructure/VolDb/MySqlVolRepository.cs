using System;
using System.Collections.Generic;
using FX.Core.Domain;
using FX.Core.Interfaces;
using MySqlConnector; // NuGet: MySqlConnector (till FX.Infrastructure)

namespace FX.Infrastructure.VolDb
{
    /// <summary>
    /// MySQL-implementation av IVolRepository som läser volytor ur fxvol-schemat.
    /// Denna klass hanterar endast läsning (senaste snapshot-id och tillhörande tenor-rader).
    /// </summary>
    public sealed class MySqlVolRepository : IVolRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// Skapar ett nytt MySqlVolRepository med explicit anslutningssträng.
        /// </summary>
        /// <param name="connectionString">ConnectionString mot fxvol-databasen.</param>
        public MySqlVolRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string kan inte vara tom.", nameof(connectionString));

            _connectionString = connectionString;
        }

        /// <summary>
        /// Returnerar snapshot-id för den senaste volytan (MAX(ts_utc)) för ett givet valutapar.
        /// </summary>
        /// <param name="pairSymbol">Par såsom "EUR/USD", "USD/SEK".</param>
        /// <returns>Snapshot-id eller null om inget snapshot finns.</returns>
        public long? GetLatestVolSnapshotId(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return null;

            const string sql = @"
                                SELECT s.snapshot_id
                                FROM fxvol.vol_surface_snapshot s
                                WHERE s.pair_symbol = @pair
                                ORDER BY s.ts_utc DESC
                                LIMIT 1;";

            using (var conn = CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                conn.Open();
                cmd.CommandText = sql;

                var p = cmd.CreateParameter();
                p.ParameterName = "@pair";
                p.Value = pairSymbol;
                cmd.Parameters.Add(p);

                var obj = cmd.ExecuteScalar();
                if (obj == null || obj == DBNull.Value)
                    return null;

                return Convert.ToInt64(obj);
            }
        }

        /// <summary>
        /// Hämtar header (konventioner + tidsstämpel + källa) för ett snapshot-id.
        /// </summary>
        /// <param name="snapshotId">Id från vol_surface_snapshot.</param>
        /// <returns>Header-objekt, eller null om snapshot saknas.</returns>
        public FX.Core.Domain.VolSurfaceSnapshotHeader GetSnapshotHeader(long snapshotId)
        {
            const string sql = @"
                                SELECT 
                                    snapshot_id,
                                    pair_symbol,
                                    ts_utc,
                                    delta_convention,
                                    premium_adjusted,
                                    source,
                                    note
                                FROM fxvol.vol_surface_snapshot
                                WHERE snapshot_id = @sid
                                LIMIT 1;";

            using (var conn = CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                conn.Open();
                cmd.CommandText = sql;

                var p = cmd.CreateParameter();
                p.ParameterName = "@sid";
                p.Value = snapshotId;
                cmd.Parameters.Add(p);

                using (var rd = cmd.ExecuteReader())
                {
                    if (!rd.Read())
                        return null;

                    return new FX.Core.Domain.VolSurfaceSnapshotHeader
                    {
                        SnapshotId = rd.GetInt64(0),
                        PairSymbol = rd.GetString(1),
                        TsUtc = rd.GetDateTime(2),
                        DeltaConvention = rd.GetString(3),
                        PremiumAdjusted = !rd.IsDBNull(4) && rd.GetBoolean(4),
                        Source = rd.IsDBNull(5) ? null : rd.GetString(5),
                        Note = rd.IsDBNull(6) ? null : rd.GetString(6)
                    };
                }
            }
        }


        /// <summary>
        /// Hämtar alla tenor-rader (ATM + RR/BF på mid) för angivet snapshot-id.
        /// Sorterar på tenor_days_nominal (om satt) och därefter tenor-kod.
        /// </summary>
        /// <param name="snapshotId">Snapshot-id från vol_surface_snapshot.</param>
        /// <returns>Enumerable av <see cref="VolSurfaceRow"/>.</returns>
        public IEnumerable<VolSurfaceRow> GetVolExpiries(long snapshotId)
        {
            const string sql = @"
                                SELECT 
                                    tenor_code,
                                    tenor_days_nominal,
                                    atm_bid, atm_ask, atm_mid,
                                    rr25_mid, bf25_mid, rr10_mid, bf10_mid
                                FROM fxvol.vol_surface_expiry
                                WHERE snapshot_id = @sid
                                ORDER BY tenor_days_nominal IS NULL, tenor_days_nominal, tenor_code;";

            var rows = new List<VolSurfaceRow>();

            using (var conn = CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                conn.Open();
                cmd.CommandText = sql;

                var p = cmd.CreateParameter();
                p.ParameterName = "@sid";
                p.Value = snapshotId;
                cmd.Parameters.Add(p);

                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        var r = new VolSurfaceRow
                        {
                            TenorCode = rd.GetString(0),
                            TenorDaysNominal = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1),
                            AtmBid = rd.IsDBNull(2) ? (decimal?)null : rd.GetDecimal(2),
                            AtmAsk = rd.IsDBNull(3) ? (decimal?)null : rd.GetDecimal(3),
                            AtmMid = rd.IsDBNull(4) ? (decimal?)null : rd.GetDecimal(4),
                            Rr25Mid = rd.IsDBNull(5) ? (decimal?)null : rd.GetDecimal(5),
                            Bf25Mid = rd.IsDBNull(6) ? (decimal?)null : rd.GetDecimal(6),
                            Rr10Mid = rd.IsDBNull(7) ? (decimal?)null : rd.GetDecimal(7),
                            Bf10Mid = rd.IsDBNull(8) ? (decimal?)null : rd.GetDecimal(8)
                        };
                        rows.Add(r);
                    }
                }
            }

            return rows;
        }

        /// <summary>
        /// Skapar en ny MySqlConnection.
        /// </summary>
        private MySqlConnection CreateConnection()
        {
            return new MySqlConnection(_connectionString);
        }





    }
}
