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

        /// <summary>
        /// Intern representation av en PARTIES-post (448/452/523) i AE-meddelandet.
        /// </summary>
        private sealed class PartyInfo
        {
            /// <summary>
            /// PartyID (tag 448), t.ex. "SWEDSTK", "DB".
            /// </summary>
            public string PartyId { get; set; }

            /// <summary>
            /// PartyRole (tag 452), t.ex. 1 = Kunde, 12 = Executing Firm, 122 = Trader-kod.
            /// </summary>
            public int? PartyRole { get; set; }

            /// <summary>
            /// Trader-kortkod (tag 523) om den finns på denna party-rad.
            /// </summary>
            public string TraderShortCode { get; set; }
        }

        /// <summary>
        /// Intern representation av en Side (NoSides-grupp) i AE-meddelandet.
        /// Används för att hitta Swedbanks egen side och dess 54-värde.
        /// </summary>
        private sealed class SideInfo
        {
            /// <summary>
            /// Side (tag 54) på denna side, t.ex. "1" (Buy) eller "2" (Sell).
            /// </summary>
            public string Side { get; set; }

            /// <summary>
            /// Anger om denna side representerar Swedbanks sida
            /// (dvs. innehåller party 448=SWEDSTK med 452=1).
            /// </summary>
            public bool IsOwnSide { get; set; }
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
        /// en Trade per leg (vanillaoptioner + eventuella hedge-legs, t.ex. FWD).
        /// Lägger även TradeWorkflowEvent-varningar när lookup-data saknas
        /// (trader-routing, cut, motpart, broker, MX3-portfölj, CalypsoBook).
        /// </summary>
        /// <param name="msg">MessageIn-raden som innehåller FIX-data.</param>
        /// <returns>ParseResult med Success = true eller Failed() vid fel.</returns>
        public ParseResult Parse(MessageIn msg)
        {
            if (msg == null)
                return ParseResult.Failed("MessageIn är null.");

            if (string.IsNullOrWhiteSpace(msg.RawPayload))
                return ParseResult.Failed("RawPayload är tomt.");

            try
            {
                // 1. Parsning av hela FIX-meddelandet → lista med FixTag
                var tags = ParseFixTags(msg.RawPayload);
                if (tags == null || tags.Count == 0)
                    return ParseResult.Failed("Inga FIX-taggar hittades i RawPayload.");

                // 2. Headerfält
                string currencyPair = GetTagValue(tags, 55) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currencyPair))
                    currencyPair = currencyPair.Replace("/", string.Empty).Trim().ToUpperInvariant();

                string mic = GetTagValue(tags, 30) ?? string.Empty;

                // Externt trade-id: välj 818 → 571 → 17
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

                // Header spotkurs och forwardpunkter (194 / 195)
                decimal? lastSpotRate = null;
                if (TryParseDecimal(GetTagValue(tags, 194), out var tmpSpot))
                    lastSpotRate = tmpSpot;

                decimal? lastForwardPoints = null;
                if (TryParseDecimal(GetTagValue(tags, 195), out var tmpFwdPts))
                    lastForwardPoints = tmpFwdPts;

                // UTI-prefix från 1903
                string utiPrefix = GetTagValue(tags, 1903) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(utiPrefix) && utiPrefix.Length > 20)
                    utiPrefix = utiPrefix.Substring(0, 20);

                // 2a. BUY/SELL – baseline ur Swedbanks side (NoSides/Party)
                var sides = ExtractSides(tags);

                string ourSideRaw = null;
                foreach (var s in sides)
                {
                    if (s.IsOwnSide)
                    {
                        ourSideRaw = s.Side;
                        break;
                    }
                }

                // Fallback om vi inte hittar vår side: använd första 54 i meddelandet
                if (string.IsNullOrWhiteSpace(ourSideRaw))
                    ourSideRaw = GetTagValue(tags, 54);

                string baselineBuySell = string.Empty;
                if (ourSideRaw == "1") baselineBuySell = "BUY";
                else if (ourSideRaw == "2") baselineBuySell = "SELL";

                // 3. PARTY → motpart + trader
                var parties = ExtractParties(tags);

                var externalCounterpartyId = ResolveCounterpartyCodeFromParties(parties);
                string externalCounterpartyIdForEvent = externalCounterpartyId ?? string.Empty;

                string counterpartyCode = string.Empty;
                bool hasCounterpartyMapping = false;

                if (!string.IsNullOrWhiteSpace(externalCounterpartyId))
                {
                    var mappedCp = _lookupRepository.ResolveCounterpartyCode(
                        msg.SourceType,
                        msg.SourceVenueCode,
                        externalCounterpartyId);

                    if (!string.IsNullOrWhiteSpace(mappedCp))
                    {
                        counterpartyCode = mappedCp;
                        hasCounterpartyMapping = true;
                    }
                    else
                    {
                        // Fallback: använd extern kod men logga varning per trade
                        counterpartyCode = externalCounterpartyId;
                    }
                }

                var traderShortCode = ExtractTraderShortCodeFromParties(parties);
                TraderRoutingInfo traderRoutingInfo = null;
                if (!string.IsNullOrWhiteSpace(traderShortCode))
                {
                    traderRoutingInfo = _lookupRepository.GetTraderRoutingInfo(
                        msg.SourceVenueCode,
                        traderShortCode);
                }

                // 4. Lookups: cut, broker, portfölj
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
                bool hasBrokerMapping = false;
                if (!string.IsNullOrWhiteSpace(msg.SourceVenueCode))
                {
                    var brokerMapping = _lookupRepository.GetBrokerMapping(
                        msg.SourceVenueCode,
                        msg.SourceVenueCode);

                    if (brokerMapping != null && brokerMapping.IsActive)
                    {
                        brokerCodeFromLookup = brokerMapping.NormalizedBrokerCode ?? string.Empty;
                        hasBrokerMapping = !string.IsNullOrWhiteSpace(brokerCodeFromLookup);
                    }
                    else
                    {
                        // Fallback: använd venue-koden som broker
                        brokerCodeFromLookup = msg.SourceVenueCode ?? string.Empty;
                    }
                }

                string portfolioMx3 = string.Empty;
                bool hasPortfolioMapping = false;
                if (!string.IsNullOrWhiteSpace(currencyPair))
                {
                    // V1: använder OPTION_VANILLA som default produkttyp i portföljregeln
                    portfolioMx3 = _lookupRepository.GetPortfolioCode("MX3", currencyPair, "OPTION_VANILLA")
                                   ?? string.Empty;

                    hasPortfolioMapping = !string.IsNullOrWhiteSpace(portfolioMx3);
                }

                // 5. Legs (bygg per-leg-taggrupper på ett robust sätt)
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

                // 6. Bygg trades per leg
                var parsedTrades = new List<ParsedTradeResult>();

                foreach (var leg in legs)
                {
                    var workflowEvents = new List<TradeWorkflowEvent>();

                    // Trader-routing
                    string traderId = string.Empty;
                    string invId = string.Empty;
                    string reportingEntityId = string.Empty;

                    if (traderRoutingInfo != null)
                    {
                        traderId = traderRoutingInfo.InternalUserId ?? string.Empty;
                        invId = traderRoutingInfo.InvId ?? string.Empty;
                        reportingEntityId = traderRoutingInfo.ReportingEntityId ?? string.Empty;
                    }
                    else if (!string.IsNullOrWhiteSpace(traderShortCode))
                    {
                        var evt = new TradeWorkflowEvent
                        {
                            EventType = "WARNING",
                            FieldName = "TraderId",
                            OldValue = traderShortCode,
                            NewValue = string.Empty,
                            Description =
                                $"Ingen trader-mappning hittades för venue '{msg.SourceVenueCode}' " +
                                $"och traderkod '{traderShortCode}'. TraderId/InvId/ReportingEntityId lämnas tomma.",
                            EventTimeUtc = DateTime.UtcNow,
                            InitiatorId = "VolbrokerFixAeParser"
                        };
                        workflowEvents.Add(evt);
                    }

                    // Notional: Volbroker skickar t.ex. "10" = 10 000 000
                    decimal notionalValue = 0m;
                    if (TryParseDecimal(leg.NotionalRaw, out var tmpNotional))
                        notionalValue = tmpNotional * 1_000_000m;

                    var productType = MapProductTypeForLeg(leg);

                    // BUY/SELL per leg – ur Swedbanks perspektiv
                    string legBuySell;
                    var legSide = leg.Side != null ? leg.Side.Trim().ToUpperInvariant() : string.Empty;

                    if (legSide == "B")
                    {
                        // Samma riktning som Swedbanks baseline
                        legBuySell = baselineBuySell;
                    }
                    else if (legSide == "C" || legSide == "S")
                    {
                        // Motsatt riktning mot baseline
                        if (baselineBuySell == "BUY")
                            legBuySell = "SELL";
                        else if (baselineBuySell == "SELL")
                            legBuySell = "BUY";
                        else
                            legBuySell = baselineBuySell; // tom / okänd
                    }
                    else
                    {
                        // Om vi inte vet leg-side, använd baseline rakt av
                        legBuySell = baselineBuySell;
                    }

                    // Gemensamt trade-objekt (fylls sen per produkt-typ)
                    var trade = new Trade
                    {
                        MessageInId = msg.MessageInId,

                        TradeId = externalTradeKey ?? string.Empty,
                        ProductType = productType,

                        SourceType = msg.SourceType ?? string.Empty,
                        SourceVenueCode = msg.SourceVenueCode ?? string.Empty,

                        CounterpartyCode = counterpartyCode,
                        BrokerCode = brokerCodeFromLookup,
                        TraderId = traderId,
                        InvId = invId,
                        ReportingEntityId = reportingEntityId,

                        CurrencyPair = currencyPair,
                        Mic = mic,
                        Isin = leg.Isin ?? string.Empty,

                        TradeDate = tradeDate,
                        ExecutionTimeUtc = execTimeUtc,

                        BuySell = legBuySell,
                        Notional = notionalValue,
                        NotionalCurrency = leg.NotionalCurrency ?? string.Empty,
                        SettlementCurrency = leg.NotionalCurrency ?? string.Empty,
                        SettlementDate = tradeDate,     // kan överstyras nedan
                        PortfolioMx3 = portfolioMx3
                    };

                    // UTI + TVTIC per leg (unik per leg)
                    if (!string.IsNullOrWhiteSpace(leg.Tvtic))
                    {
                        trade.Tvtic = leg.Tvtic;
                        if (!string.IsNullOrWhiteSpace(utiPrefix))
                            trade.Uti = utiPrefix + leg.Tvtic;
                        else
                            trade.Uti = leg.LegUti ?? string.Empty;
                    }
                    else
                    {
                        trade.Tvtic = string.Empty;
                        trade.Uti = leg.LegUti ?? string.Empty;
                    }

                    // Datum för settlement (gäller både optioner och FWD, men kommer oftast från 248)
                    DateTime settlementDate;
                    if (!TryParseFixDate(leg.SettlementDateRaw, out settlementDate))
                        settlementDate = tradeDate;
                    trade.SettlementDate = settlementDate;

                    if (productType == ProductType.OptionVanilla)
                    {
                        //
                        // OPTION-FÄLT (ska INTE sättas på FWD-legs)
                        //
                        decimal strikeValue = 0m;
                        TryParseDecimal(leg.StrikeRaw, out strikeValue);

                        decimal premiumValue = 0m;
                        TryParseDecimal(leg.PremiumRaw, out premiumValue);

                        DateTime expiryDate;
                        if (!TryParseFixDate(leg.ExpiryRaw, out expiryDate))
                            expiryDate = tradeDate;

                        trade.CallPut = MapCallPutToBase(
                            leg.CallPut,
                            currencyPair,
                            leg.StrikeCurrency);

                        trade.Cut = hasCutMapping ? cutFromLookup : string.Empty;

                        if (!hasCutMapping)
                        {
                            var evt = new TradeWorkflowEvent
                            {
                                EventType = "WARNING",
                                FieldName = "Cut",
                                OldValue = leg.VenueCut ?? string.Empty,
                                NewValue = string.Empty,
                                Description =
                                    $"Ingen cut-mappning hittades för valutapar {currencyPair}. " +
                                    $"Venue-cut '{leg.VenueCut}' ignoreras.",
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
                    }
                    else if (productType == ProductType.Fwd)
                    {
                        //
                        // HEDGE / FWD-FÄLT (ska INTE påverka option-ben)
                        //
                        trade.HedgeType = "Forward";

                        if (TryParseDecimal(leg.HedgeRateRaw, out var hedgeRate))
                            trade.HedgeRate = hedgeRate;

                        if (lastSpotRate.HasValue)
                            trade.SpotRate = lastSpotRate.Value;

                        if (lastForwardPoints.HasValue)
                            trade.SwapPoints = lastForwardPoints.Value;
                    }

                    // ---------------------------------------------------------
                    // CalypsoBook via stp_calypso_book_user (TraderId -> Book)
                    // Gäller ENDAST för spot/fwd-deals (ProductType.Spot / ProductType.Fwd)
                    // ---------------------------------------------------------
                    bool hasCalypsoBookMapping = false;

                    if ((productType == ProductType.Spot || productType == ProductType.Fwd)
                        && !string.IsNullOrWhiteSpace(trade.TraderId))
                    {
                        var calypsoRule = _lookupRepository.GetCalypsoBookByTraderId(trade.TraderId);

                        if (calypsoRule != null &&
                            calypsoRule.IsActive &&
                            !string.IsNullOrWhiteSpace(calypsoRule.CalypsoBook))
                        {
                            trade.CalypsoBook = calypsoRule.CalypsoBook;
                            hasCalypsoBookMapping = true;
                        }
                    }

                    //
                    // Header-baserade lookup-varningar som gäller denna trade
                    //
                    if (!hasCounterpartyMapping && !string.IsNullOrWhiteSpace(externalCounterpartyIdForEvent))
                    {
                        var evt = new TradeWorkflowEvent
                        {
                            EventType = "WARNING",
                            FieldName = "CounterpartyCode",
                            OldValue = externalCounterpartyIdForEvent,
                            NewValue = string.Empty,
                            Description =
                                $"Ingen motpartsmappning hittades för extern kod '{externalCounterpartyIdForEvent}' " +
                                $"(SourceType='{msg.SourceType}', SourceVenue='{msg.SourceVenueCode}'). " +
                                "CounterpartyCode sätts till extern kod.",
                            EventTimeUtc = DateTime.UtcNow,
                            InitiatorId = "VolbrokerFixAeParser"
                        };
                        workflowEvents.Add(evt);
                    }

                    if (!hasBrokerMapping && !string.IsNullOrWhiteSpace(msg.SourceVenueCode))
                    {
                        var evt = new TradeWorkflowEvent
                        {
                            EventType = "WARNING",
                            FieldName = "BrokerCode",
                            OldValue = msg.SourceVenueCode,
                            NewValue = brokerCodeFromLookup ?? string.Empty,
                            Description =
                                $"Ingen broker-mappning hittades för venue '{msg.SourceVenueCode}'. " +
                                "BrokerCode sätts till venue-koden.",
                            EventTimeUtc = DateTime.UtcNow,
                            InitiatorId = "VolbrokerFixAeParser"
                        };
                        workflowEvents.Add(evt);
                    }

                    if (!hasPortfolioMapping && !string.IsNullOrWhiteSpace(currencyPair))
                    {
                        var evt = new TradeWorkflowEvent
                        {
                            EventType = "WARNING",
                            FieldName = "PortfolioMx3",
                            OldValue = currencyPair,
                            NewValue = string.Empty,
                            Description =
                                $"Ingen MX3-portfölj hittades i ccypairportfoliorule för valutapar {currencyPair} " +
                                $"och produkttyp {productType}. PortfolioMx3 lämnas tom.",
                            EventTimeUtc = DateTime.UtcNow,
                            InitiatorId = "VolbrokerFixAeParser"
                        };
                        workflowEvents.Add(evt);
                    }

                    // CalypsoBook-warning: bara för Spot/Fwd med känd TraderId
                    if ((productType == ProductType.Spot || productType == ProductType.Fwd)
                        && !string.IsNullOrWhiteSpace(trade.TraderId)
                        && !hasCalypsoBookMapping)
                    {
                        var evt = new TradeWorkflowEvent
                        {
                            EventType = "WARNING",
                            FieldName = "CalypsoBook",
                            OldValue = trade.TraderId,
                            NewValue = string.Empty,
                            Description =
                                $"Ingen CalypsoBook-mappning hittades i stp_calypso_book_user för TraderId '{trade.TraderId}'. " +
                                "CalypsoBook lämnas tom.",
                            EventTimeUtc = DateTime.UtcNow,
                            InitiatorId = "VolbrokerFixAeParser"
                        };
                        workflowEvents.Add(evt);
                    }

                    parsedTrades.Add(new ParsedTradeResult
                    {
                        Trade = trade,
                        SystemLinks = new List<TradeSystemLink>(),
                        WorkflowEvents = workflowEvents
                    });
                }

                return ParseResult.Ok(parsedTrades);
            }
            catch (Exception ex)
            {
                return ParseResult.Failed("Fel vid parsning av Volbroker AE: " + ex.Message);
            }
        }




        #region Helpers

        /// <summary>
        /// Extraherar sides (NoSides-gruppen) från AE-meddelandet och markerar
        /// vilken side som är Swedbanks egen (448=SWEDSTK, 452=1).
        /// </summary>
        /// <param name="tags">Alla FIX-taggar i AE-meddelandet.</param>
        /// <returns>Lista med SideInfo.</returns>
        private static List<SideInfo> ExtractSides(List<FixTag> tags)
        {
            var result = new List<SideInfo>();
            SideInfo currentSide = null;

            bool insideSides = false;
            string lastPartyId = null;

            foreach (var tag in tags)
            {
                if (tag.Tag == 552)
                {
                    // NoSides – efter detta kommer side-grupper
                    insideSides = true;
                    continue;
                }

                if (!insideSides)
                    continue;

                if (tag.Tag == 54)
                {
                    // Start på en ny side
                    if (currentSide != null)
                        result.Add(currentSide);

                    currentSide = new SideInfo
                    {
                        Side = tag.Value,
                        IsOwnSide = false
                    };

                    lastPartyId = null;
                    continue;
                }

                if (currentSide == null)
                    continue;

                if (tag.Tag == 448)
                {
                    // PartyID inom denna side
                    lastPartyId = tag.Value;
                    continue;
                }

                if (tag.Tag == 452)
                {
                    // PartyRole – vi markerar vår egen side om vi hittar SWEDSTK med role=1
                    if (tag.Value == "1" &&
                        !string.IsNullOrWhiteSpace(lastPartyId) &&
                        string.Equals(lastPartyId, "SWEDSTK", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSide.IsOwnSide = true;
                    }
                }
            }

            if (currentSide != null)
                result.Add(currentSide);

            return result;
        }


        /// <summary>
        /// Bygger upp en lista av PartyInfo baserat på 448/452/523-taggarna
        /// i AE-meddelandet (PARTIES-gruppen).
        /// </summary>
        private static List<PartyInfo> ExtractParties(List<FixTag> tags)
        {
            var result = new List<PartyInfo>();
            PartyInfo current = null;

            foreach (var tag in tags)
            {
                switch (tag.Tag)
                {
                    case 448: // PartyID
                        // Starta en ny party-rad, pusha den föregående om den finns
                        if (current != null)
                            result.Add(current);

                        current = new PartyInfo
                        {
                            PartyId = tag.Value
                        };
                        break;

                    case 452: // PartyRole
                        if (current != null && int.TryParse(tag.Value, out var role))
                            current.PartyRole = role;
                        break;

                    case 523: // PartySubID (t.ex. trader-kortkod)
                        if (current != null && !string.IsNullOrWhiteSpace(tag.Value))
                            current.TraderShortCode = tag.Value;
                        break;
                }
            }

            if (current != null)
                result.Add(current);

            return result;
        }

        /// <summary>
        /// Försöker ta fram motpartskoden (externt id, t.ex. "DB") ur PARTY-listan.
        /// Regel:
        /// 1) Ta sista party med role = 1 (Client/Customer) där PartyId != vårt eget ("SWEDSTK").
        /// 2) Om ingen hittas: ta sista party med role = 12 (Executing Firm).
        /// 3) Annars: returnera null.
        /// </summary>
        private static string ResolveCounterpartyCodeFromParties(List<PartyInfo> parties)
        {
            if (parties == null || parties.Count == 0)
                return null;

            const string ownFirmId = "SWEDSTK"; // v1: hårdkodat, kan tas från config senare

            PartyInfo selected = null;

            // 1) Sista party med role=1 och inte vår egen kod
            for (int i = parties.Count - 1; i >= 0; i--)
            {
                var p = parties[i];
                if (p.PartyRole == 1 &&
                    !string.IsNullOrWhiteSpace(p.PartyId) &&
                    !string.Equals(p.PartyId, ownFirmId, StringComparison.OrdinalIgnoreCase))
                {
                    selected = p;
                    break;
                }
            }

            // 2) Om ingen hittades, ta sista party med role=12 (Executing Firm)
            if (selected == null)
            {
                for (int i = parties.Count - 1; i >= 0; i--)
                {
                    var p = parties[i];
                    if (p.PartyRole == 12 && !string.IsNullOrWhiteSpace(p.PartyId))
                    {
                        selected = p;
                        break;
                    }
                }
            }

            return selected?.PartyId;
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
        /// Delar upp hela FIX-taglistan i en lista av taggrupper,
        /// en per leg, baserat på tag 600 (LegSymbol).
        /// Avslutar legs-gruppen när vi når sides/party-delen (tag 552).
        /// </summary>
        /// <param name="tags">Alla FIX-taggar i AE-meddelandet.</param>
        /// <returns>Lista med en tag-lista per leg.</returns>
        private static List<List<FixTag>> ExtractLegTagGroups(List<FixTag> tags)
        {
            var result = new List<List<FixTag>>();
            List<FixTag> current = null;

            foreach (var tag in tags)
            {
                if (tag.Tag == 600)
                {
                    // Ny leg börjar
                    if (current != null && current.Count > 0)
                        result.Add(current);

                    current = new List<FixTag> { tag };
                }
                else
                {
                    if (current == null)
                        continue;

                    // När vi når sides/party-delen slutar vi samla legs
                    if (tag.Tag == 552)
                    {
                        if (current.Count > 0)
                            result.Add(current);

                        current = null;
                        break;
                    }

                    current.Add(tag);
                }
            }

            if (current != null && current.Count > 0)
                result.Add(current);

            return result;
        }


        /// <summary>
        /// Mappar en FixAeLeg till ProductType baserat på SecurityType (tag 609).
        /// OPT → OptionVanilla, FWD → Fwd.
        /// </summary>
        /// <param name="leg">Leg-data extraherad från AE.</param>
        /// <returns>ProductType.OptionVanilla eller ProductType.Fwd.</returns>
        private static ProductType MapProductTypeForLeg(FixAeLeg leg)
        {
            var secType = leg.SecurityType != null
                ? leg.SecurityType.Trim().ToUpperInvariant()
                : string.Empty;

            if (secType == "OPT")
                return ProductType.OptionVanilla;

            if (secType == "FWD")
                return ProductType.Fwd;

            // Fallback: behandla som vanilla option
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
        /// Försöker extrahera vår interna traderkod (short code) från PARTY-listan.
        /// Regel (Volbroker):
        /// - Hitta en party-rad med PartyRole (452) = 122 (Trader).
        /// - Använd TraderShortCode (523) om den finns, annars PartyId (448).
        /// Returnerar null om ingen trader hittas.
        /// </summary>
        /// <param name="parties">Lista av PartyInfo extraherad från AE.</param>
        /// <returns>Trader short code, t.ex. "FORSPE", eller null.</returns>
        private static string ExtractTraderShortCodeFromParties(List<PartyInfo> parties)
        {
            if (parties == null || parties.Count == 0)
                return null;

            foreach (var p in parties)
            {
                if (p.PartyRole == 122)
                {
                    if (!string.IsNullOrWhiteSpace(p.TraderShortCode))
                        return p.TraderShortCode;

                    if (!string.IsNullOrWhiteSpace(p.PartyId))
                        return p.PartyId;
                }
            }

            return null;
        }


        #endregion

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
