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


        /// <summary>
        /// Laddar ett FX-ben från Bloomberg.
        /// - Begär FWD_CURVE med override FWD_CURVE_QUOTE_FORMAT=POINTS
        /// - Läser swap-points och räknar fram outrights = SpotMid + Points*pip
        /// - Sätter PairTicker/Pair6/SpotMid/Table på denna instans
        /// </summary>
        public void LoadFromBloomberg(Bloomberglp.Blpapi.Session session, string pairTicker, Func<string, DateTime> resolveSpotDate)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(pairTicker)) throw new ArgumentNullException(nameof(pairTicker));

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");

            // Security
            req.GetElement("securities").AppendValue(pairTicker);

            // Fields: spot + bulk forward curve
            var flds = req.GetElement("fields");
            flds.AppendValue("PX_LAST");
            flds.AppendValue("PX_MID");
            flds.AppendValue("PX_BID");
            flds.AppendValue("PX_ASK");
            flds.AppendValue("FWD_CURVE");

            // Viktigt: begär POINTS i FWD_CURVE
            var ovs = req.GetElement("overrides");
            var ov = ovs.AppendElement();
            ov.SetElement("fieldId", "FWD_CURVE_QUOTE_FORMAT");
            ov.SetElement("value", "POINTS");

            session.SendRequest(req, null);

            FxSwapPoints built = null;

            while (true)
            {
                var ev = session.NextEvent();
                foreach (var msg in ev)
                {
                    if (!msg.MessageType.Equals(new Name("ReferenceDataResponse"))) continue;

                    var secArr = msg.GetElement("securityData");
                    for (int i = 0; i < secArr.NumValues; i++)
                    {
                        var sec = secArr.GetValueAsElement(i);
                        var secName = sec.GetElementAsString("security");
                        if (!secName.Equals(pairTicker, StringComparison.OrdinalIgnoreCase)) continue;

                        built = BuildFromFieldDataAtomic(secName, sec, resolveSpotDate);
                    }
                }
                if (ev.Type == Event.EventType.RESPONSE) break;
            }

            if (built == null)
                throw new InvalidOperationException("FxSwapPoints: inget data byggt för " + pairTicker);

            // Kopiera in i denna instans
            this.PairTicker = built.PairTicker;
            this.Pair6 = built.Pair6;
            this.SpotMid = built.SpotMid;
            this.SpotDate = built.SpotDate;
            this.ValDate = built.ValDate;
            this.Points = built.Points;
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
                             .OrderBy(r => r.Field<DateTime>("DATE").Date)
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
            DateTime dFirst = rows[0].Field<DateTime>("DATE").Date;
            DateTime dLast = rows[rows.Length - 1].Field<DateTime>("DATE").Date;

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
            var dateArr = rows.Select(r => r.Field<DateTime>("DATE").Date).ToArray();
            int hi = Array.BinarySearch(dateArr, d);
            if (hi >= 0)
            {
                var r = rows[hi];
                return hasOut ? r.Field<double>("OutrightMid")
                              : S + r.Field<double>("PointsMid") * pip;
            }

            hi = ~hi;
            int lo = hi - 1;

            var dLo = rows[lo].Field<DateTime>("DATE").Date;
            var dHi = rows[hi].Field<DateTime>("DATE").Date;

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
                             .OrderBy(r => r.Field<DateTime>("DATE").Date)
                             .ToArray();

            bool hasOutright = rows.Any(r => ToDouble(r["OutrightMid"]) != 0.0);
            double pip = p6.EndsWith("JPY", StringComparison.Ordinal) ? 0.01 : 0.0001;

            DateTime spot0 = (this.SpotDate != default(DateTime))
                ? this.SpotDate.Date
                : this.ValDate.Date.AddDays(2);

            DateTime d = settlement.Date;
            DateTime dFirst = rows[0].Field<DateTime>("DATE").Date;
            DateTime dLast = rows[rows.Length - 1].Field<DateTime>("DATE").Date;

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

            var dateArr = rows.Select(r => r.Field<DateTime>("DATE").Date).ToArray();
            int hi = Array.BinarySearch(dateArr, d);
            if (hi >= 0)
            {
                var r = rows[hi];
                return hasOutright ? r.Field<double>("OutrightMid")
                                   : Sspot + r.Field<double>("PointsMid") * pip;
            }

            hi = ~hi;
            int lo = hi - 1;

            DateTime dLo = rows[lo].Field<DateTime>("DATE").Date;
            DateTime dHi = rows[hi].Field<DateTime>("DATE").Date;

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

        /// <summary>
        /// Beräknar forward på <paramref name="settlement"/> för samtliga tre sidor
        /// (Bid/Mid/Ask) via linjär interpolation på Outright-kolumnerna i <see cref="Points"/>.
        /// Om Outright-kolumn saknas eller är noll för de nödvändiga raderna faller metoden
        /// mjukt tillbaka till Points (BID/MID/ASK) + pip baserat på <see cref="SpotMid"/>.
        /// Pip-regel: JPY-quote => 0.01, annars 0.0001.
        /// </summary>
        /// <param name="settlement">Leveransdatum.</param>
        /// <param name="pair6">Används enbart för pip-konvention (JPY eller ej).</param>
        /// <param name="spotOverride">Valfritt spot för fallback från points; annars används <see cref="SpotMid"/>.</param>
        /// <returns>(Bid, Mid, Ask) forwards i samma riktning som <see cref="Pair6"/>.</returns>
        public (double Bid, double Mid, double Ask) ForwardAtAllSides(DateTime settlement, string pair6, double? spotOverride = null)
        {
            if (Points == null || Points.Rows.Count == 0)
                throw new InvalidOperationException("FxSwapPoints: saknar Points-tabell. Ladda från feed först.");

            // Sortera efter DATE (vi normaliserade kolumnnamnet till versaler i buildern)
            var rows = Points.AsEnumerable()
                             .OrderBy(r => r.Field<DateTime>("DATE").Date)
                             .ToArray();

            string p6 = (pair6 ?? Pair6 ?? "").Trim().ToUpperInvariant().Replace("/", "");
            bool isJpyQuote = p6.Length >= 6 && p6.EndsWith("JPY", StringComparison.Ordinal);
            double pip = isJpyQuote ? 0.01 : 0.0001;

            // Plocka Outright-sidor, med fallback till points + S om Outright saknas
            bool hasOutCols = Points.Columns.Contains("OutrightBid")
                           && Points.Columns.Contains("OutrightMid")
                           && Points.Columns.Contains("OutrightAsk");

            bool hasPtsCols = Points.Columns.Contains("BID")
                           && Points.Columns.Contains("MID")
                           && Points.Columns.Contains("ASK");

            double S = spotOverride ?? this.SpotMid;

            Func<int, double> OB = i =>
            {
                double v = hasOutCols ? rows[i].Field<double>("OutrightBid") : 0.0;
                if (v != 0.0) return v;
                if (hasPtsCols) { var p = rows[i].Field<double>("BID"); if (p != 0.0) return S + p * pip; }
                return 0.0;
            };
            Func<int, double> OM = i =>
            {
                double v = hasOutCols ? rows[i].Field<double>("OutrightMid") : 0.0;
                if (v != 0.0) return v;
                if (hasPtsCols) { var p = rows[i].Field<double>("MID"); if (p != 0.0) return S + p * pip; }
                return 0.0;
            };
            Func<int, double> OA = i =>
            {
                double v = hasOutCols ? rows[i].Field<double>("OutrightAsk") : 0.0;
                if (v != 0.0) return v;
                if (hasPtsCols) { var p = rows[i].Field<double>("ASK"); if (p != 0.0) return S + p * pip; }
                return 0.0;
            };

            DateTime d = settlement.Date;
            DateTime d0 = rows[0].Field<DateTime>("DATE").Date;
            DateTime d1 = rows[rows.Length - 1].Field<DateTime>("DATE").Date;

            // YearFrac (enkelt ACT/360 på kalenderdagar – samma som i ForwardAt)
            Func<DateTime, DateTime, double> YF = (a, b) => Math.Max(0.0, (b.Date - a.Date).TotalDays / 360.0);
            DateTime spot0 = (this.SpotDate != default(DateTime)) ? this.SpotDate.Date : this.ValDate.Date.AddDays(2);

            // a) Extrapolera åt vänster (före första pilen): skala mellan S och första outright
            if (d <= d0)
            {
                double t = YF(spot0, d);
                double t1 = Math.Max(1e-12, YF(spot0, d0));
                double b1 = OB(0), m1 = OM(0), a1 = OA(0);
                double fb = (b1 != 0.0) ? (S + (t / t1) * (b1 - S)) : 0.0;
                double fm = (m1 != 0.0) ? (S + (t / t1) * (m1 - S)) : 0.0;
                double fa = (a1 != 0.0) ? (S + (t / t1) * (a1 - S)) : 0.0;
                return (fb, fm, fa);
            }

            // b) Extrapolera åt höger: ta sista raden
            if (d >= d1)
                return (OB(rows.Length - 1), OM(rows.Length - 1), OA(rows.Length - 1));

            // c) Interpolera linjärt mellan närmaste pilar
            var dates = rows.Select(r => r.Field<DateTime>("DATE").Date).ToArray();
            int hi = Array.BinarySearch(dates, d);
            if (hi >= 0)
                return (OB(hi), OM(hi), OA(hi));

            hi = ~hi;
            int lo = hi - 1;

            double tLo = YF(spot0, dates[lo]);
            double tHi = YF(spot0, dates[hi]);
            double tX = YF(spot0, d);
            double w = (tHi - tLo) > 0 ? (tX - tLo) / (tHi - tLo) : 0.0;
            if (w < 0.0) w = 0.0; else if (w > 1.0) w = 1.0;

            double oB = OB(lo) + w * (OB(hi) - OB(lo));
            double oM = OM(lo) + w * (OM(hi) - OM(lo));
            double oA = OA(lo) + w * (OA(hi) - OA(lo));

            return (oB, oM, oA);
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
        /// ERSÄTT (NY version): Atomisk multiladdning av flera FX-ben.
        /// Alltid POINTS i bulk-tabellen. Returnerar färdigbyggda FxSwapPoints per ticker.
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, FxSwapPoints> LoadMultipleFromBloombergAtomic(
            Bloomberglp.Blpapi.Session session,
            System.Collections.Generic.IEnumerable<string> pairTickers,
            Func<string, DateTime> resolveSpotDate)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (pairTickers == null) throw new ArgumentNullException(nameof(pairTickers));

            var list = pairTickers
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new System.Collections.Generic.Dictionary<string, FxSwapPoints>(StringComparer.OrdinalIgnoreCase);
            if (list.Count == 0) return result;

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");

            var secs = req.GetElement("securities");
            foreach (var t in list) secs.AppendValue(t);

            var flds = req.GetElement("fields");
            flds.AppendValue("PX_LAST");
            flds.AppendValue("PX_MID");
            flds.AppendValue("PX_BID");
            flds.AppendValue("PX_ASK");
            flds.AppendValue("FWD_CURVE");

            var ovs = req.GetElement("overrides");
            var ov = ovs.AppendElement();
            ov.SetElement("fieldId", "FWD_CURVE_QUOTE_FORMAT");
            ov.SetElement("value", "POINTS");

            session.SendRequest(req, null);

            while (true)
            {
                var ev = session.NextEvent();
                foreach (var msg in ev)
                {
                    if (!msg.MessageType.Equals(new Name("ReferenceDataResponse"))) continue;

                    var secDataArr = msg.GetElement("securityData");
                    for (int i = 0; i < secDataArr.NumValues; i++)
                    {
                        var secData = secDataArr.GetValueAsElement(i);
                        var secName = secData.GetElementAsString("security");

                        var leg = BuildFromFieldDataAtomic(secName, secData, resolveSpotDate);
                        result[secName] = leg;
                    }
                }
                if (ev.Type == Event.EventType.RESPONSE) break;
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
        /// ERSÄTT (NY version): Bygger ett FxSwapPoints-objekt från Bloomberg fieldData.
        /// - Läser bulk-tabellen FWD_CURVE (med POINTS)
        /// - Räknar fram OutrightBid/Mid/Ask = SpotMid + Points * pip
        /// - Returnerar instans med PairTicker/Pair6/SpotMid/Table ifyllda
        /// </summary>
        private static FxSwapPoints BuildFromFieldDataAtomic(
            string pairTicker,
            Bloomberglp.Blpapi.Element secData,
            Func<string, DateTime> resolveSpotDate)
        {
            var leg = new FxSwapPoints
            {
                PairTicker = pairTicker,
                Pair6 = (pairTicker ?? "").Split(' ')[0].Replace("/", "").ToUpperInvariant(),
                ValDate = DateTime.Today
            };

            var fd = secData.HasElement("fieldData") ? secData.GetElement("fieldData") : null;
            if (fd == null)
                return leg;

            // Spot (mid med rimlig fallback)
            double spotMid = double.NaN;
            if (fd.HasElement("PX_MID")) spotMid = fd.GetElementAsFloat64("PX_MID");
            if (double.IsNaN(spotMid) && fd.HasElement("PX_LAST")) spotMid = fd.GetElementAsFloat64("PX_LAST");
            if (double.IsNaN(spotMid) && fd.HasElement("PX_BID") && fd.HasElement("PX_ASK"))
            {
                var b = fd.GetElementAsFloat64("PX_BID");
                var a = fd.GetElementAsFloat64("PX_ASK");
                if (b > 0 && a > 0) spotMid = 0.5 * (b + a);
            }
            leg.SpotMid = double.IsNaN(spotMid) ? 0.0 : spotMid;
            leg.SpotDate = resolveSpotDate != null ? resolveSpotDate(leg.Pair6) : DateTime.Today.AddDays(2);

            // Bulk → DataTable (alla kolumnnamn i VERSALER)
            var tbl = fd.HasElement("FWD_CURVE") ? BulkToTable(fd.GetElement("FWD_CURVE"))
                                                 : new System.Data.DataTable("FWD_CURVE");

            // Normalisera schema: säkerställ DATE och ev. TENOR
            NormalizeSchema(tbl); // gör om t.ex. "SETTLEMENT DATE" → "DATE"

            // Pip-storlek
            var quote = leg.Pair6.Length >= 6 ? leg.Pair6.Substring(3, 3) : "USD";
            double pip = string.Equals(quote, "JPY", StringComparison.OrdinalIgnoreCase) ? 0.01 : 0.0001;

            // Lägg till Outright-kolumner om de saknas (kamel-case är ok)
            if (!tbl.Columns.Contains("OutrightBid")) tbl.Columns.Add("OutrightBid", typeof(double));
            if (!tbl.Columns.Contains("OutrightMid")) tbl.Columns.Add("OutrightMid", typeof(double));
            if (!tbl.Columns.Contains("OutrightAsk")) tbl.Columns.Add("OutrightAsk", typeof(double));

            // Räkna fram outrights = SpotMid + (BID/MID/ASK)*pip
            foreach (System.Data.DataRow r in tbl.Rows)
            {
                // Kolumnerna i POINTS-uttrycket heter BID/MID/ASK (versal)
                double pBid = ToDouble(r.Table.Columns.Contains("BID") ? r["BID"] : null);
                double pMid = ToDouble(r.Table.Columns.Contains("MID") ? r["MID"] : null);
                double pAsk = ToDouble(r.Table.Columns.Contains("ASK") ? r["ASK"] : null);

                if (pBid != 0.0) r["OutrightBid"] = leg.SpotMid + pBid * pip;
                if (pMid != 0.0) r["OutrightMid"] = leg.SpotMid + pMid * pip;
                if (pAsk != 0.0) r["OutrightAsk"] = leg.SpotMid + pAsk * pip;
            }

            leg.Points = tbl;
            return leg;
        }




        #endregion

    }
}
