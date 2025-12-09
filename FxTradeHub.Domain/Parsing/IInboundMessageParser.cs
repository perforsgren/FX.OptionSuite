using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Parsing
{
    /// <summary>
    /// Definierar en parser som kan hantera ett visst typ av inkommande meddelande.
    /// Implementationer konverterar en MessageIn-post till en eller flera normaliserade trades.
    /// </summary>
    public interface IInboundMessageParser
    {
        /// <summary>
        /// Anger om parsern kan hantera det angivna meddelandet baserat på
        /// t.ex. SourceType, SourceVenueCode och FixMsgType.
        /// </summary>
        /// <param name="message">Inkommande meddelande från MessageIn-tabellen.</param>
        /// <returns>
        /// true om parsern kan hantera meddelandet, annars false.
        /// </returns>
        bool CanParse(MessageIn message);

        /// <summary>
        /// Försöker parsa det angivna meddelandet till en eller flera trades.
        /// Vid lyckat resultat returneras ett ParseResult med Success=true och en lista
        /// med ParsedTradeResult (option, hedge, etc). Vid fel returneras Success=false
        /// och ett beskrivande felmeddelande.
        /// </summary>
        /// <param name="message">Meddelandet som ska parsas.</param>
        /// <returns>Resultatobjekt som beskriver utfallet av parsningen.</returns>
        ParseResult Parse(MessageIn message);
    }
}
