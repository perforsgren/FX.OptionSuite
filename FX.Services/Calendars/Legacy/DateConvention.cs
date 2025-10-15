using System;
using System.Data;
using System.Globalization;

namespace FX.Infrastructure.Calendars.Legacy
{
    /// <summary>
    /// Bärare av datumresultat.
    /// </summary>
    class Convention
    {
        public DateTime SpotDate { get; set; }
        public DateTime ExpiryDate { get; set; }
        public DateTime DeliveryDate { get; set; }
        public int Days { get; set; }
    }

    /// <summary>
    /// Räknar Spot/Expiry/Delivery givet valutapar + union av helgdagar.
    /// Policy:
    ///  - D/W-tenorer (1d, 2d, 1w, ...): Expiry får inte vara lör/sön eller 1 jan (loopa framåt tills OK).
    ///  - M/Y-tenorer (1m, 6m, 1y, ...): Expiry får vara helgdag, men inte lör/sön/1 jan.
    ///  - Specifikt datum (explicit): Expiry får vara helgdag, men inte lör/sön/1 jan
    ///    (samma som M/Y). Delivery = T+lag från det valda expiry-datumet.
    ///  - Delivery beräknas alltid via getForwardDate (business day, union).
    ///  - M-tenorer har EOM-stickiness: om SpotDate är sista bankdagen i månaden,
    ///    sätt Delivery till sista bankdagen i mål-månaden.
    /// </summary>
    class DateConvention
    {
        private readonly DataTable _holidays;          // union av parets kalendrar
        private readonly string _ccy;                  // EURSEK etc.
        private readonly int _tAdd;                    // spot-lag (T+?)
        private readonly HolidaySet _union;            // snabb lookup över unionen

