using System;
using System.Collections.Generic;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Domain.Parsing;
using FxTradeHub.Domain.Repositories;

namespace FxTradeHub.Services.Parsing
{
    /// <summary>
    /// Koordinerar parsing av inkommande meddelanden genom att delegera till
    /// rätt parser och persistera normaliserade trades, systemlänkar och
    /// workflow-event. Stödjer flera trades per MessageIn (t.ex. option + hedge).
    /// </summary>
    public class MessageInParserOrchestrator : IMessageInParserOrchestrator
    {
        private readonly IMessageInRepository _messageRepo;
        private readonly IStpRepository _stpRepository;
        private readonly List<IInboundMessageParser> _parsers;

        /// <summary>
        /// Skapar en ny instans av MessageInParserOrchestrator med angivna
        /// repository-implementationer och parserlista.
        /// </summary>
        /// <param name="messageRepo">
        /// Repository för att läsa och uppdatera MessageIn-poster.
        /// </param>
        /// <param name="stpRepository">
        /// Repository för att persistera Trade, TradeSystemLink och TradeWorkflowEvent.
        /// </param>
        /// <param name="parsers">
        /// Lista med parser-implementationer som kan hantera olika typer av
        /// inkommande meddelanden (FIX, e-post, filer m.m.).
        /// </param>
        public MessageInParserOrchestrator(
            IMessageInRepository messageRepo,
            IStpRepository stpRepository,
            List<IInboundMessageParser> parsers)
        {
            _messageRepo = messageRepo ?? throw new ArgumentNullException(nameof(messageRepo));
            _stpRepository = stpRepository ?? throw new ArgumentNullException(nameof(stpRepository));
            _parsers = parsers ?? new List<IInboundMessageParser>();
        }

        /// <summary>
        /// Bearbetar en batch av oparsade meddelanden (ParsedFlag = false).
        /// Hämtar ett begränsat antal rader från MessageIn och försöker parsa dem.
        /// </summary>
        public void ProcessPendingMessages()
        {
            const int maxBatchSize = 100;

            var pending = _messageRepo.GetUnparsedMessages(maxBatchSize);
            foreach (var msg in pending)
            {
                ProcessMessage(msg.MessageInId);
            }
        }

        /// <summary>
        /// Bearbetar ett enskilt inkommande meddelande identifierat via MessageInId.
        /// Hämtar posten från MessageIn, väljer rätt parser och försöker skapa
        /// en eller flera trades med tillhörande systemlänkar och workflow-event.
        /// </summary>
        /// <param name="messageInId">Primärnyckeln för MessageIn-posten som ska bearbetas.</param>
        public void ProcessMessage(long messageInId)
        {
            var message = _messageRepo.GetById(messageInId);
            if (message == null)
                return;

            if (message.ParsedFlag)
                return;

            var parser = FindParser(message);
            if (parser == null)
            {
                MarkFailed(message, "No parser available for this message.");
                return;
            }

            ParseAndPersist(message, parser);
        }

        private IInboundMessageParser FindParser(MessageIn message)
        {
            foreach (var parser in _parsers)
            {
                if (parser.CanParse(message))
                    return parser;
            }

            return null;
        }

        private void ParseAndPersist(MessageIn source, IInboundMessageParser parser)
        {
            try
            {
                var result = parser.Parse(source);

                if (!result.Success)
                {
                    MarkFailed(source, result.ErrorMessage);
                    return;
                }

                if (result.Trades == null || result.Trades.Count == 0)
                {
                    MarkFailed(source, "Parser returned success but no trades.");
                    return;
                }

                foreach (var tradeBundle in result.Trades)
                {
                    if (tradeBundle == null || tradeBundle.Trade == null)
                    {
                        MarkFailed(source, "Parser returned a trade bundle without Trade.");
                        return;
                    }

                    // 1) Koppla Trade tillbaka till MessageIn och spara via IStpRepository
                    var trade = tradeBundle.Trade;
                    trade.MessageInId = source.MessageInId;

                    var stpTradeId = _stpRepository.InsertTrade(trade);

                    // 2) Spara TradeSystemLink-rader och sätt FK mot Trade.StpTradeId
                    if (tradeBundle.SystemLinks != null)
                    {
                        foreach (var link in tradeBundle.SystemLinks)
                        {
                            link.StpTradeId = stpTradeId;
                            _stpRepository.InsertTradeSystemLink(link);
                        }
                    }

                    // 3) Spara WorkflowEvents och sätt FK mot Trade.StpTradeId
                    if (tradeBundle.WorkflowEvents != null)
                    {
                        foreach (var evt in tradeBundle.WorkflowEvents)
                        {
                            evt.StpTradeId = stpTradeId;
                            _stpRepository.InsertTradeWorkflowEvent(evt);
                        }
                    }
                }

                // 4) Markera MessageIn som parsed OK om alla trades kunde persisteras
                MarkSuccess(source);
            }
            catch (Exception ex)
            {
                MarkFailed(source, ex.ToString());
            }
        }

        private void MarkSuccess(MessageIn msg)
        {
            msg.ParsedFlag = true;
            msg.ParsedUtc = DateTime.UtcNow;
            msg.ParseError = null;

            _messageRepo.UpdateParsingState(msg);
        }

        private void MarkFailed(MessageIn msg, string error)
        {
            msg.ParsedFlag = true;
            msg.ParsedUtc = DateTime.UtcNow;
            msg.ParseError = error;

            _messageRepo.UpdateParsingState(msg);
        }
    }
}
