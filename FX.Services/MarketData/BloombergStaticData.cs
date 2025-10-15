using Bloomberglp.Blpapi;
using System;
using System.Globalization;
using System.Threading;
using static System.Collections.Specialized.BitVector32;
using System.Xml.Linq;

public static class BloombergStaticData
{
    public static decimal? GetFxSpotMid(string pair, int timeoutMs = 3000)
    {
        if (TryGetFxSpotTwoWay(pair, out var bid, out var ask, timeoutMs))
        {
            if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
            if (bid > 0m) return bid;
            if (ask > 0m) return ask;
        }
        return null;
    }


    /// <summary>
    /// Försöker hämta tvåvägs spot (BID/ASK) för ett FX-par, t.ex. "EURSEK".
    /// Returnerar true när någon sida (>0) finns. Saknas en sida försöker vi alternative fält
    /// (PX_BID/PX_ASK), och saknas båda använder vi MID och speglar till båda.
    /// </summary>
    public static bool TryGetFxSpotTwoWay(string pair, out decimal bid, out decimal ask, int timeoutMs = 3000)
    {
        bid = 0m;
        ask = 0m;

        if (string.IsNullOrWhiteSpace(pair) || pair.Length < 6)
            return false;

        var ticker = pair.ToUpperInvariant() + " Curncy";

        var opts = new SessionOptions { ServerHost = "localhost", ServerPort = 8194 };
        using (var session = new Session(opts))
        {
            if (!session.Start()) return false;

            if (!session.OpenService("//blp/refdata"))
                return false;

            var svc = session.GetService("//blp/refdata");
            var req = svc.CreateRequest("ReferenceDataRequest");
            req.Append("securities", ticker);

            // Be om flera fält: primärt BID/ASK, alternativ PX_BID/PX_ASK, samt MID som fallback.
            var fields = req.GetElement("fields");
            fields.AppendValue("BID");
            fields.AppendValue("ASK");
            fields.AppendValue("PX_BID");
            fields.AppendValue("PX_ASK");
            fields.AppendValue("MID");

            session.SendRequest(req, null);

            var stopAt = DateTime.UtcNow.AddMilliseconds(timeoutMs <= 0 ? 3000 : timeoutMs);
            while (DateTime.UtcNow < stopAt)
            {
                var ev = session.NextEvent(500); // polla 0.5s åt gången
                switch (ev.Type)
                {
                    case Event.EventType.PARTIAL_RESPONSE:
                    case Event.EventType.RESPONSE:
                        foreach (var msg in ev)
                        {
                            if (!msg.MessageType.Equals(Name.GetName("ReferenceDataResponse")))
                                continue;

                            if (!msg.HasElement("securityData"))
                                continue;

                            var secDataArray = msg.GetElement("securityData");
                            if (secDataArray.NumValues <= 0)
                                continue;

                            var secData = secDataArray.GetValueAsElement(0);
                            if (!secData.HasElement("fieldData"))
                                continue;

                            var fieldData = secData.GetElement("fieldData");

                            // 1) Primärt: BID/ASK
                            var b = TryGetDec(fieldData, "BID");
                            var a = TryGetDec(fieldData, "ASK");

                            // 2) Alternativt: PX_BID/PX_ASK om BID/ASK saknas
                            if (!b.HasValue || b.Value <= 0m)
                                b = TryGetDec(fieldData, "PX_BID");
                            if (!a.HasValue || a.Value <= 0m)
                                a = TryGetDec(fieldData, "PX_ASK");

                            // 3) Fallback: MID → spegla till båda
                            if ((!b.HasValue || b.Value <= 0m) && (!a.HasValue || a.Value <= 0m))
                            {
                                var m = TryGetDec(fieldData, "MID");
                                if (m.HasValue && m.Value > 0m)
                                {
                                    bid = m.Value;
                                    ask = m.Value;
                                    return true;
                                }
                            }

                            // Finns åtminstone en sida? Returnera det vi har (saknas en sida → 0)
                            bid = (b.HasValue && b.Value > 0m) ? b.Value : 0m;
                            ask = (a.HasValue && a.Value > 0m) ? a.Value : 0m;

                            if (bid > 0m || ask > 0m)
                                return true;
                        }
                        break;

                    default:
                        // Ignorera andra eventtyper, fortsätt polla tills timeout
                        break;
                }
            }
        }

        return false; // timeout eller inget användbart svar
    }

    /// <summary>
    /// Hämtar tvåvägs spot som tuple. Returnerar null om inget hämtades.
    /// </summary>
    public static (decimal bid, decimal ask)? GetFxSpotTwoWay(string pair, int timeoutMs = 3000)
    {
        if (TryGetFxSpotTwoWay(pair, out var b, out var a, timeoutMs))
            return (b, a);
        return null;
    }

    private static decimal? TryGetDec(Element fieldData, string name)
    {
        var n = Name.GetName(name);
        if (fieldData.HasElement(n))
        {
            try
            {
                return Convert.ToDecimal(fieldData.GetElementAsFloat64(n), CultureInfo.InvariantCulture);
            }
            catch { /* ignore */ }
        }
        return null;
    }
}
