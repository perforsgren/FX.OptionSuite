using System;
using System.Collections.Generic;
using System.Globalization;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Domain.Repositories;

namespace FxTradeHub.Domain.Parsing
{
    /// <summary>
    /// Parser för Volbroker FIX TradeCaptureReport (AE).
    /// Denna version innehåller:
    /// 1) FIX-tag parsning,
    /// 2) Header-parsning,
    /// 3) Per-leg extraktion till en intern datamodell (FixAeLeg),
    /// 4) Returnerar fortfarande ParseResult.Failed tills Trade-mappningen implementeras.
    /// </summary>
    public sealed class VolbrokerFixAeParser : IInboundMessageParser
    {

        /// <summary>
        /// Intern representation av ett leg i AE-meddelandet.
        /// Denna klass används endast i parsern innan Trade-objekt skapas.
        /// All konvertering (decimal, datum etc.) görs i senare steg.
        /// </summary>
        private sealed class FixAeLeg
        {
            /// <summary>
            /// Leg-typ enligt FIX-tag 609, t.ex. "OPT" eller "FWD".
            /// Används för att bestämma ProductType i Trade.
            /// </summary>
            public string SecurityType { get; set; }              // 609

            /// <summary>
            /// Leg side enligt FIX-tag 624, t.ex. "B" eller "C".
            /// Används för att sätta Buy/Sell per leg.
            /// </summary>
            public string Side { get; set; }                      // 624

            /// <summary>
            /// Tenor enligt FIX-tag 620, t.ex. "2M".
            /// </summary>
            public string Tenor { get; set; }                     // 620

            /// <summary>
            /// Call/Put-flagga enligt FIX-tag 764 ("C" eller "P").
            /// Detta är call/put i den valuta som StrikeCurrency anger.
            /// </summary>
            public string CallPut { get; set; }                   // 764

            /// <summary>
            /// Strikevaluta enligt FIX-tag 942.
            /// Avgör om call/put är definierad mot bas- eller prisvalutan.
            /// </summary>
            public string StrikeCurrency { get; set; }            // 942

            /// <summary>
            /// Rå sträng för strike enligt FIX-tag 612.
            /// </summary>
            public string StrikeRaw { get; set; }                 // 612

            /// <summary>
            /// Rå sträng för expiry datum enligt FIX-tag 611 (yyyyMMdd).
            /// </summary>
            public string ExpiryRaw { get; set; }                 // 611

            /// <summary>
            /// Venue-syntax för cut enligt FIX-tag 598.
            /// Används inte direkt, vi mappar cut via intern tabell.
            /// </summary>
            public string VenueCut { get; set; }                  // 598

            /// <summary>
            /// Rå notional enligt FIX-tag 687 (Volbroker skickar t.ex. "10" = 10M).
            /// </summary>
            public string NotionalRaw { get; set; }               // 687

            /// <summary>
            /// Valuta för notional enligt FIX-tag 556.
            /// </summary>
            public string NotionalCurrency { get; set; }          // 556

            /// <summary>
            /// Rå sträng för premium-belopp enligt FIX-tag 614 (endast optioner).
            /// </summary>
            public string PremiumRaw { get; set; }                // 614

            /// <summary>
            /// Premiumvaluta. V1 sätts lika med notionalvalutan.
            /// </summary>
            public string PremiumCurrency { get; set; }

            /// <summary>
            /// Rå sträng för settlementdatum enligt FIX-tag 248 (yyyyMMdd).
            /// </summary>
            public string SettlementDateRaw { get; set; }         // 248

            /// <summary>
            /// ISIN för leg:et enligt FIX-tag 602.
            /// </summary>
            public string Isin { get; set; }                      // 602

            /// <summary>
            /// Leg-UTI enligt FIX-tag 2893 (Volbroker-specifik identifierare).
            /// Används bara som fallback om vi saknar prefix + TVTIC.
            /// </summary>
            public string LegUti { get; set; }                    // 2893

            /// <summary>
            /// Leg-hedgerate / LastPx enligt FIX-tag 637.
            /// För FWD-leg används detta som HedgeRate.
            /// </summary>
            public string HedgeRateRaw { get; set; }              // 637

            /// <summary>
            /// TVTIC per leg, byggt från FIX 688/689 där 688 = "USI".
            /// </summary>
            public string Tvtic { get; set; }                     // 688/689 ("USI")
        }



