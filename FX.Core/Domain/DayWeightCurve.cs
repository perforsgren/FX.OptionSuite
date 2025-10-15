using System;
using System.Collections.Generic;

namespace FX.Core.Domain
{
    /// <summary>Dagvikter (0..1) för helger/events. Enkel lookup i steg 2.</summary>
    public sealed class DayWeightCurve
    {
        private readonly Dictionary<DateTime, double> _weights;

        public DayWeightCurve(Dictionary<DateTime, double> weights)
        {
            _weights = weights ?? new Dictionary<DateTime, double>();
        }

        public static DayWeightCurve Uniform() => new DayWeightCurve(new Dictionary<DateTime, double>());

        public double GetWeight(DateTime date)
        {
            double w;
            if (_weights.TryGetValue(date.Date, out w)) return w;
            return 1.0;
        }
    }
}
