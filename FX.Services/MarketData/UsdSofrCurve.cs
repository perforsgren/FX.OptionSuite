using System;
using System.Data;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using Bloomberglp.Blpapi;

namespace FX.Services.MarketData
{
    /// <summary>
    /// USD SOFR OIS-kurva via Bloomberg (YCSW0490 Index).
    /// - Hämtar bulk-fält (CURVE_TENOR_RATES eller PAR_CURVE och ev. CURVE_TENOR_DATES).
    /// - Bygger DF-pelare och log-DF-interpolerar (ankarnod T=0).
    /// - Robust parsing av DF/ränta (MID YIELD, BID/ASK, %, bp, etc.).
    /// - Valfri kortände via SOFR O/N (kan användas senare).
    /// - C# 7.3-kompatibel.
    /// </summary>
    public sealed class UsdSofrCurve
    {
        // Publika data
        public DateTime ValDate { get; private set; }
        public DataTable Pillars { get; private set; }  // Kolumner: Date, Tenor, ZeroMid (cont), DF

        // Interpoleringsknutar (log-DF, piecewise-linear)
        private struct Knot { public double T; public double LogDf; }
        private Knot[] _knots;
        private double[] _times;

        // ---- Bloomberg-fält/konstanter ----
        private const string CurveTicker = "YCSW0490 Index";      // USD OIS (SOFR) ICVS
        private const string FldTenorRates = "CURVE_TENOR_RATES";   // bulk tabell (kan saknas)
        private const string FldTenorDates = "CURVE_TENOR_DATES";   // bulk tabell (kan saknas)
        private const string FldParCurve = "PAR_CURVE";           // bulk tabell (fallback)
        private const double MinDf = 1e-12;

        /// <summary>
        /// Laddar SOFR-kurvan via BLP. Kräver en startad session.
        /// Hämtar bulk-fälten för räntor/datum, bygger DF-tabell och log-DF knutar.
        /// </summary>
        public void LoadFromBloomberg(Session session, Func<DateTime, bool> isBusinessDay = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!session.OpenService("//blp/refdata"))
                throw new InvalidOperationException("Could not open //blp/refdata.");

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");

            req.Append("securities", CurveTicker);

            // Be om båda; acceptera den som finns
            req.Append("fields", FldTenorRates);
            req.Append("fields", FldTenorDates);
            req.Append("fields", FldParCurve);

