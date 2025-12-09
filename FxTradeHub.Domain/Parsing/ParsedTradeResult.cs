using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Parsing
{
    /// <summary>
    /// Representerar resultatet av parsing för en enskild trade.
    /// Innehåller själva traden samt tillhörande systemlänkar och workflow-event.
    /// </summary>
    public class ParsedTradeResult
    {
        /// <summary>
        /// Hämtar eller sätter den normaliserade traden.
        /// </summary>
        public Trade Trade { get; set; }

        /// <summary>
        /// Hämtar eller sätter listan med TradeSystemLink-poster som hör till traden.
        /// Kan vara null eller tom om inga systemlänkar ska skapas.
        /// </summary>
        public List<TradeSystemLink> SystemLinks { get; set; }

        /// <summary>
        /// Hämtar eller sätter listan med TradeWorkflowEvent-poster som hör till traden.
        /// Kan vara null eller tom om inga event ska skapas.
        /// </summary>
        public List<TradeWorkflowEvent> WorkflowEvents { get; set; }
    }

    /// <summary>
    /// Representerar utfallet av parsing för ett helt MessageIn-objekt.
    /// Kan innehålla en eller flera trades (t.ex. option + hedge) eller ett fel.
    /// </summary>
    public class ParseResult
    {
        /// <summary>
        /// Hämtar eller sätter en flagga som anger om parsing lyckades.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Hämtar eller sätter ett felmeddelande om parsing misslyckades.
        /// Tom eller null om Success = true.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hämtar eller sätter listan med trades som skapats från meddelandet.
        /// Kan innehålla en eller flera ParsedTradeResult-objekt.
        /// </summary>
        public List<ParsedTradeResult> Trades { get; set; }

        /// <summary>
        /// Skapar ett ParseResult som representerar ett misslyckat parse-försök.
        /// </summary>
        public static ParseResult Failed(string error)
        {
            return new ParseResult
            {
                Success = false,
                ErrorMessage = error,
                Trades = null
            };
        }

        /// <summary>
        /// Skapar ett ParseResult som representerar ett lyckat parse-försök
        /// med en eller flera trades.
        /// </summary>
        public static ParseResult Ok(List<ParsedTradeResult> trades)
        {
            return new ParseResult
            {
                Success = true,
                ErrorMessage = null,
                Trades = trades ?? new List<ParsedTradeResult>()
            };
        }
    }
}
