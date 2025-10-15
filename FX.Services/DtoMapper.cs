// ============================================================
// SPRINT 1 – STEG 4: Enkel mapper (Messages DTO -> Core Domän)
// Varför:  Hålla FX.Messages fria från Core-beroenden, men ändå kunna
//          arbeta starkt typat i services (Core).
// Vad:     Parsear Pair6/LegDto/VolNodeDto till domänobjekt (CurrencyPair,
//          OptionLeg, Strike, Expiry, VolNode).
// Klar när:VolService/PriceEngine kan konsumera domänobjekt från UI-commands.
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using FX.Core.Domain;
using FX.Messages.Dtos;

namespace FX.Services
{
    public static class DtoMapper
    {
        public static CurrencyPair ToPair(string pair6)
        {
            return CurrencyPair.FromPair6(pair6);
        }

        public static List<OptionLeg> ToOptionLegs(CurrencyPair pair, IEnumerable<LegDto> dtos)
        {
            var list = new List<OptionLeg>();
            if (dtos == null) return list;

            foreach (var dto in dtos)
            {
                var side = ParseSide(dto.Side);
                var type = ParseType(dto.Type);
                var strike = ParseStrike(dto.Strike);
                var expiry = ParseExpiry(dto.ExpiryIso);
                var leg = new OptionLeg(pair, side, type, strike, new Expiry(expiry), dto.Notional);
                list.Add(leg);
            }
            return list;
        }

        public static List<VolNode> ToVolNodes(IEnumerable<VolNodeDto> dtos)
        {
            var list = new List<VolNode>();
            if (dtos == null) return list;
            foreach (var dto in dtos)
            {
                var tenor = (dto.Tenor ?? "").ToUpperInvariant();
                var label = (dto.Label ?? "").ToUpperInvariant();
                list.Add(new VolNode(tenor, label, dto.Vol));
            }
            return list;
        }

        private static BuySell ParseSide(string s)
        {
            var u = (s ?? "").Trim().ToUpperInvariant();
            if (u == "BUY") return BuySell.Buy;
            if (u == "SELL") return BuySell.Sell;
            throw new ArgumentException("Ogiltig Side (BUY/SELL).");
        }

        private static OptionType ParseType(string s)
        {
            var u = (s ?? "").Trim().ToUpperInvariant();
            if (u == "CALL") return OptionType.Call;
            if (u == "PUT") return OptionType.Put;
            throw new ArgumentException("Ogiltig Type (CALL/PUT).");
        }

        private static Strike ParseStrike(string s)
        {
            var t = (s ?? "").Trim().ToUpperInvariant();
            // Delta-format: "25D", "10D", "75D"
            if (t.EndsWith("D"))
            {
                var num = t.Substring(0, t.Length - 1);
                double d;
                if (!double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    throw new ArgumentException("Ogiltigt delta-strike: " + s);
                return new Strike(d);
            }
            // Absolut strike (decimal)
            decimal k;
            if (!decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out k))
                throw new ArgumentException("Ogiltigt strike: " + s);
            return new Strike(k);
        }

        private static DateTime ParseExpiry(string iso)
        {
            // Förväntat "yyyy-MM-dd"
            DateTime dt;
            if (DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
                return dt.Date;
            // fallback för säkerhets skull
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
                return dt.Date;
            throw new ArgumentException("Ogiltigt expiry-datum: " + iso);
        }
    }
}