            // CURVE_DATE override (YYYYMMDD) → vi använder Today
            var overrides = req.GetElement("overrides");
            var ov = overrides.AppendElement();
            ov.SetElement("fieldId", "CURVE_DATE");
            ov.SetElement("value", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

            var reqId = new CorrelationID(42);
            session.SendRequest(req, reqId);

            DataTable ratesTable = null, datesTable = null;
            bool done = false;

            while (!done)
            {
                var ev = session.NextEvent();
                foreach (Message msg in ev)
                {
                    if (msg.MessageType.ToString() == "ReferenceDataResponse")
                    {
                        var secData = msg.GetElement("securityData");
                        if (secData.NumValues == 0)
                            throw new Exception("No securityData in response.");

                        var first = secData.GetValueAsElement(0);
                        var fData = first.GetElement("fieldData");

                        if (fData.HasElement(FldTenorRates))
                            ratesTable = BulkToTable(fData.GetElement(FldTenorRates));
                        else if (fData.HasElement(FldParCurve))
                            ratesTable = BulkToTable(fData.GetElement(FldParCurve));

                        if (fData.HasElement(FldTenorDates))
                            datesTable = BulkToTable(fData.GetElement(FldTenorDates));
                    }
                }
                if (ev.Type == Event.EventType.RESPONSE) done = true;
            }

            if (ratesTable == null && datesTable == null)
                throw new Exception("Missing bulk fields (rates/dates).");

            // Fallback-datum: försök hitta datum i rates; annars TENOR→datum (Following m/kalender om given)
            if (datesTable == null && ratesTable != null)
            {
                var vdGuess = DateTime.Today;
                datesTable = BuildDatesTableFallback(ratesTable, vdGuess, isBusinessDay);
            }

            // Merge som bevarar ALLA rate-kolumner + lägger till DATE
            var merged = MergeCurveTablesPreserveRates(ratesTable, datesTable);

            // Bygg pelare
            Pillars = new DataTable("USD_SOFR_OIS");
            Pillars.Locale = CultureInfo.InvariantCulture;
            Pillars.Columns.Add("Date", typeof(DateTime));
            Pillars.Columns.Add("Tenor", typeof(string));
            Pillars.Columns.Add("ZeroMid", typeof(double)); // kontinuerlig zero
            Pillars.Columns.Add("DF", typeof(double));

            // Viktigt: sätt ValDate = samma som i override (Today)
            ValDate = DateTime.Today;

            foreach (DataRow r in merged.Rows)
            {
                var d = r.Field<DateTime>("DATE").Date;
                var tn = (r.Table.Columns.Contains("TENOR") ? (r["TENOR"] as string) : "") ?? "";

                double T = YearFrac_Act360(ValDate, d);
                if (T < 0) continue;

                double df, zero;
                if (!TryReadDfOrZero(r, T, out df, out zero))
                {
                    var cols = string.Join(", ", r.Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                    throw new Exception("No numeric rate/DF column found in curve bulk. Columns: " + cols);
                }

                var row = Pillars.NewRow();
                row["Date"] = d;
                row["Tenor"] = tn;
                row["ZeroMid"] = zero;
                row["DF"] = df;
                Pillars.Rows.Add(row);
            }

            BuildKnots(); // bygger in ankarnod T=0
        }

        /// <summary>
        /// Valfri: Fyll kortänden med SOFR O/N (PX_LAST) dag-för-dag till första kurvpunkten.
        /// </summary>
        public void AugmentShortEndWithSofrOn(Session session, Func<DateTime, bool> isBusinessDay)
        {
            if (Pillars == null || Pillars.Rows.Count == 0)
                throw new InvalidOperationException("LoadFromBloomberg() först.");

            if (isBusinessDay == null)
                isBusinessDay = (DateTime d) =>
                    d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;

            double onRate = FetchSofrOnRate(session); // t.ex. 0.0543

            var firstCurveDate = Pillars.AsEnumerable().Select(r => r.Field<DateTime>("Date")).Min().Date;
            var nextBiz = NextBusinessDay(ValDate, isBusinessDay);
            if (firstCurveDate <= nextBiz) return; // redan kort punkt

            var rows = new List<DataRow>();
            DateTime prev = ValDate;
            DateTime cur = nextBiz;
            int step = 0;
            double dfAcc = 1.0;

            while (cur < firstCurveDate)
            {
                double days = (cur - prev).TotalDays;      // ack helg/helgdagar
                double yf = Math.Max(0.0, days / 360.0);   // ACT/360
                dfAcc *= Math.Exp(-onRate * yf);

                step++;
                string label = (step == 1) ? "ON" : (step - 1).ToString(CultureInfo.InvariantCulture) + "D";

                var row = Pillars.NewRow();
                row["Date"] = cur;
                row["Tenor"] = label;
                row["ZeroMid"] = onRate;
                row["DF"] = Math.Max(MinDf, Math.Min(1.0, dfAcc));
                rows.Add(row);

                prev = cur;
                cur = NextBusinessDay(cur, isBusinessDay);
            }

            foreach (var r in rows) Pillars.Rows.Add(r);
            BuildKnots();
        }

        // ----- Publika convenience -----

        public double DiscountFactor(DateTime d)
        {
            if (_knots == null || _knots.Length == 0) throw new InvalidOperationException("Curve not loaded.");

            double T = YearFrac_Act360(ValDate, d);
            if (T <= 0.0) return 1.0; // vid/före ValDate

            int last = _knots.Length - 1;

            // Mellan T=0 (ankarnod) och första riktiga noden → loglin mellan (0,0) och första
            if (T <= _knots[1].T)
            {
                double t1 = _knots[1].T;
                double y1 = _knots[1].LogDf;
                double w = T / t1;
                return Math.Exp(w * y1);
            }

            if (T >= _knots[last].T) return Math.Exp(_knots[last].LogDf);

            int i = Array.BinarySearch(_times, T);
            if (i >= 0) return Math.Exp(_knots[i].LogDf);

            i = ~i - 1; // segment i..i+1
            double t0 = _knots[i].T, t1b = _knots[i + 1].T;
            double y0 = _knots[i].LogDf, y1b = _knots[i + 1].LogDf;
            double w2 = (T - t0) / (t1b - t0);
            return Math.Exp(y0 + w2 * (y1b - y0));
        }

        public double RdCont(DateTime d)
        {
            double T = YearFrac_Act360(ValDate, d);
            if (T <= 1e-8) return 0.0;
            double df = Math.Max(MinDf, DiscountFactor(d));
            return -Math.Log(df) / T; // kontinuerlig
        }

        // ----- Privata helpers -----

        private void BuildKnots()
        {
            var rows = Pillars.AsEnumerable()
                              .OrderBy(r => r.Field<DateTime>("Date"))
                              .ToArray();

            var list = new List<Knot>(rows.Length + 1);

            // Ankarnod i T=0 (DF=1)
            list.Add(new Knot { T = 0.0, LogDf = 0.0 });

            for (int i = 0; i < rows.Length; i++)
            {
                var d = rows[i].Field<DateTime>("Date");
                var T = YearFrac_Act360(ValDate, d);
                if (T < 1e-10) continue; // undvik dublett med T=0
                var df = Math.Max(MinDf, rows[i].Field<double>("DF"));
                list.Add(new Knot { T = T, LogDf = Math.Log(df) });
            }

            list.Sort((a, b) => a.T.CompareTo(b.T));
            _knots = list.ToArray();
            _times = new double[_knots.Length];
            for (int i = 0; i < _knots.Length; i++) _times[i] = _knots[i].T;

            if (_knots.Length < 2)
                throw new Exception("Insufficient curve knots after build (need at least T=0 and one pillar).");
        }

        private static DataTable BulkToTable(Element bulk)
        {
            var dt = new DataTable();
            if (bulk.NumValues == 0) return dt;

            // Schema från första raden
            var first = bulk.GetValueAsElement(0);
            for (int k = 0; k < first.NumElements; k++)
            {
                var el = first.GetElement(k);
                string name = el.Name.ToString().ToUpperInvariant();
                dt.Columns.Add(new DataColumn(name, ToNetType(el.Datatype)));
            }

            // Rader
            for (int i = 0; i < bulk.NumValues; i++)
            {
                var e = bulk.GetValueAsElement(i);
                var row = dt.NewRow();
                for (int k = 0; k < e.NumElements; k++)
                {
                    var el = e.GetElement(k);
                    string name = el.Name.ToString().ToUpperInvariant();

                    object val = null;
                    var dtype = el.Datatype;
                    if (dtype == Schema.Datatype.STRING)
                        val = el.GetValueAsString();
                    else if (dtype == Schema.Datatype.FLOAT64)
                        val = el.GetValueAsFloat64();
                    else if (dtype == Schema.Datatype.INT32)
                        val = el.GetValueAsInt32();
                    else if (dtype == Schema.Datatype.DATE || dtype == Schema.Datatype.DATETIME)
                        val = el.GetValueAsDatetime().ToSystemDateTime().Date;
                    else
                        val = el.ToString();

                    row[name] = val ?? DBNull.Value;
                }
                dt.Rows.Add(row);
            }

            // Normalisera kolumnnamn
            if (!dt.Columns.Contains("DATE"))
            {
                foreach (var c in new[] { "VALUE_DATE", "MATURITY", "DT", "DATE" })
                {
                    if (dt.Columns.Contains(c)) { dt.Columns[c].ColumnName = "DATE"; break; }
                }
            }
            if (!dt.Columns.Contains("TENOR") && dt.Columns.Contains("CURVE_TENOR"))
                dt.Columns["CURVE_TENOR"].ColumnName = "TENOR";

            return dt;
        }

        /// <summary>
        /// Merge som bevarar ALLA rate-kolumner från rates-tabellen och fyller DATE från dates-tabellen.
        /// </summary>
        private static DataTable MergeCurveTablesPreserveRates(DataTable rates, DataTable dates)
        {
            if (rates == null) throw new ArgumentNullException(nameof(rates));

            // 1) Klona hela schemat från rates → alla kolumner följer med
            var merged = rates.Clone();

            // 2) Se till att vi har en DATE-kolumn att fylla
            if (!merged.Columns.Contains("DATE"))
                merged.Columns.Add("DATE", typeof(DateTime));

            // 3) Uppslag TENOR->DATE eller Index->DATE
            Dictionary<string, DateTime> byTenor = null;
            Dictionary<int, DateTime> byIndex = null;

            if (dates != null)
            {
                if (dates.Columns.Contains("TENOR"))
                {
                    byTenor = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    foreach (DataRow d in dates.Rows)
                    {
                        var tn = Convert.ToString(d["TENOR"] ?? "").Trim();
                        if (string.IsNullOrEmpty(tn)) continue;
                        var dd = (d.Table.Columns.Contains("DATE"))
                            ? Convert.ToDateTime(d["DATE"], CultureInfo.InvariantCulture).Date
                            : DateTime.MinValue;
                        if (dd != DateTime.MinValue && !byTenor.ContainsKey(tn))
                            byTenor[tn] = dd;
                    }
                }
                else if (dates.Columns.Contains("DATE"))
                {
                    byIndex = new Dictionary<int, DateTime>();
                    for (int i = 0; i < dates.Rows.Count; i++)
                        byIndex[i] = Convert.ToDateTime(dates.Rows[i]["DATE"], CultureInfo.InvariantCulture).Date;
                }
            }

            // 4) Kopiera varje rad från rates → merged, sätt DATE
            for (int i = 0; i < rates.Rows.Count; i++)
            {
                var r = rates.Rows[i];
                var newRow = merged.NewRow();

                foreach (DataColumn c in rates.Columns)
                    newRow[c.ColumnName] = r[c.ColumnName];

                DateTime date = DateTime.MinValue;
                if (byTenor != null && rates.Columns.Contains("TENOR"))
                {
                    var tn = Convert.ToString(r["TENOR"] ?? "").Trim();
                    if (!string.IsNullOrEmpty(tn) && byTenor.TryGetValue(tn, out var d)) date = d;
                }
                else if (byIndex != null && byIndex.TryGetValue(i, out var di))
                {
                    date = di;
                }

                if (date != DateTime.MinValue)
                    newRow["DATE"] = date;

                merged.Rows.Add(newRow);
            }

            return merged;
        }

        /// <summary>
        /// Fallback: bygg en 'datesTable' utifrån rates (prova befintliga datumkolumner, annars TENOR→datum).
        /// Ankrar tenor-datum till SPOT (ValDate T+2 bankdagar). M/Y följer End-End/EOM med Modified Following.
        /// </summary>
        private static DataTable BuildDatesTableFallback(DataTable rates, DateTime valDate, Func<DateTime, bool> isBiz)
        {
            var dt = new DataTable(); dt.Locale = CultureInfo.InvariantCulture;
            dt.Columns.Add("TENOR", typeof(string));
            dt.Columns.Add("DATE", typeof(DateTime));

            // 1) Prova hitta datum direkt i rates
            string[] dateCandidates = { "DATE", "MATURITY", "VALUE_DATE", "TENOR_EFFECTIVE_DATE", "EFFECTIVE_DATE", "END_DATE" };
            bool ratesHasAnyDateCol = dateCandidates.Any(c => rates.Columns.Contains(c));

            if (ratesHasAnyDateCol)
            {
                for (int i = 0; i < rates.Rows.Count; i++)
                {
                    var rr = rates.Rows[i];
                    DateTime? d = null;
                    foreach (var c in dateCandidates)
                    {
                        if (!rates.Columns.Contains(c)) continue;
                        var o = rr[c];
                        if (o == null || o == DBNull.Value) continue;
                        try
                        {
                            d = Convert.ToDateTime(o, CultureInfo.InvariantCulture).Date; break;
                        }
                        catch { /* ignore parse fail */ }
                    }
                    if (d.HasValue)
                    {
                        var row = dt.NewRow();
                        row["TENOR"] = rates.Columns.Contains("TENOR") ? (rr["TENOR"] ?? "").ToString() : "";
                        row["DATE"] = d.Value;
                        dt.Rows.Add(row);
                    }
                }
                if (dt.Rows.Count > 0) return dt;
            }

            if (isBiz == null) isBiz = d => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;

            // === Viktigt: ankra TENOR till SPOT/EFFECTIVE = ValDate T+2 BANKDAGAR ===
            var spot0 = AddBizDays(valDate.Date, 2, isBiz);
            bool endToEndAnchor = IsCalendarEom(spot0); // EOM-ankare om start är EOM

            for (int i = 0; i < rates.Rows.Count; i++)
            {
                string tnRaw = rates.Columns.Contains("TENOR") ? Convert.ToString(rates.Rows[i]["TENOR"] ?? "") : "";
                string tn = (tnRaw ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(tn)) continue;

                DateTime d;
                if (tn.EndsWith("D") && int.TryParse(tn.Substring(0, tn.Length - 1), out var nd))
                    d = RollModifiedFollowing(AddBizDays(spot0, nd, isBiz), isBiz);
                else if (tn.EndsWith("W") && int.TryParse(tn.Substring(0, tn.Length - 1), out var nw))
                    d = RollModifiedFollowing(spot0.AddDays(7 * nw), isBiz);
                else if (tn.EndsWith("M") && int.TryParse(tn.Substring(0, tn.Length - 1), out var nm))
                {
                    var raw = spot0.AddMonths(nm);
                    d = endToEndAnchor ? RollModifiedFollowing(LastCalendarDayOfMonth(raw), isBiz)
                                       : RollModifiedFollowing(raw, isBiz);
                }
                else if (tn.EndsWith("Y") && int.TryParse(tn.Substring(0, tn.Length - 1), out var ny))
                {
                    var raw = spot0.AddYears(ny);
                    d = endToEndAnchor ? RollModifiedFollowing(LastCalendarDayOfMonth(raw), isBiz)
                                       : RollModifiedFollowing(raw, isBiz);
                }
                else
                {
                    d = RollModifiedFollowing(spot0, isBiz);
                }

                var row = dt.NewRow();
                row["TENOR"] = tnRaw;
                row["DATE"] = d.Date;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static Type ToNetType(Schema.Datatype t)
        {
            switch (t)
            {
                case Schema.Datatype.STRING: return typeof(string);
                case Schema.Datatype.FLOAT64: return typeof(double);
                case Schema.Datatype.INT32: return typeof(int);
                case Schema.Datatype.DATE: return typeof(DateTime);
                case Schema.Datatype.DATETIME: return typeof(DateTime);
                default: return typeof(string);
            }
        }

        // Försöker läsa antingen DF eller Zero-rate ur en rad.
        // df ∈ (0,1], zero = kontinuerlig -ln(df)/T. Returnerar true om något hittades.
        private static bool TryReadDfOrZero(DataRow r, double T, out double df, out double zero)
        {
            df = double.NaN; zero = double.NaN;

            // 1) DF direkt?
            if (TryGetNumeric(r, out df, "DF", "DISCOUNT_FACTOR", "DISCOUNT FACTOR"))
            {
                df = ClampDf(df);
                zero = (T > 1e-8) ? (-Math.Log(df) / T) : 0.0;
                return true;
            }

            // 2) Yields / rates
            double rate;
            if (TryGetNumeric(r, out rate,
                "ZERO", "ZERO_RATE", "ZERO YIELD", "ZERO_YIELD", "ZERO MID", "ZERO_MID",
                "RATE", "YIELD", "PX_MID", "MID",
                "MID YIELD", "BID YIELD", "ASK YIELD",
                "MID_YIELD", "BID_YIELD", "ASK_YIELD",
                "ASK", "BID"))
            {
                if (rate > 2.0) rate *= 0.01;   // % → decimal
                if (rate > 20.0) rate *= 0.0001; // bp → decimal

                zero = Math.Max(0.0, rate);
                df = (T > 1e-8) ? Math.Exp(-zero * T) : 1.0;
                df = ClampDf(df);
                return true;
            }

            return false;
        }

        private static double ClampDf(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return 1.0;
            if (x < MinDf) return MinDf;
            if (x > 1.0) return 1.0;
            return x;
        }

        // Läs numeriskt från uppsättning kolumnnamn (tabellen har redan UPPERCASE kolumner).
        // Hanterar strängar med %, "bp", kommatecken etc.
        private static bool TryGetNumeric(DataRow r, out double v, params string[] candidates)
        {
            v = double.NaN;
            if (r == null || r.Table == null) return false;

            foreach (var raw in candidates)
            {
                var name = (raw ?? "").ToUpperInvariant();
                if (!r.Table.Columns.Contains(name)) continue;
                var o = r[name];
                if (o == null || o == DBNull.Value) continue;

                var d = o as double?;
                if (d.HasValue && !double.IsNaN(d.Value) && !double.IsInfinity(d.Value))
                {
                    v = d.Value; return true;
                }

                var s = Convert.ToString(o, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(s)) continue;

                s = s.Trim()
                     .Replace(",", ".")
                     .Replace("%", "")
                     .Replace("\u00A0", " "); // NBSP

                bool isBp = false;
                if (s.EndsWith("bp", StringComparison.OrdinalIgnoreCase))
                {
                    isBp = true;
                    s = s.Substring(0, s.Length - 2).Trim();
                }

                double tmp;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out tmp))
                {
                    if (isBp) tmp *= 1e-4;
                    v = tmp;
                    return true;
                }
            }
            return false;
        }

        private static double YearFrac_Act360(DateTime a, DateTime b)
        {
            return Math.Max(0.0, (b.Date - a.Date).TotalDays / 360.0);
        }

        // --- EOM/Modified-Following helpers ---
        private static bool IsCalendarEom(DateTime d) => d.AddDays(1).Month != d.Month;

        private static DateTime LastCalendarDayOfMonth(DateTime d) =>
            new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

        private static DateTime NextBusinessDay(DateTime from, Func<DateTime, bool> isBiz)
        {
            var d = from.AddDays(1).Date;
            while (!isBiz(d)) d = d.AddDays(1);
            return d;
        }

        private static DateTime AddBizDays(DateTime d, int n, Func<DateTime, bool> isBiz)
        {
            var x = d.Date;
            for (int i = 0; i < n; i++)
            {
                x = x.AddDays(1);
                while (!isBiz(x)) x = x.AddDays(1);
            }
            return x;
        }

        // Modified Following: rulla framåt till närmaste bizdag, men om månaden byts → rulla bakåt
        private static DateTime RollModifiedFollowing(DateTime d, Func<DateTime, bool> isBiz)
        {
            var start = d.Date;
            if (isBiz(start)) return start;

            var fwd = start;
            while (!isBiz(fwd)) fwd = fwd.AddDays(1);

            if (fwd.Month != start.Month)
            {
                var back = start;
                while (!isBiz(back)) back = back.AddDays(-1);
                return back;
            }
            return fwd;
        }

        /// <summary>Hämtar dagens SOFR O/N fixing (PX_LAST) som decimal (t.ex. 0.0543 = 5.43%).</summary>
        private static double FetchSofrOnRate(Session session)
        {
            if (!session.OpenService("//blp/refdata"))
                throw new InvalidOperationException("Could not open //blp/refdata.");

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");
            req.Append("securities", "SOFRRATE Index");
            req.Append("fields", "PX_LAST");

            var cid = new CorrelationID(77);
            session.SendRequest(req, cid);

            double last = double.NaN;
            bool done = false;
            while (!done)
            {
                var ev = session.NextEvent();
                foreach (Message msg in ev)
                {
                    if (msg.MessageType.ToString() == "ReferenceDataResponse")
                    {
                        var secData = msg.GetElement("securityData");
                        if (secData.NumValues > 0)
                        {
                            var first = secData.GetValueAsElement(0);
                            var fData = first.GetElement("fieldData");
                            if (fData.HasElement("PX_LAST"))
                                last = fData.GetElementAsFloat64("PX_LAST");
                        }
                    }
                }
                if (ev.Type == Event.EventType.RESPONSE) done = true;
            }
            if (double.IsNaN(last)) throw new Exception("SOFR O/N PX_LAST not available.");
            return last;
        }
    }
}

