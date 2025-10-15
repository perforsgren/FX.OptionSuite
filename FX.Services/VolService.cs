using System;
using FX.Core;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class VolService : IVolService
    {
        private readonly IVolInterpolator _interp;

        public VolService(IVolInterpolator interp)
        {
            _interp = interp;
        }

        public double GetVol(string pair6, DateTime expiry, double strike, bool strikeIsDelta)
        {
            var surface = new VolSurface(); // TODO: hämta från cache
            return _interp.Interpolate(surface, expiry, strike, strikeIsDelta);
        }
    }
}
