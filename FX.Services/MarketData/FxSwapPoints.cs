using System;
using System.Data;
using System.Linq;
using System.Globalization;
using Bloomberglp.Blpapi;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Generisk FX forward/points-loader för ett valfritt par (ex "EURUSD BGN Curncy").
    /// Läser FWD_CURVE + PX_BID/ASK/MID/LAST (spot).
    /// - DATE normaliseras (t.ex. "SETTLEMENT DATE" -> "DATE")
    /// - TENOR är frivillig (infereras från "SECURITY DESCRIPTION" om möjligt)
    /// - Outright:  MID/BID/ASK (eller OUTRIGHT MID etc.)
    /// - Points:    endast kolumnnamn som innehåller "POINT"
    ///
    /// ForwardAt(): använder outright om tillgängligt, annars (spotUsed + points * pip).
    /// Spot används i första hand från laddad PX_*, men du kan override:a per anrop.
    /// </summary>
    public sealed class FxSwapPoints
    {
        public DateTime ValDate { get; private set; }
        public DataTable Points { get; private set; }  // Date, Tenor, PointsBid/Mid/Ask, OutrightBid/Mid/Ask
        public string PairTicker { get; private set; } // "EURSEK BGN Curncy"
        public string Pair6 { get; private set; }      // "EURSEK"

        // Spot från samma request (om tillgänglig)
        public double SpotBid { get; private set; }
        public double SpotMid { get; private set; }
        public double SpotAsk { get; private set; }

        public DateTime SpotDate { get; set; }   // sätts vid load enligt parets spot-lag/kalendrar

        private const string FldFwdCurve = "FWD_CURVE";


        public void LoadFromBloomberg(Session session, string pairTicker, Func<string, DateTime> tenorToDateFallback = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(pairTicker)) throw new ArgumentException("pairTicker required.", nameof(pairTicker));

            PairTicker = pairTicker.Trim();
            Pair6 = ExtractPair6(PairTicker);

            if (!session.OpenService("//blp/refdata"))
                throw new InvalidOperationException("Could not open //blp/refdata.");

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");
            req.Append("securities", PairTicker);

            // Bulk + spotfält
            req.Append("fields", FldFwdCurve);
            req.Append("fields", "PX_BID");
            req.Append("fields", "PX_ASK");
            req.Append("fields", "PX_MID");
            req.Append("fields", "PX_LAST");

            // CURVE_DATE = idag
            var ov = req.GetElement("overrides").AppendElement();
            ov.SetElement("fieldId", "CURVE_DATE");
            ov.SetElement("value", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

            var cid = new CorrelationID(9001);
            session.SendRequest(req, cid);

            DataTable tbl = null;
            bool done = false;

            while (!done)
            {
                var ev = session.NextEvent();
                foreach (Message msg in ev)
                {
                    if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;
                    var sd = msg.GetElement("securityData");
                    if (sd.NumValues == 0) continue;

                    var first = sd.GetValueAsElement(0);
                    var fd = first.GetElement("fieldData");

                    // bulk
                    if (fd.HasElement(FldFwdCurve))
                        tbl = BulkToTable(fd.GetElement(FldFwdCurve));

                    // spot
                    SpotBid = TryGetDoubleField(fd, "PX_BID");
                    SpotAsk = TryGetDoubleField(fd, "PX_ASK");
                    var mid = TryGetDoubleField(fd, "PX_MID");
                    var last = TryGetDoubleField(fd, "PX_LAST");
                    if (!double.IsNaN(mid)) SpotMid = mid;
                    else if (!double.IsNaN(last)) SpotMid = last;
                    else if (!double.IsNaN(SpotBid) && !double.IsNaN(SpotAsk) && !double.IsInfinity(SpotBid) && !double.IsInfinity(SpotAsk))
                        SpotMid = 0.5 * (SpotBid + SpotAsk);
                }
                if (ev.Type == Event.EventType.RESPONSE) done = true;
            }

            if (tbl == null || tbl.Rows.Count == 0)
                throw new Exception(PairTicker + ": FWD_CURVE not available.");

            NormalizeSchema(tbl);

            // Saknas DATE? försök via callback
            if (!tbl.Columns.Contains("DATE"))
            {
                if (tenorToDateFallback == null)
                    throw new Exception("FWD_CURVE: DATE missing and no tenor→date resolver provided.");
                tbl.Columns.Add("DATE", typeof(DateTime));
                foreach (DataRow r in tbl.Rows)
                {
                    var tn = tbl.Columns.Contains("TENOR")
                        ? Convert.ToString(r["TENOR"] ?? "").Trim()
                        : InferTenorFromSecDesc(Convert.ToString(r["SECURITY DESCRIPTION"]), Pair6);
                    r["DATE"] = tenorToDateFallback(tn);
                }
            }

            // Output-tabell
            Points = new DataTable(Pair6 + "_FWD"); Points.Locale = CultureInfo.InvariantCulture;
            Points.Columns.Add("Date", typeof(DateTime));
            Points.Columns.Add("Tenor", typeof(string));
            Points.Columns.Add("PointsBid", typeof(double));
            Points.Columns.Add("PointsMid", typeof(double));
            Points.Columns.Add("PointsAsk", typeof(double));
            Points.Columns.Add("OutrightBid", typeof(double));
            Points.Columns.Add("OutrightMid", typeof(double));
            Points.Columns.Add("OutrightAsk", typeof(double));

            foreach (DataRow r in tbl.Rows)
            {
                var d = Convert.ToDateTime(r["DATE"], CultureInfo.InvariantCulture).Date;

                string tn = "";
                if (tbl.Columns.Contains("TENOR"))
                    tn = Convert.ToString(r["TENOR"] ?? "");
                else if (tbl.Columns.Contains("SECURITY DESCRIPTION"))
                    tn = InferTenorFromSecDesc(Convert.ToString(r["SECURITY DESCRIPTION"]), Pair6);

                // Points: endast namn med "POINT"
                double pMid = FirstNumeric(r, "MID POINTS", "MID_POINTS", "POINTS MID", "POINTS_MID", "POINTS", "FWD_POINTS", "FWD PTS");
                double pBid = FirstNumeric(r, "BID POINTS", "BID_POINTS");
                double pAsk = FirstNumeric(r, "ASK POINTS", "ASK_POINTS");

                // Outright: MID/BID/ASK etc.
                double oMid = FirstNumeric(r, "OUTRIGHT MID", "OUTRIGHT_MID", "FWD OUTRIGHT", "OUTRIGHT", "PX_MID", "MID");
                double oBid = FirstNumeric(r, "BID OUTRIGHT", "BID_OUTRIGHT", "BID PX", "BID");
                double oAsk = FirstNumeric(r, "ASK OUTRIGHT", "ASK_OUTRIGHT", "ASK PX", "ASK");

                var row = Points.NewRow();
                row["Date"] = d;
                row["Tenor"] = tn;
                row["PointsBid"] = pBid;
                row["PointsMid"] = pMid;
                row["PointsAsk"] = pAsk;
                row["OutrightBid"] = oBid;
                row["OutrightMid"] = oMid;
                row["OutrightAsk"] = oAsk;
                Points.Rows.Add(row);
            }

            ValDate = DateTime.Today;
        }

        /// <summary>
        /// Outright/points → forward vid givet settlement-datum.
        /// Interpolerar linjärt i ACT/360-tid **från SPOTDATUM**, inte från "idag".
        /// Före första noden: linjär interpol mellan Spot (t=0) och första datapunktens datum.
        /// Efter sista noden: flat extrap (behåll sista nodens värde).
        /// </summary>
        public double ForwardAt(DateTime settlement, string pair6, double? spotOverride = null)
        {
            if (Points == null || Points.Rows.Count == 0)
                throw new InvalidOperationException("LoadFromBloomberg() first.");

            // Sortera rader enligt datum (säkerställ DateTime)
            var rows = Points.AsEnumerable()
                             .OrderBy(r => r.Field<DateTime>("Date").Date)
                             .ToArray();

            // Pip-storlek
            string p6 = (pair6 ?? Pair6 ?? "").Trim().ToUpperInvariant().Replace("/", "");
            double pip = p6.EndsWith("JPY", StringComparison.Ordinal) ? 0.01 : 0.0001;

            // Finns outrightkolumn eller är det bara swap points?
            bool hasOut = rows.Any(r => ToDouble(r["OutrightMid"]) != 0.0);

            // === Viktigt: tidsnoll = SPOTDATE (fall back T+2 om ej satt) ===
            DateTime spot0 = (this.SpotDate != default(DateTime))
                ? this.SpotDate.Date
                : this.ValDate.Date.AddDays(2);  // defensiv fallback

            // Mål-datum
            DateTime d = settlement.Date;

            // Första/sista datapunkt i tabellen
            DateTime dFirst = rows[0].Field<DateTime>("Date").Date;
            DateTime dLast = rows[rows.Length - 1].Field<DateTime>("Date").Date;

            // Årsbråk i ACT/360, mätt från SPOTDATE
            double YF(DateTime a, DateTime b) =>
                Math.Max(0.0, (b.Date - a.Date).TotalDays / 360.0);

            // Spot (från override eller tabellens spot)
            double S = UseSpot(spotOverride);

            // === Före första noden: interpolera från spot (t=0) → första noden ===
            if (d <= dFirst)
            {
                double t = YF(spot0, d);
                double t1 = Math.Max(1e-12, YF(spot0, dFirst));

                if (hasOut)
                {
                    double o1 = rows[0].Field<double>("OutrightMid");
                    // Linjärt i tid mellan o(0)=S och o(t1)=o1
                    return S + (t / t1) * (o1 - S);
                }
                else
                {
                    double p1 = rows[0].Field<double>("PointsMid");
                    double pts = (t / t1) * p1;  // points(0)=0
                    return S + pts * pip;
                }
            }

            // === Efter sista noden: flat extrap ===
            if (d >= dLast)
            {
                return hasOut
                    ? rows[rows.Length - 1].Field<double>("OutrightMid")
                    : S + rows[rows.Length - 1].Field<double>("PointsMid") * pip;
            }

            // === Mellan två noder: binärsök + linjär i ACT/360-tid från SPOTDATE ===
            var dateArr = rows.Select(r => r.Field<DateTime>("Date").Date).ToArray();
            int hi = Array.BinarySearch(dateArr, d);
            if (hi >= 0)
            {
                var r = rows[hi];
                return hasOut ? r.Field<double>("OutrightMid")
                              : S + r.Field<double>("PointsMid") * pip;
            }

            hi = ~hi;
            int lo = hi - 1;

            var dLo = rows[lo].Field<DateTime>("Date").Date;
            var dHi = rows[hi].Field<DateTime>("Date").Date;

            double tLo = YF(spot0, dLo);
            double tHi = YF(spot0, dHi);
            double tX = YF(spot0, d);

            double w = (tHi - tLo) > 0 ? (tX - tLo) / (tHi - tLo) : 0.0;
            w = Math.Max(0.0, Math.Min(1.0, w)); // defensivt clamp

            if (hasOut)
            {
                double o0 = rows[lo].Field<double>("OutrightMid");
                double o1 = rows[hi].Field<double>("OutrightMid");
                return o0 + w * (o1 - o0);
            }
            else
            {
                double p0 = rows[lo].Field<double>("PointsMid");
                double p1 = rows[hi].Field<double>("PointsMid");
                double pts = p0 + w * (p1 - p0);
                return S + pts * pip;
            }
        }

        public double ForwardAt(
            DateTime settlement,
            string pair6,
            double? spotOverride = null,
            Func<string, DateTime, double> dfProvider = null,   // kan vara null
            bool useDfRatioIfAvailable = false
        )
        {
            if (Points == null || Points.Rows.Count == 0)
                throw new InvalidOperationException("LoadFromBloomberg() first.");

            string p6 = (pair6 ?? Pair6 ?? "").Trim().ToUpperInvariant().Replace("/", "");
            if (p6.Length < 6) throw new ArgumentException("pair6");

            // === NYTT: DF-ratio genväg ===
            if (useDfRatioIfAvailable && dfProvider != null)
            {
                string baseCcy = p6.Substring(0, 3);
                string quoteCcy = p6.Substring(3, 3);

                double dfB = Math.Max(1e-12, Math.Min(1.0, dfProvider(baseCcy, settlement.Date)));
                double dfQ = Math.Max(1e-12, Math.Min(1.0, dfProvider(quoteCcy, settlement.Date)));

                double S = UseSpot(spotOverride);
                return S * (dfB / dfQ);
            }

            // === Originaltabell-logik (lite städad) ===
            var rows = Points.AsEnumerable()
                             .OrderBy(r => r.Field<DateTime>("Date").Date)
                             .ToArray();

            bool hasOutright = rows.Any(r => ToDouble(r["OutrightMid"]) != 0.0);
            double pip = p6.EndsWith("JPY", StringComparison.Ordinal) ? 0.01 : 0.0001;

            DateTime spot0 = (this.SpotDate != default(DateTime))
                ? this.SpotDate.Date
                : this.ValDate.Date.AddDays(2);

            DateTime d = settlement.Date;
            DateTime dFirst = rows[0].Field<DateTime>("Date").Date;
            DateTime dLast = rows[rows.Length - 1].Field<DateTime>("Date").Date;

            Func<DateTime, DateTime, double> YF = (a, b) =>
                Math.Max(0.0, (b.Date - a.Date).TotalDays / 360.0);

            double Sspot = UseSpot(spotOverride);

            if (d <= dFirst)
            {
                double t = YF(spot0, d);
                double t1 = Math.Max(1e-12, YF(spot0, dFirst));

                if (hasOutright)
                {
                    double o1 = rows[0].Field<double>("OutrightMid");
                    return Sspot + (t / t1) * (o1 - Sspot);
                }
                else
                {
                    double p1 = rows[0].Field<double>("PointsMid");
                    double pts = (t / t1) * p1;
                    return Sspot + pts * pip;
                }
            }

            if (d >= dLast)
            {
                return hasOutright
                    ? rows[rows.Length - 1].Field<double>("OutrightMid") // <<< ändrat från ^1
                    : Sspot + rows[rows.Length - 1].Field<double>("PointsMid") * pip;
            }

            var dateArr = rows.Select(r => r.Field<DateTime>("Date").Date).ToArray();
            int hi = Array.BinarySearch(dateArr, d);
            if (hi >= 0)
            {
                var r = rows[hi];
                return hasOutright ? r.Field<double>("OutrightMid")
                                   : Sspot + r.Field<double>("PointsMid") * pip;
            }

            hi = ~hi;
            int lo = hi - 1;

            DateTime dLo = rows[lo].Field<DateTime>("Date").Date;
            DateTime dHi = rows[hi].Field<DateTime>("Date").Date;

            double tLo = YF(spot0, dLo);
            double tHi = YF(spot0, dHi);
            double tX = YF(spot0, d);

            double w = (tHi - tLo) > 0 ? (tX - tLo) / (tHi - tLo) : 0.0;
            if (w < 0.0) w = 0.0;
            if (w > 1.0) w = 1.0;

            if (hasOutright)
            {
                double o0 = rows[lo].Field<double>("OutrightMid");
                double o1 = rows[hi].Field<double>("OutrightMid");
                return o0 + w * (o1 - o0);
            }
            else
            {
                double p0 = rows[lo].Field<double>("PointsMid");
                double p1 = rows[hi].Field<double>("PointsMid");
                double pts = p0 + w * (p1 - p0);
                return Sspot + pts * pip;
            }
        }



        #region === Helpers ===
        // ---------- helpers ----------
        private double UseSpot(double? spotOverride)
        {
            if (spotOverride.HasValue) return spotOverride.Value;
            if (!double.IsNaN(SpotMid) && !double.IsInfinity(SpotMid) && SpotMid > 0.0) return SpotMid;
            throw new InvalidOperationException("Spot not available; pass spotOverride or ensure PX_* returned.");
        }

        private static string ExtractPair6(string pairTicker)
        {
            var first = (pairTicker ?? "").Trim().ToUpperInvariant().Split(' ')[0];
            var letters = new string(first.Where(char.IsLetter).ToArray());
            if (letters.Length >= 6) return letters.Substring(0, 6);
            return letters;
        }

        private static double TryGetDoubleField(Element fd, string name)
        {
            try
            {
                if (fd.HasElement(name))
                {
                    var el = fd.GetElement(name);
                    if (el.Datatype == Schema.Datatype.FLOAT64) return el.GetValueAsFloat64();
                    if (el.Datatype == Schema.Datatype.STRING)
                    {
                        double x; if (double.TryParse(el.GetValueAsString(), NumberStyles.Any, CultureInfo.InvariantCulture, out x)) return x;
                    }
                }
            }
            catch { }
            return double.NaN;
        }

        private static DataTable BulkToTable(Element bulk)
        {
            var dt = new DataTable(); if (bulk.NumValues == 0) return dt;

            var first = bulk.GetValueAsElement(0);
            for (int k = 0; k < first.NumElements; k++)
                dt.Columns.Add(first.GetElement(k).Name.ToString().ToUpperInvariant(), typeof(object));

            for (int i = 0; i < bulk.NumValues; i++)
            {
                var e = bulk.GetValueAsElement(i);
                var row = dt.NewRow();
                for (int k = 0; k < e.NumElements; k++)
                {
                    var el = e.GetElement(k);
                    string name = el.Name.ToString().ToUpperInvariant();
                    object val;
                    var dtype = el.Datatype;
                    if (dtype == Schema.Datatype.STRING) val = el.GetValueAsString();
                    else if (dtype == Schema.Datatype.FLOAT64) val = el.GetValueAsFloat64();
                    else if (dtype == Schema.Datatype.INT32) val = el.GetValueAsInt32();
                    else if (dtype == Schema.Datatype.DATE || dtype == Schema.Datatype.DATETIME) val = el.GetValueAsDatetime().ToSystemDateTime().Date;
                    else val = el.ToString();
                    row[name] = val ?? DBNull.Value;
                }
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static void NormalizeSchema(DataTable t)
        {
            if (!t.Columns.Contains("TENOR"))
            {
                foreach (var c in new[] { "CURVE_TENOR", "FWD_TENOR" })
                    if (t.Columns.Contains(c)) { t.Columns[c].ColumnName = "TENOR"; break; }
            }

            if (!t.Columns.Contains("DATE"))
            {
                foreach (var c in new[]
                {
                    "SETTLEMENT DATE","SETTLEMENT_DATE",
                    "DELIVERY DATE","DELIVERY_DATE",
                    "VALUE_DATE","END DATE","END_DATE","DATE"
                })
                {
                    if (t.Columns.Contains(c)) { t.Columns[c].ColumnName = "DATE"; break; }
                }
            }
        }

        private static string InferTenorFromSecDesc(string secDesc, string pair6)
        {
            if (string.IsNullOrWhiteSpace(secDesc)) return "";
            var s = secDesc.Trim().ToUpperInvariant();
            var key = (pair6 ?? "").Trim().ToUpperInvariant();
            int i = s.IndexOf(key);
            if (i >= 0)
            {
                var rest = s.Substring(i + key.Length).TrimStart();
                var token = rest.Split(' ')[0]; // ON, TN, 1W, 1M, ...
                return token;
            }
            return "";
        }

        private static double FirstNumeric(DataRow r, params string[] names)
        {
            foreach (var raw in names)
            {
                var nm = (raw ?? "").ToUpperInvariant();
                if (!r.Table.Columns.Contains(nm)) continue;
                var o = r[nm]; if (o == null || o == DBNull.Value) continue;

                var d = o as double?;
                if (d.HasValue && !double.IsNaN(d.Value) && !double.IsInfinity(d.Value))
                    return d.Value;

                var s = Convert.ToString(o, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(s)) continue;

                s = s.Trim().Replace(",", ".").Replace("%", "");
                double x;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out x))
                    return x;
            }
            return 0.0;
        }

        private static double ToDouble(object o)
        {
            if (o == null || o == DBNull.Value) return 0.0;
            var d = o as double?; if (d.HasValue) return d.Value;
            double x;
            if (double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out x))
                return x;
            return 0.0;
        }

        #endregion

        #region === Atomic multi-load (BLP) ===

        /// <summary>
        /// (NY) Laddar FLERA FX-tickers i EN Bloomberg ReferenceDataRequest (atomiskt snapshot).
        /// Returnerar en dictionary [exact ticker string → FxSwapPoints].
        /// Robust mot saknad DATE i bulk via tenor→datum-fallback.
        /// </summary>
        /// <param name="session">Öppen Bloomberg-session.</param>
        /// <param name="tickers">Lista av tickers, t.ex. { "EURUSD BGN Curncy", "USDEUR BGN Curncy", "USDSEK BGN Curncy", "SEKUSD BGN Curncy" }.</param>
        /// <param name="tenorToDateFallback">Fallback-funktion för att härleda datum från tenor om DATE saknas i bulk.</param>
        /// <returns>Dictionary som innehåller endast de tickers som gav giltigt svar.</returns>
        public static System.Collections.Generic.Dictionary<string, FxSwapPoints> LoadMultipleFromBloombergAtomic(
            Session session,
            System.Collections.Generic.IEnumerable<string> tickers,
            Func<string, DateTime> tenorToDateFallback)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (tickers == null) throw new ArgumentNullException(nameof(tickers));

            var list = new System.Collections.Generic.List<string>();
            foreach (var t in tickers)
            {
                var s = (t ?? "").Trim();
                if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase))
                    list.Add(s);
            }
            if (list.Count == 0) return new System.Collections.Generic.Dictionary<string, FxSwapPoints>(StringComparer.OrdinalIgnoreCase);

            if (!session.OpenService("//blp/refdata"))
                throw new InvalidOperationException("Could not open //blp/refdata.");

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");

            foreach (var t in list) req.Append("securities", t);

            // Fält: forward-bulk + spotfält
            req.Append("fields", FldFwdCurve);
            req.Append("fields", "PX_BID");
            req.Append("fields", "PX_ASK");
            req.Append("fields", "PX_MID");
            req.Append("fields", "PX_LAST");


            // VIKTIGT: tvinga fwd_curve att returnera "POINTS" (inte RATES/OUTRIGHT)
            //var ovs = req.GetElement("overrides");
            //{
            //    var ov = ovs.AppendElement();
            //    ov.SetElement("fieldId", "FWD_CURVE_QUOTE_FORMAT"); // PX342
            //    ov.SetElement("value", "POINTS");                   // "POINTS" | "OUTRIGHT" | (ev. "RATES" på vissa källor)
            //}

            // Override kurvdatum → idag
            var ovd = req.GetElement("overrides").AppendElement();
            ovd.SetElement("fieldId", "CURVE_DATE");
            ovd.SetElement("value", System.DateTime.Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));

            session.SendRequest(req, new CorrelationID(9521));

            var result = new System.Collections.Generic.Dictionary<string, FxSwapPoints>(System.StringComparer.OrdinalIgnoreCase);
            bool done = false;

            while (!done)
            {
                var ev = session.NextEvent();
                foreach (Message msg in ev)
                {
                    if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;

                    var sd = msg.GetElement("securityData");
                    for (int i = 0; i < sd.NumValues; i++)
                    {
                        var sec = sd.GetValueAsElement(i);
                        var ticker = sec.HasElement("security") ? sec.GetElementAsString("security") : "";
                        if (string.IsNullOrWhiteSpace(ticker)) continue;

                        if (!sec.HasElement("fieldData")) continue;
                        var fd = sec.GetElement("fieldData");

                        try
                        {
                            var built = BuildFromFieldDataAtomic(ticker, fd, tenorToDateFallback);
                            if (IsValidLeg(built))
                                result[ticker] = built;
                        }
                        catch
                        {
                            // Ignorera ogiltig/inkomplett security — vi väljer ett av alternativen per ben
                        }
                    }
                }
                if (ev.Type == Event.EventType.RESPONSE) done = true;
            }

            return result;
        }

        /// <summary>
        /// (NY) Hjälpare: bedöm om ett leg är "användbart" (spot>0 och åtminstone någon forward-data).
        /// </summary>
        public static bool IsValidLeg(FxSwapPoints leg)
        {
            if (leg == null) return false;
            if (!(leg.SpotMid > 0.0)) return false;
            if (leg.Points == null || leg.Points.Rows.Count == 0) return false;
            return true;
        }

        /// <summary>
        /// (NY) Bygger ett FxSwapPoints från ett fieldData-element (ReferenceDataResponse).
        /// Läser PX_* för spot, FWD_CURVE till DataTable, normaliserar schema och fyller Points-tabellen.
        /// Robust mot saknad DATE via tenor→datum-fallback.
        /// OBS: Skild från ev. befintlig BuildFromFieldData i din fil för att undvika namnkonflikt.
        /// </summary>
        private static FxSwapPoints BuildFromFieldDataAtomic(string pairTicker, Element fieldData, System.Func<string, System.DateTime> tenorToDateFallback)
        {
            var leg = new FxSwapPoints
            {
                PairTicker = (pairTicker ?? "").Trim(),
                Pair6 = ExtractPair6(pairTicker)
            };

            // Forward-bulk
            System.Data.DataTable bulk = null;
            if (fieldData.HasElement(FldFwdCurve))
                bulk = BulkToTable(fieldData.GetElement(FldFwdCurve));

            // Spot (PX_*)
            leg.SpotBid = TryGetDoubleField(fieldData, "PX_BID");
            leg.SpotAsk = TryGetDoubleField(fieldData, "PX_ASK");
            var mid = TryGetDoubleField(fieldData, "PX_MID");
            var last = TryGetDoubleField(fieldData, "PX_LAST");
            if (!double.IsNaN(mid)) leg.SpotMid = mid;
            else if (!double.IsNaN(last)) leg.SpotMid = last;
            else if (!double.IsNaN(leg.SpotBid) && !double.IsNaN(leg.SpotAsk) && !double.IsInfinity(leg.SpotBid) && !double.IsInfinity(leg.SpotAsk))
                leg.SpotMid = 0.5 * (leg.SpotBid + leg.SpotAsk);

            if (bulk == null || bulk.Rows.Count == 0)
                throw new System.Exception(pairTicker + ": FWD_CURVE not available.");

            NormalizeSchema(bulk);

            // DATE saknas → generera via fallback
            if (!bulk.Columns.Contains("DATE"))
            {
                if (tenorToDateFallback == null)
                    throw new System.Exception("FWD_CURVE: DATE missing and no tenor→date resolver provided.");

                bulk.Columns.Add("DATE", typeof(System.DateTime));
                foreach (System.Data.DataRow r in bulk.Rows)
                {
                    var tn = bulk.Columns.Contains("TENOR")
                        ? System.Convert.ToString(r["TENOR"] ?? "").Trim()
                        : InferTenorFromSecDesc(System.Convert.ToString(r["SECURITY DESCRIPTION"]), leg.Pair6);
                    r["DATE"] = tenorToDateFallback(tn);
                }
            }

            // Bygg Points-tabellen i samma format som singel-load använder
            var points = new System.Data.DataTable(leg.Pair6 + "_FWD") { Locale = System.Globalization.CultureInfo.InvariantCulture };
            points.Columns.Add("Date", typeof(System.DateTime));
            points.Columns.Add("Tenor", typeof(string));
            points.Columns.Add("PointsBid", typeof(double));
            points.Columns.Add("PointsMid", typeof(double));
            points.Columns.Add("PointsAsk", typeof(double));
            points.Columns.Add("OutrightBid", typeof(double));
            points.Columns.Add("OutrightMid", typeof(double));
            points.Columns.Add("OutrightAsk", typeof(double));

            foreach (System.Data.DataRow r in bulk.Rows)
            {
                var d = System.Convert.ToDateTime(r["DATE"], System.Globalization.CultureInfo.InvariantCulture).Date;

                string tn = "";
                if (bulk.Columns.Contains("TENOR"))
                    tn = System.Convert.ToString(r["TENOR"] ?? "");
                else if (bulk.Columns.Contains("SECURITY DESCRIPTION"))
                    tn = InferTenorFromSecDesc(System.Convert.ToString(r["SECURITY DESCRIPTION"]), leg.Pair6);

                double pMid = FirstNumeric(r, "MID POINTS", "MID_POINTS", "POINTS MID", "POINTS_MID", "POINTS", "FWD_POINTS", "FWD PTS");
                double pBid = FirstNumeric(r, "BID POINTS", "BID_POINTS");
                double pAsk = FirstNumeric(r, "ASK POINTS", "ASK_POINTS");

                double oMid = FirstNumeric(r, "OUTRIGHT MID", "OUTRIGHT_MID", "FWD OUTRIGHT", "OUTRIGHT", "PX_MID", "MID");
                double oBid = FirstNumeric(r, "BID OUTRIGHT", "BID_OUTRIGHT", "BID PX", "BID");
                double oAsk = FirstNumeric(r, "ASK OUTRIGHT", "ASK_OUTRIGHT", "ASK PX", "ASK");

                var row = points.NewRow();
                row["Date"] = d;
                row["Tenor"] = tn;
                row["PointsBid"] = pBid;
                row["PointsMid"] = pMid;
                row["PointsAsk"] = pAsk;
                row["OutrightBid"] = oBid;
                row["OutrightMid"] = oMid;
                row["OutrightAsk"] = oAsk;
                points.Rows.Add(row);
            }

            leg.Points = points;
            leg.ValDate = System.DateTime.Today;
            return leg;
        }

        #endregion

    }
}