        private sealed class FixTag
        {
            public int Tag { get; set; }
            public string Value { get; set; }
        }

        private readonly IStpLookupRepository _lookupRepository;

        public VolbrokerFixAeParser(IStpLookupRepository lookupRepository)
        {
            _lookupRepository = lookupRepository ?? throw new ArgumentNullException(nameof(lookupRepository));
        }

        /// <summary>
        /// Identifierar om detta är ett Volbroker AE-meddelande.
        /// </summary>
        public bool CanParse(MessageIn msg)
        {
            if (msg == null)
                return false;

            if (!string.Equals(msg.SourceType, "FIX", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(msg.SourceVenueCode, "VOLBROKER", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(msg.FixMsgType, "AE", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Parsar ett Volbroker FIX AE-meddelande från MessageIn och skapar
        /// en Trade per leg (optioner + eventuella hedge-legs, t.ex. FWD).
        /// </summary>
        /// <param name="msg">MessageIn-raden som innehåller FIX-data.</param>
        /// <returns>ParseResult med Success=true eller Failed() vid fel.</returns>
        public ParseResult Parse(MessageIn msg)
        {
            if (msg == null)
                return ParseResult.Failed("MessageIn är null.");

            if (string.IsNullOrWhiteSpace(msg.RawPayload))
                return ParseResult.Failed("RawPayload är tomt.");

            try
            {
                //
                // 1. Parsning av hela FIX-meddelandet → lista med FixTag
                //
                var tags = ParseFixTags(msg.RawPayload);
                if (tags == null || tags.Count == 0)
                    return ParseResult.Failed("Inga FIX-taggar hittades i RawPayload.");

                //
                // 2. Hämta headerfält
                //
                string currencyPair = GetTagValue(tags, 55) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currencyPair))
                    currencyPair = currencyPair.Replace("/", string.Empty).Trim().ToUpperInvariant();

                string mic = GetTagValue(tags, 30) ?? string.Empty;

                // Externt trade id: välj 818 → 571 → 17
                string externalTradeKey =
                    GetTagValue(tags, 818) ??
                    GetTagValue(tags, 571) ??
                    GetTagValue(tags, 17) ??
                    string.Empty;

                // TradeDate (75)
                DateTime tradeDate;
                if (!TryParseFixDate(GetTagValue(tags, 75), out tradeDate))
                    tradeDate = msg.ReceivedUtc.Date;

                // ExecutionTimeUtc (60)
                DateTime execTimeUtc;
                if (!TryParseFixTimestamp(GetTagValue(tags, 60), out execTimeUtc))
                    execTimeUtc = msg.SourceTimestamp ?? msg.ReceivedUtc;

                // Header-nivå spotkurs och forward-punkter (194 / 195) – används för FWD-legs
                decimal? lastSpotRate = null;
                if (TryParseDecimal(GetTagValue(tags, 194), out var tmpSpot))
                {
                    lastSpotRate = tmpSpot;
                }

                decimal? lastForwardPoints = null;
                if (TryParseDecimal(GetTagValue(tags, 195), out var tmpFwdPts))
                {
                    lastForwardPoints = tmpFwdPts;
                }

                // UTI-prefix från 1903: ta första 20 tecknen (LEI)
                string utiPrefix = GetTagValue(tags, 1903) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(utiPrefix) && utiPrefix.Length > 20)
                {
                    utiPrefix = utiPrefix.Substring(0, 20);
                }

                //
                // BUY/SELL (54) – fallback om leg.Side saknas
                //
                string headerBuySell = string.Empty;
                var sideRaw = GetTagValue(tags, 54);
                if (sideRaw == "1") headerBuySell = "BUY";
                else if (sideRaw == "2") headerBuySell = "SELL";

                //
                // 3. Extrahera legs → FixAeLeg
                //
                var legTagGroups = ExtractLegTagGroups(tags);
                var legs = new List<FixAeLeg>();

                foreach (var legTags in legTagGroups)
                {
                    var leg = ParseLeg(legTags);
                    if (leg != null)
                        legs.Add(leg);
                }

                if (legs.Count == 0)
                    return ParseResult.Failed("Inga legs hittades i AE-meddelandet.");

                //
                // 4. Lookups (cut, broker, portfölj)
                //
                string cutFromLookup = string.Empty;
                bool hasCutMapping = false;

                if (!string.IsNullOrWhiteSpace(currencyPair))
                {
                    var cutRule = _lookupRepository.GetExpiryCutByCurrencyPair(currencyPair);
                    if (cutRule != null && cutRule.IsActive)
                    {
                        cutFromLookup = cutRule.ExpiryCut ?? string.Empty;
                        hasCutMapping = !string.IsNullOrWhiteSpace(cutFromLookup);
                    }
                }

                string brokerCodeFromLookup = string.Empty;
                if (!string.IsNullOrWhiteSpace(msg.SourceVenueCode))
                {
                    // v1: använder SourceVenueCode som extern brokerkod.
                    var brokerMapping = _lookupRepository.GetBrokerMapping(msg.SourceVenueCode, msg.SourceVenueCode);
                    if (brokerMapping != null && brokerMapping.IsActive)
                    {
                        brokerCodeFromLookup = brokerMapping.NormalizedBrokerCode ?? string.Empty;
                    }
                    else
                    {
                        // Fallback: sätt broker = venue
                        brokerCodeFromLookup = msg.SourceVenueCode ?? string.Empty;
                    }
                }

                string portfolioMx3 = string.Empty;
                if (!string.IsNullOrWhiteSpace(currencyPair))
                {
                    // v1: MX3-portfölj baserad på valutapar + produkttyp OPTION_VANILLA
                    portfolioMx3 = _lookupRepository.GetPortfolioCode("MX3", currencyPair, "OPTION_VANILLA")
                                   ?? string.Empty;
                }

                //
                // 5. Skapa en Trade per leg
                //
                var parsedTrades = new List<ParsedTradeResult>();

                foreach (var leg in legs)
                {
                    // Notional: Volbroker skickar t.ex. "10" = 10 000 000
                    decimal notionalValue = 0m;
                    if (TryParseDecimal(leg.NotionalRaw, out var tmpNotional))
                    {
                        notionalValue = tmpNotional * 1_000_000m;
                    }

                    // Strike (rå) – används endast för optioner
                    decimal strikeValue = 0m;
                    TryParseDecimal(leg.StrikeRaw, out strikeValue);

                    // Premium (tag 614 = premium amount, ingen multiplikation) – endast optioner
                    decimal premiumValue = 0m;
                    TryParseDecimal(leg.PremiumRaw, out premiumValue);

                    // Expiry – endast relevant för optioner
                    DateTime expiryDate;
                    if (!TryParseFixDate(leg.ExpiryRaw, out expiryDate))
                    {
                        expiryDate = tradeDate;
                    }

                    // Settlement – gemensamt för både optioner och FWD
                    DateTime settlementDate;
                    if (!TryParseFixDate(leg.SettlementDateRaw, out settlementDate))
                    {
                        settlementDate = tradeDate;
                    }

                    // ProductType per leg: OPT → OptionVanilla, FWD → Fwd
                    var productType = MapProductTypeForLeg(leg);

                    // BUY/SELL per leg från tag 624 (B/C). Fallback till header 54 om saknas.
                    string legBuySell;
                    var legSide = leg.Side != null ? leg.Side.Trim().ToUpperInvariant() : string.Empty;
                    if (legSide == "B")
                        legBuySell = "BUY";
                    else if (legSide == "C" || legSide == "S")
                        legBuySell = "SELL";
                    else
                        legBuySell = headerBuySell;

                    // Workflow-events per leg (t.ex. varning vid saknad cut-mapping)
                    var workflowEvents = new List<TradeWorkflowEvent>();

                    // Basdata gemensamt för alla legs
                    var trade = new Trade
                    {
                        MessageInId = msg.MessageInId,

                        TradeId = externalTradeKey ?? string.Empty,
                        ProductType = productType,

                        SourceType = msg.SourceType ?? string.Empty,
                        SourceVenueCode = msg.SourceVenueCode ?? string.Empty,

                        // Lookup-fält fylls i senare iterationer:
                        CounterpartyCode = string.Empty,
                        BrokerCode = brokerCodeFromLookup,
                        TraderId = string.Empty,
                        InvId = string.Empty,
                        ReportingEntityId = string.Empty,

                        CurrencyPair = currencyPair,
                        Mic = mic,
                        Isin = leg.Isin ?? string.Empty,

                        TradeDate = tradeDate,
                        ExecutionTimeUtc = execTimeUtc,

                        BuySell = legBuySell,
                        Notional = notionalValue,
                        NotionalCurrency = leg.NotionalCurrency ?? string.Empty,
                        SettlementCurrency = leg.NotionalCurrency ?? string.Empty,
                        SettlementDate = settlementDate,

                        PortfolioMx3 = portfolioMx3
                    };

                    // ----- UTI/TVTIC per leg -----
                    if (!string.IsNullOrWhiteSpace(leg.Tvtic))
                    {
                        trade.Tvtic = leg.Tvtic;

                        if (!string.IsNullOrWhiteSpace(utiPrefix))
                        {
                            trade.Uti = utiPrefix + leg.Tvtic;
                        }
                        else
                        {
                            // Fallback: använd leg.LegUti om vi inte har prefix
                            trade.Uti = leg.LegUti ?? string.Empty;
                        }
                    }
                    else
                    {
                        // Fallback: ingen TVTIC hittad → använd 2893 om den finns
                        trade.Tvtic = string.Empty;
                        trade.Uti = leg.LegUti ?? string.Empty;
                    }

                    // ----- Produkt-beroende fält -----
                    if (productType == ProductType.OptionVanilla)
                    {
                        // Option: Call/Put (mot basvalutan), strike, expiry, premium
                        trade.CallPut = MapCallPutToBase(
                            leg.CallPut,
                            currencyPair,
                            leg.StrikeCurrency);

                        // Cut från mapping-tabellen (inte Volbrokers egna syntax)
                        trade.Cut = hasCutMapping ? cutFromLookup : string.Empty;

                        if (!hasCutMapping)
                        {
                            // Lägg ett workflow-event med varning om att cut-mapping saknas
                            var evt = new TradeWorkflowEvent
                            {
                                // StpTradeId sätts senare i orchestratorn när traden skrivs till DB
                                EventType = "WARNING",
                                Description =
                                    $"Ingen cut-mappning hittades för valutapar {currencyPair}. " +
                                    $"Venue-cut '{leg.VenueCut}' ignoreras.",
                                FieldName = "Cut",
                                OldValue = leg.VenueCut ?? string.Empty,
                                NewValue = string.Empty,
                                EventTimeUtc = DateTime.UtcNow,
                                InitiatorId = "VolbrokerFixAeParser"
                            };

                            workflowEvents.Add(evt);
                        }

                        trade.Strike = strikeValue;
                        trade.ExpiryDate = expiryDate;

                        trade.Premium = premiumValue;
                        trade.PremiumCurrency = leg.PremiumCurrency ?? leg.NotionalCurrency ?? string.Empty;
                        trade.PremiumDate = settlementDate;

                        // Hedge-fält används inte för option-legs i v1
                    }
                    else if (productType == ProductType.Fwd)
                    {
                        // FWD / hedge-leg:
                        // - Sätt hedge-fält
                        // - Låt option-fält (Strike/Expiry/Premium*) vara tomma

                        trade.HedgeType = "Forward";

                        // HedgeRate från leg.HedgeRateRaw (LastPx, tag 637)
                        if (TryParseDecimal(leg.HedgeRateRaw, out var hedgeRate))
                        {
                            trade.HedgeRate = hedgeRate;
                        }

                        // SpotRate / SwapPoints från header (194/195), om de finns
                        if (lastSpotRate.HasValue)
                        {
                            trade.SpotRate = lastSpotRate.Value;
                        }

                        if (lastForwardPoints.HasValue)
                        {
                            trade.SwapPoints = lastForwardPoints.Value;
                        }
                    }

                    parsedTrades.Add(new ParsedTradeResult
                    {
                        Trade = trade,
                        SystemLinks = new List<TradeSystemLink>(),
                        WorkflowEvents = workflowEvents
                    });
                }

                //
                // 6. Returnera OK-resultat
                //
                return ParseResult.Ok(parsedTrades);
            }
            catch (Exception ex)
            {
                return ParseResult.Failed("Fel vid parsning av Volbroker AE: " + ex.Message);
            }
        }



        /// <summary>
        /// Hämtar första matchande värde för en FIX-tagg.
        /// </summary>
        private static string GetTagValue(List<FixTag> tags, int tag)
        {
            if (tags == null) return null;

            foreach (var t in tags)
            {
                if (t.Tag == tag)
                    return t.Value;
            }
            return null;
        }

        /// <summary>
        /// Tolkar FIX-datum i format yyyyMMdd.
        /// </summary>
        private static bool TryParseFixDate(string raw, out DateTime date)
        {
            date = default(DateTime);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            return DateTime.TryParseExact(
                raw,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        /// <summary>
        /// Tolkar FIX timestamp yyyyMMdd-HH:mm:ss.fff
        /// </summary>
        private static bool TryParseFixTimestamp(string raw, out DateTime ts)
        {
            ts = default(DateTime);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            return DateTime.TryParseExact(
                raw,
                "yyyyMMdd-HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out ts);
        }

        /// <summary>
        /// Försök läsa decimal med invariant culture.
        /// </summary>
        private static bool TryParseDecimal(string raw, out decimal value)
        {
            return decimal.TryParse(
                raw,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value);
        }


        /// <summary>
        /// Identifierar leg-grupper i FIX-taggarna.
        /// Ett leg startar vid tag 600 och fortsätter tills nästa 600 eller slutet.
        /// </summary>
        private static List<List<FixTag>> ExtractLegTagGroups(List<FixTag> tags)
        {
            var result = new List<List<FixTag>>();
            List<FixTag> current = null;

            foreach (var t in tags)
            {
                if (t.Tag == 600) // start på nytt leg
                {
                    if (current != null)
                        result.Add(current);

                    current = new List<FixTag>();
                }

                if (current != null)
                    current.Add(t);
            }

            if (current != null)
                result.Add(current);

            return result;
        }

        /// <summary>
        /// Bestämmer ProductType för ett leg baserat på SecurityType (tag 609).
        /// OPT → OptionVanilla, FWD → Fwd, annars OptionVanilla som default.
        /// </summary>
        /// <param name="leg">Leg med SecurityType satt från FIX-tag 609.</param>
        /// <returns>ProductType som ska användas på Trade.</returns>
        private static ProductType MapProductTypeForLeg(FixAeLeg leg)
        {
            if (leg == null || string.IsNullOrWhiteSpace(leg.SecurityType))
                return ProductType.OptionVanilla;

            var secType = leg.SecurityType.Trim().ToUpperInvariant();

            if (secType == "FWD")
                return ProductType.Fwd;

            // OPT eller allt annat → OptionVanilla i v1
            return ProductType.OptionVanilla;
        }

        /// <summary>
        /// Mappar Volbrokers call/put (tag 764) till Call/Put definierat mot basvalutan.
        /// Hanterar både fallet där 764/942 gäller basvalutan och fallet där de gäller prisvalutan.
        /// </summary>
        /// <param name="rawCallPut">Rå call/put från FIX-tag 764 ("C" eller "P").</param>
        /// <param name="currencyPair">Valutapar från FIX-tag 55, t.ex. "USDJPY" eller "USD/JPY".</param>
        /// <param name="strikeCurrency">Strikevaluta från FIX-tag 942, t.ex. "JPY".</param>
        /// <returns>"Call", "Put" eller tom sträng om det inte går att tolka.</returns>
        private static string MapCallPutToBase(string rawCallPut, string currencyPair, string strikeCurrency)
        {
            if (string.IsNullOrWhiteSpace(rawCallPut))
                return string.Empty;

            var cp = rawCallPut.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(currencyPair) || string.IsNullOrWhiteSpace(strikeCurrency))
            {
                // Fallback: rak tolkning C→Call, P→Put
                return cp == "C" ? "Call" :
                       cp == "P" ? "Put" : string.Empty;
            }

            var pair = currencyPair.Replace("/", string.Empty).Trim().ToUpperInvariant();
            if (pair.Length != 6)
            {
                // Oväntat format – fallback
                return cp == "C" ? "Call" :
                       cp == "P" ? "Put" : string.Empty;
            }

            var baseCcy = pair.Substring(0, 3);
            var quoteCcy = pair.Substring(3, 3);
            var strikeCcy = strikeCurrency.Trim().ToUpperInvariant();

            // Fall 1: Call/Put definierad mot basvalutan
            if (strikeCcy == baseCcy)
            {
                // C = call i basvalutan → Call, P = put i basvalutan → Put
                return cp == "C" ? "Call" :
                       cp == "P" ? "Put" : string.Empty;
            }

            // Fall 2: Call/Put definierad mot prisvalutan
            if (strikeCcy == quoteCcy)
            {
                // Call i prisvalutan = put i basvalutan
                // Put i prisvalutan = call i basvalutan
                return cp == "C" ? "Put" :
                       cp == "P" ? "Call" : string.Empty;
            }

            // Om strikeCcy inte matchar varken bas eller pris → fallback
            return cp == "C" ? "Call" :
                   cp == "P" ? "Put" : string.Empty;
        }



        /// <summary>
        /// Bygger upp ett FixAeLeg-objekt från en lista FIX-taggar som tillhör ett leg.
        /// Här plockar vi bara ut råa strängar – konvertering sker senare.
        /// Hanterar även 688/689 för att bygga TVTIC per leg.
        /// </summary>
        /// <param name="legTags">FIX-taggar för ett leg (startar med 600).</param>
        /// <returns>Ett ifyllt FixAeLeg-objekt.</returns>
        private static FixAeLeg ParseLeg(List<FixTag> legTags)
        {
            var leg = new FixAeLeg();

            if (legTags == null || legTags.Count == 0)
                return leg;

            string lastQualifier = null; // för 688/689-par

            foreach (var t in legTags)
            {
                if (t.Tag == 688)
                {
                    // Kvalifierare för nästa 689-värde, t.ex. "USI", "USI.NAMESPACE", "SEFEXEC"
                    lastQualifier = t.Value;
                    continue;
                }

                if (t.Tag == 689)
                {
                    // V1: vi bryr oss bara om 688="USI" → 689 = TVTIC
                    if (!string.IsNullOrWhiteSpace(lastQualifier) &&
                        lastQualifier.Trim().ToUpperInvariant() == "USI")
                    {
                        leg.Tvtic = t.Value;
                    }

                    continue;
                }

                switch (t.Tag)
                {
                    case 609: // SecurityType, t.ex. OPT eller FWD
                        leg.SecurityType = t.Value;
                        break;

                    case 624: // Leg side ("B"/"C")
                        leg.Side = t.Value;
                        break;

                    case 620: // Tenor
                        leg.Tenor = t.Value;
                        break;

                    case 764: // Call/Put
                        leg.CallPut = t.Value;
                        break;

                    case 942: // Strikevaluta (t.ex. JPY)
                        leg.StrikeCurrency = t.Value;
                        break;

                    case 612: // Strike
                        leg.StrikeRaw = t.Value;
                        break;

                    case 611: // Expiry
                        leg.ExpiryRaw = t.Value;
                        break;

                    case 598: // Venue-cut
                        leg.VenueCut = t.Value;
                        break;

                    case 687: // Notional
                        leg.NotionalRaw = t.Value;
                        break;

                    case 556: // Notionalvaluta
                        leg.NotionalCurrency = t.Value;
                        break;

                    case 614: // Premium
                        leg.PremiumRaw = t.Value;
                        break;

                    case 602: // ISIN
                        leg.Isin = t.Value;
                        break;

                    case 2893: // Leg-UTI (Volbroker-id)
                        leg.LegUti = t.Value;
                        break;

                    case 248: // Settlementdatum
                        leg.SettlementDateRaw = t.Value;
                        break;

                    case 637: // LastPx / hedge rate
                        leg.HedgeRateRaw = t.Value;
                        break;
                }
            }

            // PremiumCurrency v1 = samma som notional currency
            leg.PremiumCurrency = leg.NotionalCurrency;

            return leg;
        }





        // ---------------------------------------------------------------------
        // FIX-tag parser
        // ---------------------------------------------------------------------
        private static List<FixTag> ParseFixTags(string rawPayload)
        {
            var result = new List<FixTag>();
            if (string.IsNullOrEmpty(rawPayload))
                return result;

            char[] separators;

            if (rawPayload.IndexOf('\x01') >= 0)
                separators = new[] { '\x01' };
            else if (rawPayload.IndexOf('|') >= 0)
                separators = new[] { '|' };
            else
                separators = new[] { ' ' };

            var fields = rawPayload.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            foreach (var field in fields)
            {
                int eq = field.IndexOf('=');
                if (eq <= 0 || eq >= field.Length - 1)
                    continue;

                var tagPart = field.Substring(0, eq).Trim();
                var valPart = field.Substring(eq + 1).Trim();

                int tagNum;
                if (!int.TryParse(tagPart, out tagNum))
                    continue;

                result.Add(new FixTag { Tag = tagNum, Value = valPart });
            }

            return result;
        }
    }
}