        // Exempelpar med T+1 spot-lag
        private static readonly System.Collections.Generic.Dictionary<string, int> PairSpotLag =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "USDCAD", 1 }, { "USDTRY", 1 }, { "USDPHP", 1 }, { "USDRUB", 1 },
                { "USDKZT", 1 }, { "USDPKR", 1 }
            };

        private DateTime _spotDate, _expiryDate, _deliveryDate;
        private int _days;

        public DateConvention(string CCY, DataTable Holidays)
        {
            _holidays = Holidays ?? throw new ArgumentNullException(nameof(Holidays));
            _ccy = (CCY ?? "").Replace("/", "").Trim().ToUpperInvariant();
            if (_ccy.Length != 6) throw new ArgumentException("Valutapar måste vara 6 tecken, t.ex. EURSEK.", nameof(CCY));

            if (!PairSpotLag.TryGetValue(_ccy, out _tAdd)) _tAdd = 2;

            // _holidays förväntas vara union av båda benen
            _union = new HolidaySet(_holidays);
        }

        public DateTime SpotDate => _spotDate;
        public DateTime ExpiryDate => _expiryDate;
        public DateTime DeliveryDate => _deliveryDate;
        public int Days => _days;

        /// <summary>
        /// Huvudmetod: tolka tenor/datum och beräkna datum enligt policyn.
        /// </summary>
        public Convention GetConvention(string timeToExpiry)
        {
            var horizonDate = DateTime.Today;

            // --- ON: nästföljande business day från idag (som tidigare) ---
            if (!string.IsNullOrWhiteSpace(timeToExpiry) &&
                timeToExpiry.Trim().ToLowerInvariant() == "on")
            {
                _spotDate = getForwardDate(horizonDate, _tAdd);
                _expiryDate = moveBusinessDays(horizonDate, 1);
                if (IsJan1(_expiryDate)) _expiryDate = moveBusinessDays(_expiryDate, 1);
                _deliveryDate = getForwardDate(_expiryDate, _tAdd);
            }
            // --- D-tenorer: ej lör/sön, ej 1 jan (loop framåt) ---
            else if (!string.IsNullOrWhiteSpace(timeToExpiry) &&
                     timeToExpiry.Trim().ToLowerInvariant().EndsWith("d"))
            {
                _spotDate = getForwardDate(horizonDate, _tAdd);

                double daysToExpiry = double.Parse(
                    timeToExpiry.Trim().ToLowerInvariant().Replace("d", ""),
                    CultureInfo.InvariantCulture);

                var candidate = horizonDate.AddDays(daysToExpiry);
                _expiryDate = AdjustExpiry_DayWeek(candidate);    // helg/1-jan-loop
                _deliveryDate = getForwardDate(_expiryDate, _tAdd);
            }
            // --- W-tenorer: samma som D ---
            else if (!string.IsNullOrWhiteSpace(timeToExpiry) &&
                     timeToExpiry.Trim().ToLowerInvariant().EndsWith("w"))
            {
                _spotDate = getForwardDate(horizonDate, _tAdd);

                var nStr = timeToExpiry.Trim().ToLowerInvariant().Replace("w", "");
                int weeks = int.Parse(nStr, CultureInfo.InvariantCulture);

                var candidate = horizonDate.AddDays(7 * weeks);
                _expiryDate = AdjustExpiry_DayWeek(candidate);    // helg/1-jan-loop
                _deliveryDate = getForwardDate(_expiryDate, _tAdd);
            }
            // --- M-tenorer: EOM-stickiness + Modified Following på unionskalendern ---
            else if (!string.IsNullOrWhiteSpace(timeToExpiry) &&
                     timeToExpiry.Trim().ToLowerInvariant().EndsWith("m"))
            {
                var ms = timeToExpiry.Substring(0, timeToExpiry.Length - 1);
                int months;
                if (int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out months))
                {
                    _spotDate = getForwardDate(horizonDate, _tAdd);

                    // EOM-stickiness: om SpotDate är sista bankdagen i månaden → Delivery = sista bankdagen i mål-månaden
                    if (IsLastBusinessDayOfMonth(_spotDate))
                    {
                        _deliveryDate = LastBusinessDayOfMonth(_spotDate.AddMonths(months));
                    }
                    else
                    {
                        _deliveryDate = addMonths(_spotDate, months); // Modified Following
                    }

                    _expiryDate = getBackwardDate(_deliveryDate, _tAdd); // backa T-lag i BD, men expiry får vara helgdag (ej lör/sön/1 jan)
                    _expiryDate = AdjustExpiry_Other(_expiryDate);       // tillåt holiday, men ej lör/sön/1-jan
                }
            }
            // --- Y-tenorer: helgdag tillåts, men ej lör/sön/1-jan ---
            else if (!string.IsNullOrWhiteSpace(timeToExpiry) &&
                     timeToExpiry.Trim().ToLowerInvariant().EndsWith("y"))
            {
                var ys = timeToExpiry.Substring(0, timeToExpiry.Length - 1);
                int years;
                if (int.TryParse(ys, NumberStyles.Integer, CultureInfo.InvariantCulture, out years))
                {
                    _spotDate = getForwardDate(horizonDate, _tAdd);

                    // EOM-stickiness även för år om spot är EOM
                    if (IsLastBusinessDayOfMonth(_spotDate))
                    {
                        _deliveryDate = LastBusinessDayOfMonth(_spotDate.AddYears(years));
                    }
                    else
                    {
                        _deliveryDate = addYears(_spotDate, years); // Modified Following
                    }

                    _expiryDate = getBackwardDate(_deliveryDate, _tAdd);
                    _expiryDate = AdjustExpiry_Other(_expiryDate);
                }
            }
            // --- Specifikt datum (explicit): samma regel som M/Y (tillåt holiday, ej lör/sön/1-jan) ---
            else
            {
                _spotDate = getForwardDate(horizonDate, _tAdd);

                var explicitDate = DateTime.Parse(timeToExpiry, CultureInfo.InvariantCulture);

                // Expiry = explicit datum rullat ENDAST för helg/1-jan (helgdag OK)
                _expiryDate = AdjustExpiry_Other(explicitDate);

                // Delivery = T+lag från detta expiry-datum (business day)
                _deliveryDate = getForwardDate(_expiryDate, _tAdd);
            }

            _days = (int)(_expiryDate - horizonDate).TotalDays;

            return new Convention
            {
                SpotDate = _spotDate,
                ExpiryDate = _expiryDate,
                DeliveryDate = _deliveryDate,
                Days = _days
            };
        }

        // ========= Policy-hjälpare =========

        private static bool IsJan1(DateTime d) { return d.Month == 1 && d.Day == 1; }

        private static bool IsWeekend(DateTime d)
        {
            var w = d.DayOfWeek;
            return w == DayOfWeek.Saturday || w == DayOfWeek.Sunday;
        }

        /// <summary>
        /// D/W-policy: loopa tills datum inte är lör/sön eller 1 jan.
        /// Täcker fallet: 3d → 1 jan → +1 dag = 2 jan (som kan vara helg) → rulla till måndag → OK.
        /// </summary>
        private static DateTime AdjustExpiry_DayWeek(DateTime d)
        {
            while (true)
            {
                if (IsWeekend(d))
                {
                    int daysToMonday = ((int)DayOfWeek.Monday - (int)d.DayOfWeek + 7) % 7;
                    if (daysToMonday == 0) daysToMonday = 7;
                    d = d.AddDays(daysToMonday);
                    continue;
                }
                if (IsJan1(d))
                {
                    d = d.AddDays(1);
                    continue;
                }
                return d;
            }
        }

        /// <summary>
        /// M/Y och explicit datum: tillåt helgdagar men inte lör/sön eller 1 jan.
        /// Rulla framåt tills villkoret uppfylls.
        /// </summary>
        private static DateTime AdjustExpiry_Other(DateTime d)
        {
            while (IsWeekend(d) || IsJan1(d))
                d = d.AddDays(1);
            return d;
        }

        // ========= Datumrörelse (business-day) =========

        /// <summary>
        /// Sista bankdagen i samma månad som 'd', enligt unionskalendern.
        /// </summary>
        private DateTime LastBusinessDayOfMonth(DateTime d)
        {
            var y = d.Year; var m = d.Month;
            var lastCalDay = new DateTime(y, m, DateTime.DaysInMonth(y, m));
            var x = lastCalDay;
            while (IsWeekend(x) || _union.IsNonBusinessDay(x))
                x = x.AddDays(-1);
            return x;
        }

        /// <summary>
        /// Är d sista bankdagen i månaden (union)?
        /// </summary>
        private bool IsLastBusinessDayOfMonth(DateTime d)
        {
            return d.Date == LastBusinessDayOfMonth(d).Date;
        }

        /// <summary>
        /// Flytta ett antal business-dagar (±) över unionen.
        /// </summary>
        private DateTime moveBusinessDays(DateTime startDate, int businessDays)
        {
            int direction = Math.Sign(businessDays);
            if (direction == 1)
            {
                if (startDate.DayOfWeek == DayOfWeek.Saturday) { startDate = startDate.AddDays(2); businessDays--; }
                else if (startDate.DayOfWeek == DayOfWeek.Sunday) { startDate = startDate.AddDays(1); businessDays--; }
            }
            else if (direction == -1)
            {
                if (startDate.DayOfWeek == DayOfWeek.Saturday) { startDate = startDate.AddDays(-1); businessDays++; }
                else if (startDate.DayOfWeek == DayOfWeek.Sunday) { startDate = startDate.AddDays(-2); businessDays++; }
            }

            int initialDow = (int)startDate.DayOfWeek;
            int weeksBase = Math.Abs(businessDays / 5);
            int addDays = Math.Abs(businessDays % 5);

            if ((direction == 1 && addDays + initialDow > 5) ||
                (direction == -1 && addDays >= initialDow))
            {
                addDays += 2;
            }

            int totalDays = (weeksBase * 7) + addDays;
            var d = startDate.AddDays(totalDays * direction);

            // rulla över helgdagar i unionen
            while (_union.IsNonBusinessDay(d))
                d = d.AddDays(direction > 0 ? 1 : -1);

            return d;
        }

        /// <summary>
        /// Delivery via månader: Modified Following på unionskalendern (ingen EOM-stickiness här).
        /// (EOM-stickiness hanteras i GetConvention när SpotDate är EOM.)
        /// </summary>
        private DateTime addMonths(DateTime spotDate, int nrMonthAdd)
        {
            DateTime raw = spotDate.AddMonths(nrMonthAdd);
            DateTime start = raw.Date;

            if (!_union.IsNonBusinessDay(start) &&
                start.DayOfWeek != DayOfWeek.Saturday &&
                start.DayOfWeek != DayOfWeek.Sunday)
                return start;

            DateTime fwd = start;
            while (fwd.DayOfWeek == DayOfWeek.Saturday || fwd.DayOfWeek == DayOfWeek.Sunday || _union.IsNonBusinessDay(fwd))
                fwd = fwd.AddDays(1);

            if (fwd.Month != start.Month)
            {
                DateTime back = start;
                while (back.DayOfWeek == DayOfWeek.Saturday || back.DayOfWeek == DayOfWeek.Sunday || _union.IsNonBusinessDay(back))
                    back = back.AddDays(-1);
                return back;
            }

            return fwd;
        }

        /// <summary>
        /// Delivery via år: Modified Following på unionskalendern.
        /// (EOM-stickiness hanteras i GetConvention när SpotDate är EOM.)
        /// </summary>
        private DateTime addYears(DateTime spotDate, int nrYearsAdd)
        {
            DateTime raw = spotDate.AddYears(nrYearsAdd);
            DateTime start = raw.Date;

            if (!_union.IsNonBusinessDay(start) &&
                start.DayOfWeek != DayOfWeek.Saturday &&
                start.DayOfWeek != DayOfWeek.Sunday)
                return start;

            DateTime fwd = start;
            while (fwd.DayOfWeek == DayOfWeek.Saturday || fwd.DayOfWeek == DayOfWeek.Sunday || _union.IsNonBusinessDay(fwd))
                fwd = fwd.AddDays(1);

            if (fwd.Month != start.Month)
            {
                DateTime back = start;
                while (back.DayOfWeek == DayOfWeek.Saturday || back.DayOfWeek == DayOfWeek.Sunday || _union.IsNonBusinessDay(back))
                    back = back.AddDays(-1);
                return back;
            }

            return fwd;
        }

        /// <summary>
        /// Spot T+lag – med USD-special (andra benets kalender).
        /// </summary>
        public DateTime getForwardDate(DateTime horizonDate, int tAdd)
        {
            DateTime output = horizonDate.AddDays(1);
            int numBusDays = 0;

            bool anyLegUSD = _ccy.StartsWith("USD", StringComparison.OrdinalIgnoreCase) ||
                             _ccy.EndsWith("USD", StringComparison.OrdinalIgnoreCase);

            if (!anyLegUSD)
            {
                do
                {
                    if (!_union.IsNonBusinessDay(output)) numBusDays++;
                    if (numBusDays < tAdd) output = output.AddDays(1);
                } while (numBusDays < tAdd);
            }
            else
            {
                // USD-special: använd "andra benet" (ej USA) för att räkna tAdd
                do
                {
                    if (!isCCYHoliday_notUS(output)) numBusDays++;
                    if (numBusDays < tAdd) output = output.AddDays(1);
                } while (numBusDays < tAdd);
            }

            // Efter tAdd: säkra business day i unionen
            while (_union.IsNonBusinessDay(output)) output = output.AddDays(1);

            return output;
        }

        /// <summary>
        /// Hitta SENASTE giltiga expiry &lt; delivery så att getForwardDate(expiry, tAdd) == delivery.
        /// Expiry får vara helgdag, men inte lör/sön/1 jan (policy för M/Y & explicit).
        /// </summary>
        private DateTime getBackwardDate(DateTime deliveryDate, int tAdd)
        {
            DateTime e = deliveryDate.AddDays(-1).Date;

            for (int guard = 0; guard < 366; guard++)
            {
                if (!IsWeekend(e) && !IsJan1(e))
                {
                    var dFwd = getForwardDate(e, tAdd);
                    if (dFwd.Date == deliveryDate.Date)
                        return e;
                }
                e = e.AddDays(-1);
            }

            // Fallback: backa tAdd business-dagar och säkra ej helg/1 jan
            DateTime fb = moveBusinessDays(deliveryDate, -tAdd);
            while (IsWeekend(fb) || IsJan1(fb)) fb = fb.AddDays(-1);
            return fb;
        }

        // ========= Kalenderhjälp för "andra benet" (ej USA om USD är ena leg) =========

        private bool isCCYHoliday_notUS(DateTime date)
        {
            var cal = CurrencyCalendarMapper.GetCalendarsForPair(_ccy);
            var a = cal[0]; var b = cal[1];

            // om ena är USA – använd bara den andra kalendern
            var list = new System.Collections.Generic.List<string>(2);
            if (!string.Equals(a, "USA", StringComparison.OrdinalIgnoreCase)) list.Add(a);
            if (!string.Equals(b, "USA", StringComparison.OrdinalIgnoreCase)) list.Add(b);

            var hs = new HolidaySet(_holidays, list);
            return hs.IsNonBusinessDay(date);
        }
    }
}
