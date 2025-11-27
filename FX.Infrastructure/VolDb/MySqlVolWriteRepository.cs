using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FX.Core.Interfaces;
using MySqlConnector;

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
        /// Publicerar ändringar för ett valutapar enligt policyn:
        /// • Icke-ankrat: skapar alltid ett nytt snapshot och skriver Bid/Ask (beräknat från Mid/Spread) samt ev. RR/BF.
        /// • Ankrat:     skriver inget snapshot. Upsert av Offset+Spread per tenor i fxvol.vol_anchor_atm_policy.
        /// Returnerar nytt snapshot_id (icke-ankrat) eller tidsbaserat id (ankrat).
        /// </summary>
        public async Task<long> UpsertSurfaceRowsAsync(
            string user,
            string pair,
            DateTime tsUtc,
            IEnumerable<VolPublishRow> rows,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pair)) throw new ArgumentException("pair saknas.", nameof(pair));
            if (rows == null) throw new ArgumentNullException(nameof(rows));

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        // 0) Är paret ankrat? (via vol_anchor_map)
                        string anchorPair = null;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = @"
SELECT anchor_pair_symbol
FROM fxvol.vol_anchor_map
WHERE target_pair_symbol = @p
LIMIT 1;";
                            cmd.Parameters.Add("@p", MySqlDbType.VarChar).Value = pair;
                            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                            anchorPair = (obj == null || obj == DBNull.Value) ? null : Convert.ToString(obj);
                        }

                        // A) ANKRAT → upsert i vol_anchor_atm_policy (offset + spread). Inget snapshot.
                        if (!string.IsNullOrWhiteSpace(anchorPair))
                        {
                            using (var up = conn.CreateCommand())
                            {
                                up.Transaction = tx;
                                up.CommandText = @"
INSERT INTO fxvol.vol_anchor_atm_policy
(target_pair_symbol, tenor_code, effective_from_utc, effective_to_utc, offset_mid, spread_total, source, note)
VALUES (@pair, @tenor, @ts, NULL, @off, @spr, 'UI', @note)
ON DUPLICATE KEY UPDATE
  offset_mid         = COALESCE(VALUES(offset_mid), offset_mid),
  spread_total       = COALESCE(VALUES(spread_total), spread_total),
  effective_from_utc = COALESCE(VALUES(effective_from_utc), effective_from_utc),
  effective_to_utc   = NULL,
  source             = VALUES(source),
  note               = VALUES(note);";
                                up.Parameters.Add("@pair", MySqlDbType.VarChar).Value = pair;
                                up.Parameters.Add("@tenor", MySqlDbType.VarChar);
                                up.Parameters.Add("@ts", MySqlDbType.Timestamp).Value = tsUtc;
                                up.Parameters.Add("@off", MySqlDbType.Decimal);
                                up.Parameters.Add("@spr", MySqlDbType.Decimal);
                                up.Parameters.Add("@note", MySqlDbType.VarChar).Value = $"UI Publish by {user}";

                                foreach (var r in rows)
                                {
                                    if (r == null || string.IsNullOrWhiteSpace(r.TenorCode)) continue;

                                    up.Parameters["@tenor"].Value = r.TenorCode;
                                    up.Parameters["@off"].Value = r.AtmOffset.HasValue ? (object)r.AtmOffset.Value : DBNull.Value;
                                    up.Parameters["@spr"].Value = r.AtmSpread.HasValue ? (object)r.AtmSpread.Value : DBNull.Value;

                                    await up.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                                }
                            }

                            await tx.CommitAsync(ct).ConfigureAwait(false);
                            return tsUtc.Ticks; // tidsbaserat id för policy-publish
                        }

                        // B) ICKE-ANKRAT → nytt snapshot med Bid/Ask (från Mid/Spread)
                        // 1) Ärv header från tidigare snapshot
                        string prevDeltaConv = null;
                        int prevPremAdj = 0;
                        string prevSource = "UI";
                        string prevNote = "";

                        using (var hdr = conn.CreateCommand())
                        {
                            hdr.Transaction = tx;
                            hdr.CommandText = @"
SELECT delta_convention, premium_adjusted, source, COALESCE(note,'')
FROM fxvol.vol_surface_snapshot
WHERE pair_symbol=@pair AND ts_utc <= @ts
ORDER BY ts_utc DESC
LIMIT 1;";
                            hdr.Parameters.Add("@pair", MySqlDbType.VarChar).Value = pair;
                            hdr.Parameters.Add("@ts", MySqlDbType.Timestamp).Value = tsUtc;

                            using (var rdr = (MySqlDataReader)await hdr.ExecuteReaderAsync(ct).ConfigureAwait(false))
                            {
                                if (await rdr.ReadAsync(ct).ConfigureAwait(false))
                                {
                                    prevDeltaConv = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                                    prevPremAdj = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                                    prevSource = rdr.IsDBNull(2) ? "UI" : rdr.GetString(2);
                                    prevNote = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                                }
                            }
                        }

                        // 2) Skapa nytt snapshot (header)
                        long newSnapshotId;
                        using (var ins = conn.CreateCommand())
                        {
                            ins.Transaction = tx;
                            ins.CommandText = @"
INSERT INTO fxvol.vol_surface_snapshot
(pair_symbol, ts_utc, delta_convention, premium_adjusted, source, note)
VALUES (@pair, @ts, @dc, @pa, @src, @note);
SELECT LAST_INSERT_ID();";
                            ins.Parameters.Add("@pair", MySqlDbType.VarChar).Value = pair;
                            ins.Parameters.Add("@ts", MySqlDbType.Timestamp).Value = tsUtc;

                            ins.Parameters.Add("@dc", MySqlDbType.VarChar);
                            ins.Parameters["@dc"].Value = prevDeltaConv == null ? (object)DBNull.Value : prevDeltaConv;

                            ins.Parameters.Add("@pa", MySqlDbType.Int32).Value = prevPremAdj;
                            ins.Parameters.Add("@src", MySqlDbType.VarChar).Value = string.IsNullOrEmpty(prevSource) ? "UI" : prevSource;
                            ins.Parameters.Add("@note", MySqlDbType.VarChar).Value = string.IsNullOrEmpty(prevNote) ? $"UI Publish by {user}" : prevNote;

                            var obj = await ins.ExecuteScalarAsync(ct).ConfigureAwait(false);
                            newSnapshotId = Convert.ToInt64(obj);
                        }

                        // 3) Klona yta från föregående snapshot (om det finns)
                        using (var clone = conn.CreateCommand())
                        {
                            clone.Transaction = tx;
                            clone.CommandText = @"
INSERT INTO fxvol.vol_surface_expiry
(snapshot_id, tenor_code, expiry_date, tenor_days_nominal,
 atm_bid, atm_ask, rr25_mid, rr10_mid, bf25_mid, bf10_mid)
SELECT
  @NewSid,
  e.tenor_code,
  DATE_ADD(DATE(@ts), INTERVAL e.tenor_days_nominal DAY),
  e.tenor_days_nominal,
  e.atm_bid, e.atm_ask, e.rr25_mid, e.rr10_mid, e.bf25_mid, e.bf10_mid
FROM fxvol.vol_surface_expiry e
JOIN fxvol.vol_surface_snapshot s ON s.snapshot_id = e.snapshot_id
WHERE s.pair_symbol = @pair
  AND s.ts_utc = (
       SELECT MAX(ts_utc) FROM fxvol.vol_surface_snapshot WHERE pair_symbol=@pair AND ts_utc < @ts
      );";
                            clone.Parameters.Add("@NewSid", MySqlDbType.Int64).Value = newSnapshotId;
                            clone.Parameters.Add("@ts", MySqlDbType.Timestamp).Value = tsUtc;
                            clone.Parameters.Add("@pair", MySqlDbType.VarChar).Value = pair;

                            await clone.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }

                        // 4) Applicera ändringar per tenor i nya snapshotet
                        using (var upd = conn.CreateCommand())
                        using (var exi = conn.CreateCommand())
                        using (var qDays = conn.CreateCommand())
                        using (var ins = conn.CreateCommand())
                        {
                            upd.Transaction = tx;
                            exi.Transaction = tx;
                            qDays.Transaction = tx;
                            ins.Transaction = tx;

                            upd.CommandText = @"
UPDATE fxvol.vol_surface_expiry
   SET
       atm_bid  = CASE
                    WHEN @AtmMid IS NOT NULL OR @AtmSpread IS NOT NULL
                      THEN (COALESCE(@AtmMid, (atm_bid + atm_ask)/2.0) - (COALESCE(@AtmSpread, (atm_ask - atm_bid)) / 2.0))
                    ELSE atm_bid
                  END,
       atm_ask  = CASE
                    WHEN @AtmMid IS NOT NULL OR @AtmSpread IS NOT NULL
                      THEN (COALESCE(@AtmMid, (atm_bid + atm_ask)/2.0) + (COALESCE(@AtmSpread, (atm_ask - atm_bid)) / 2.0))
                    ELSE atm_ask
                  END,
       rr25_mid = COALESCE(@Rr25Mid, rr25_mid),
       rr10_mid = COALESCE(@Rr10Mid, rr10_mid),
       bf25_mid = COALESCE(@Bf25Mid, bf25_mid),
       bf10_mid = COALESCE(@Bf10Mid, bf10_mid)
 WHERE snapshot_id = @Sid AND tenor_code = @Tenor;";
                            upd.Parameters.Add("@Sid", MySqlDbType.Int64);
                            upd.Parameters.Add("@Tenor", MySqlDbType.VarChar);
                            upd.Parameters.Add("@AtmMid", MySqlDbType.Decimal);
                            upd.Parameters.Add("@AtmSpread", MySqlDbType.Decimal);
                            upd.Parameters.Add("@Rr25Mid", MySqlDbType.Decimal);
                            upd.Parameters.Add("@Rr10Mid", MySqlDbType.Decimal);
                            upd.Parameters.Add("@Bf25Mid", MySqlDbType.Decimal);
                            upd.Parameters.Add("@Bf10Mid", MySqlDbType.Decimal);

                            exi.CommandText = @"SELECT 1 FROM fxvol.vol_surface_expiry WHERE snapshot_id=@Sid AND tenor_code=@Tenor LIMIT 1;";
                            exi.Parameters.Add("@Sid", MySqlDbType.Int64);
                            exi.Parameters.Add("@Tenor", MySqlDbType.VarChar);

                            qDays.CommandText = @"SELECT tenor_days_nominal FROM fxvol.vol_tenor_def WHERE tenor_code=@Tenor LIMIT 1;";
                            qDays.Parameters.Add("@Tenor", MySqlDbType.VarChar);

                            ins.CommandText = @"
INSERT INTO fxvol.vol_surface_expiry
(snapshot_id, tenor_code, expiry_date, tenor_days_nominal,
 atm_bid, atm_ask, rr25_mid, rr10_mid, bf25_mid, bf10_mid)
VALUES
(@Sid, @Tenor,
 DATE_ADD(DATE(@ts), INTERVAL @Days DAY),
 @Days, @InsBid, @InsAsk, @Rr25Mid, @Rr10Mid, @Bf25Mid, @Bf10Mid);";
                            ins.Parameters.Add("@Sid", MySqlDbType.Int64);
                            ins.Parameters.Add("@Tenor", MySqlDbType.VarChar);
                            ins.Parameters.Add("@Days", MySqlDbType.Int32);
                            ins.Parameters.Add("@InsBid", MySqlDbType.Decimal);
                            ins.Parameters.Add("@InsAsk", MySqlDbType.Decimal);
                            ins.Parameters.Add("@Rr25Mid", MySqlDbType.Decimal);
                            ins.Parameters.Add("@Rr10Mid", MySqlDbType.Decimal);
                            ins.Parameters.Add("@Bf25Mid", MySqlDbType.Decimal);
                            ins.Parameters.Add("@Bf10Mid", MySqlDbType.Decimal);

                            foreach (var r in rows)
                            {
                                if (r == null || string.IsNullOrWhiteSpace(r.TenorCode)) continue;

                                // UPDATE
                                upd.Parameters["@Sid"].Value = newSnapshotId;
                                upd.Parameters["@Tenor"].Value = r.TenorCode;
                                upd.Parameters["@AtmMid"].Value = r.AtmMid.HasValue ? (object)r.AtmMid.Value : DBNull.Value;
                                upd.Parameters["@AtmSpread"].Value = r.AtmSpread.HasValue ? (object)r.AtmSpread.Value : DBNull.Value;
                                upd.Parameters["@Rr25Mid"].Value = r.Rr25Mid.HasValue ? (object)r.Rr25Mid.Value : DBNull.Value;
                                upd.Parameters["@Rr10Mid"].Value = r.Rr10Mid.HasValue ? (object)r.Rr10Mid.Value : DBNull.Value;
                                upd.Parameters["@Bf25Mid"].Value = r.Bf25Mid.HasValue ? (object)r.Bf25Mid.Value : DBNull.Value;
                                upd.Parameters["@Bf10Mid"].Value = r.Bf10Mid.HasValue ? (object)r.Bf10Mid.Value : DBNull.Value;

                                var affected = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                                if (affected > 0) continue;

                                // INSERT ny tenor
                                exi.Parameters["@Sid"].Value = newSnapshotId;
                                exi.Parameters["@Tenor"].Value = r.TenorCode;
                                var existsObj = await exi.ExecuteScalarAsync(ct).ConfigureAwait(false);
                                if (existsObj != null) continue;

                                qDays.Parameters["@Tenor"].Value = r.TenorCode;
                                var daysObj = await qDays.ExecuteScalarAsync(ct).ConfigureAwait(false);
                                var days = (daysObj == null || daysObj == DBNull.Value) ? 0 : Convert.ToInt32(daysObj);

                                decimal? bid = null, ask = null;
                                if (r.AtmMid.HasValue && r.AtmSpread.HasValue)
                                {
                                    var half = r.AtmSpread.Value / 2m;
                                    bid = r.AtmMid.Value - half;
                                    ask = r.AtmMid.Value + half;
                                }

                                ins.Parameters["@Sid"].Value = newSnapshotId;
                                ins.Parameters["@Tenor"].Value = r.TenorCode;
                                ins.Parameters["@Days"].Value = days;
                                ins.Parameters["@InsBid"].Value = bid.HasValue ? (object)bid.Value : DBNull.Value;
                                ins.Parameters["@InsAsk"].Value = ask.HasValue ? (object)ask.Value : DBNull.Value;
                                ins.Parameters["@Rr25Mid"].Value = r.Rr25Mid.HasValue ? (object)r.Rr25Mid.Value : DBNull.Value;
                                ins.Parameters["@Rr10Mid"].Value = r.Rr10Mid.HasValue ? (object)r.Rr10Mid.Value : DBNull.Value;
                                ins.Parameters["@Bf25Mid"].Value = r.Bf25Mid.HasValue ? (object)r.Bf25Mid.Value : DBNull.Value;
                                ins.Parameters["@Bf10Mid"].Value = r.Bf10Mid.HasValue ? (object)r.Bf10Mid.Value : DBNull.Value;

                                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            }
                        }

                        await tx.CommitAsync(ct).ConfigureAwait(false);
                        return newSnapshotId;
                    }
                    catch
                    {
                        await tx.RollbackAsync(ct).ConfigureAwait(false);
                        throw;
                    }
                }
            }
        }









    }
}
