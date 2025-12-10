using System;
using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Interfaces
{

    /// <summary>
    /// Repository-interface för STP-relaterade lookups.
    /// Används av tjänstelagret för att slå upp expiry cut,
    /// Calypso-bok per trader och broker-mapping.
    /// </summary>
    public interface IStpLookupRepository
    {
        /// <summary>
        /// Hämtar expiry cut-regel för ett givet valutapar.
        /// Returnerar null om ingen aktiv regel finns.
        /// </summary>
        /// <param name="currencyPair">Valutapar, t.ex. EURSEK.</param>
        /// <returns>ExpiryCutCcyRule eller null.</returns>
        ExpiryCutCcyRule GetExpiryCutByCurrencyPair(string currencyPair);

        /// <summary>
        /// Hämtar Calypso-bok-regel för en given trader.
        /// Returnerar null om ingen aktiv regel finns.
        /// </summary>
        /// <param name="traderId">TraderId / användar-id.</param>
        /// <returns>CalypsoBookUserRule eller null.</returns>
        CalypsoBookUserRule GetCalypsoBookByTraderId(string traderId);

        /// <summary>
        /// Hämtar broker-mapping för en given venue och extern brokerkod.
        /// Returnerar null om ingen aktiv mappning finns.
        /// </summary>
        /// <param name="sourceVenueCode">Venue/källa, t.ex. VOLBROKER.</param>
        /// <param name="externalBrokerCode">Extern brokerkod från meddelandet.</param>
        /// <returns>BrokerMapping eller null.</returns>
        BrokerMapping GetBrokerMapping(string sourceVenueCode, string externalBrokerCode);

        /// <summary>
        /// Hämtar MX3-portföljkod för ett visst system, valutapar och produkttyp
        /// från regeltabellen ccypairportfoliorule.
        /// Returnerar null om ingen aktiv regel finns.
        /// </summary>
        /// <param name="systemCode">Systemkod, t.ex. "MX3".</param>
        /// <param name="currencyPair">Valutapar, t.ex. "EURSEK".</param>
        /// <param name="productType">
        /// Produkttyp, t.ex. "OPTION_VANILLA". Kan vara null för regler utan produkttyp.
        /// </param>
        /// <returns>PortfolioCode eller null.</returns>
        string GetPortfolioCode(string systemCode, string currencyPair, string productType);

        /// <summary>
        /// Försöker mappa ett externt motpartsnamn/kod till ett internt CounterpartyCode
        /// via tabellen trade_stp.counterpartynamepattern.
        /// Returnerar null om ingen aktiv mappning hittas.
        /// </summary>
        /// <param name="sourceType">Källa, t.ex. "FIX".</param>
        /// <param name="sourceVenueCode">Venue, t.ex. "VOLBROKER".</param>
        /// <param name="externalName">Externt motparts-id/namn, t.ex. "DB".</param>
        /// <returns>Internt CounterpartyCode eller null.</returns>
        string ResolveCounterpartyCode(string sourceType, string sourceVenueCode, string externalName);

        /// <summary>
        /// Hämtar trader-routinginformation för en given venue-traderkod.
        /// Bygger på tabellen trade_stp.stp_venue_trader_mapping och userprofile.
        /// Returnerar null om ingen aktiv mappning hittas eller om användaren saknas.
        /// </summary>
        /// <param name="sourceVenueCode">Venue/källa, t.ex. "VOLBROKER".</param>
        /// <param name="venueTraderCode">Traderkod från AE, t.ex. "FORSPE".</param>
        /// <returns>TraderRoutingInfo eller null.</returns>
        TraderRoutingInfo GetTraderRoutingInfo(string sourceVenueCode, string venueTraderCode);
    }
}
