using System;
using System.Collections.Generic;
using FX.Core;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class SpotSetDateService : ISpotSetDateService
    {
        private readonly ICalendarResolver _resolver;
        private readonly IBusinessCalendar _cal;
        private readonly IDictionary<string, int> _spotLagByPair; // T+1/T+2
        private readonly int _defaultLag = 2;

        public SpotSetDateService(ICalendarResolver resolver, IBusinessCalendar cal)
        {
            _resolver = resolver; _cal = cal;

            var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            map["USDCAD"] = 1;
            map["USDTRY"] = 1;
            map["USDPHP"] = 1;
            map["USDRUB"] = 1;
            map["USDKZT"] = 1;
            map["USDPKR"] = 1;
            _spotLagByPair = map;
        }

        public SpotSetDates Compute(string pair6, DateTime today, DateTime expiry)
        {
            var cals = _resolver.CalendarsForPair(pair6) ?? new string[0];
            int lag = GetSpotLagBD(pair6);

            // Samma princip som din legacy:
            // spotLagBD = BD(EXPIRY -> SETTLEMENT);  spotDate = TODAY + spotLagBD (BD)
            var spotDate = _cal.AddBusinessDays(cals, today, lag);
            var settlement = _cal.AddBusinessDays(cals, expiry, lag);

            var res = new SpotSetDates();
            res.SpotDate = spotDate;
            res.SettlementDate = settlement;
            res.SpotLagBusinessDays = lag;
            return res;
        }

        private int GetSpotLagBD(string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6)) return _defaultLag;
            var key = pair6.Replace("/", "").ToUpperInvariant();
            int lag;
            if (_spotLagByPair.TryGetValue(key, out lag)) return lag;
            return _defaultLag;
        }
    }
}
