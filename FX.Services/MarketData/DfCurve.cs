using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Enkel diskonteringskurva byggd från (Date, DF)-noder.
    /// Ankarnod T=0 (DF=1) + log-DF-linjär interpolering.
    /// C# 7.3-kompatibel.
    /// </summary>
    public sealed class DfCurve
    {
        public DateTime ValDate { get; }
        private readonly double[] _t;
        private readonly double[] _logDf; // inkluderar T=0

        private DfCurve(DateTime valDate, List<Tuple<DateTime, double>> nodes)
        {
            ValDate = valDate.Date;

            // sortera & dedupliera på datum
            var uniq = nodes
                .Where(n => n != null && n.Item2 > 0.0 && n.Item2 <= 1.0)
                .GroupBy(n => n.Item1.Date)
                .Select(g => Tuple.Create(g.Key, g.Last().Item2))
                .OrderBy(u => u.Item1)   // <— byt namn i lambdan
                .ToList();

            var times = new List<double>(uniq.Count + 1);
            var logDfs = new List<double>(uniq.Count + 1);

            // ankare
            times.Add(0.0);
            logDfs.Add(0.0);

            foreach (var n in uniq)
            {
                var T = Math.Max(0.0, (n.Item1.Date - ValDate).TotalDays / 360.0);
                if (T < 1e-12) continue; // hoppa dubletter nära T=0
                var df = Math.Max(1e-12, Math.Min(1.0, n.Item2));
                times.Add(T);
                logDfs.Add(Math.Log(df));
            }

            if (times.Count < 2) throw new Exception("Need at least one pillar beyond T=0.");

            _t = times.ToArray();
            _logDf = logDfs.ToArray();
        }

        public static DfCurve FromDatesAndDfs(DateTime valDate, IEnumerable<Tuple<DateTime, double>> nodes)
        {
            return new DfCurve(valDate, new List<Tuple<DateTime, double>>(nodes));
        }

        public double DiscountFactor(DateTime d)
        {
            var T = Math.Max(0.0, (d.Date - ValDate).TotalDays / 360.0);
            if (T <= 0.0) return 1.0;

            // mellan T=0 och första punkten
            if (T <= _t[1])
            {
                double w = T / _t[1];
                return Math.Exp(w * _logDf[1]);
            }

            int last = _t.Length - 1;
            if (T >= _t[last]) return Math.Exp(_logDf[last]);

            int i = Array.BinarySearch(_t, T);
            if (i >= 0) return Math.Exp(_logDf[i]);

            i = ~i - 1;
            double t0 = _t[i], t1 = _t[i + 1];
            double y0 = _logDf[i], y1 = _logDf[i + 1];
            double w2 = (T - t0) / (t1 - t0);
            return Math.Exp(y0 + w2 * (y1 - y0));
        }

        public double RdCont(DateTime d)
        {
            double T = Math.Max(0.0, (d.Date - ValDate).TotalDays / 360.0);
            if (T <= 1e-8) return 0.0;
            double df = Math.Max(1e-12, DiscountFactor(d));
            return -Math.Log(df) / T;
        }
    }
}
