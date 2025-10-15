using System;
using FX.Core;
using FX.Core.Interfaces;

namespace FX.Services
{
    /// MVP: returnerar en platt vol så pipeline kan köras.
    public sealed class FlatVolInterpolator : IVolInterpolator
    {
        private readonly double _flat;
        public FlatVolInterpolator() : this(0.10) { }   // 10%
        public FlatVolInterpolator(double flat) { _flat = flat; }

        public double Interpolate(VolSurface surface, DateTime expiry, double strike, bool strikeIsDelta)
        {
            return _flat;
        }
    }
}
