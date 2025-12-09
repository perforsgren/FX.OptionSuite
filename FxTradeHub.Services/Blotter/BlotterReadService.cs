using System;
using System.Collections.Generic;
using FxTradeHub.Contracts.Dtos;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;
using FxTradeHub.Domain.Interfaces;

namespace FxTradeHub.Services
{
    /// <summary>
    /// Läs-tjänst mot STP-hubben för blottern.
    /// Använder IStpRepository för att hämta TradeSystemSummary
    /// och mappar dessa till BlotterTradeRow DTOs för UI-lagret.
    /// </summary>
    public sealed class BlotterReadService : IBlotterReadService
    {
        private readonly IStpRepository _repository;

        /// <summary>
        /// Skapar en ny instans av BlotterReadService.
        /// </summary>
        /// <param name="repository">
        /// STP-repository som kan läsa TradeSystemSummary-rader från databasen.
        /// </param>
        public BlotterReadService(IStpRepository repository)
        {
            if (repository == null) throw new ArgumentNullException("repository");

            _repository = repository;
        }

        /// <summary>
        /// Mappar en TradeSystemSummary (Trade + TradeSystemLink för ett system)
        /// till en flattenad BlotterTradeRow för UI-blottern.
        /// </summary>
        /// <param name="s">Sammanfattningsrad från STP-hubben.</param>
        /// <returns>En blotter-rad.</returns>
        private static BlotterTradeRow MapToBlotterRow(TradeSystemSummary s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            var row = new BlotterTradeRow();

            // ----------------------------------------------------
            // Trade-nivå
            // ----------------------------------------------------
            row.StpTradeId = s.StpTradeId;
            row.SystemLinkId = s.SystemLinkId;

            row.TradeId = s.TradeId;
            row.ProductType = s.ProductType.ToString().ToUpperInvariant();

            row.SourceType = s.SourceType;
            row.SourceVenueCode = s.SourceVenueCode;

            row.CounterpartyCode = s.CounterpartyCode;
            row.BrokerCode = s.BrokerCode;
            row.TraderId = s.TraderId;
            row.InvId = s.InvId;
            row.ReportingEntityId = s.ReportingEntityId;

            row.CcyPair = s.CurrencyPair;
            row.Mic = s.Mic;
            row.Isin = s.Isin;

            row.TradeDate = s.TradeDate;
            row.ExecutionTimeUtc = s.ExecutionTimeUtc;
            row.BuySell = s.BuySell;

            row.Notional = s.Notional;
            row.NotionalCcy = s.NotionalCurrency;

            row.SettlementDate = s.SettlementDate;
            row.NearSettlementDate = s.NearSettlementDate;

            row.IsNonDeliverable = s.IsNonDeliverable;
            row.FixingDate = s.FixingDate;
            row.SettlementCcy = s.SettlementCurrency;

            row.Uti = s.Uti;
            row.Tvtic = s.Tvtic;
            row.Margin = s.Margin;

            row.HedgeRate = s.HedgeRate;
            row.SpotRate = s.SpotRate;
            row.SwapPoints = s.SwapPoints;
            row.HedgeType = s.HedgeType;

            row.CallPut = s.CallPut;
            row.Strike = s.Strike;
            row.ExpiryDate = s.ExpiryDate;
            row.Cut = s.Cut;

            row.Premium = s.Premium;
            row.PremiumCcy = s.PremiumCurrency;
            row.PremiumDate = s.PremiumDate;

            // Primär MX3-portfölj på trade-nivå (fallback om systemlänk saknar portfölj)
            row.PortfolioMx3 = s.PortfolioMx3;

            row.TradeIsDeleted = s.TradeIsDeleted;
            row.TradeLastUpdatedUtc = s.TradeLastUpdatedUtc;
            row.TradeLastUpdatedBy = s.TradeLastUpdatedBy;

            // ----------------------------------------------------
            // SystemLink – generellt
            // ----------------------------------------------------
            row.SystemCode = s.SystemCode.ToString();
            row.Status = s.Status.ToString();
            row.SystemStatus = s.Status.ToString();

            row.SystemTradeId = s.SystemTradeId;
            row.ExternalTradeId = s.ExternalTradeId;

            row.SystemLastStatusUtc = s.SystemLastStatusUtc;
            row.SystemLastError = s.SystemLastError;

            row.SystemPortfolioCode = s.SystemPortfolioCode;
            row.BookFlag = s.BookFlag;
            row.StpMode = s.StpMode;
            row.ImportedBy = s.ImportedBy;
            row.BookedBy = s.BookedBy;
            row.FirstBookedUtc = s.FirstBookedUtc;
            row.LastBookedUtc = s.LastBookedUtc;
            row.StpFlag = s.StpFlag;
            row.SystemCreatedUtc = s.SystemCreatedUtc;
            row.SystemLinkIsDeleted = s.SystemLinkIsDeleted;

            row.LastChangeUtc = s.LastChangeUtc;

            // ----------------------------------------------------
            // System-specifika flattenade fält (MX3 / Calypso)
            // ----------------------------------------------------
            switch (s.SystemCode)
            {
                case SystemCode.Mx3:
                    row.Mx3TradeId = s.SystemTradeId;
                    row.Mx3Status = s.Status.ToString();

                    // PortfolioMx3: försök använda portfölj från systemlänken först.
                    if (!string.IsNullOrEmpty(s.SystemPortfolioCode))
                    {
                        row.PortfolioMx3 = s.SystemPortfolioCode;
                    }
                    // annars låt den ligga kvar som trade.PortfolioMx3 (s.PortfolioMx3)
                    break;

                case SystemCode.Calypso:
                    row.CalypsoTradeId = s.SystemTradeId;
                    row.CalypsoStatus = s.Status.ToString();
                    row.CalypsoPortfolio = s.SystemPortfolioCode;
                    break;

                case SystemCode.VolbrokerStp:
                    // ev. framtida flattenade Volbroker-fält
                    break;

                case SystemCode.Rtns:
                    // ev. framtida flattenade RTNS-fält
                    break;
            }

            // ----------------------------------------------------
            // UI-hjälp
            // ----------------------------------------------------
            row.CanEdit = CalculateCanEdit(s);

            return row;
        }



        /// <summary>
        /// Hämtar trades för blottern baserat på ett BlotterFilter.
        /// Använder IStpRepository för att läsa TradeSystemSummary
        /// och mappar varje rad till en BlotterTradeRow.
        /// </summary>
        /// <param name="filter">Filter för datum, produkt, källa, motpart, trader, max-rader etc.</param>
        /// <returns>Lista med blotter-rader (en rad per trade/system-kombination v1).</returns>
        public List<BlotterTradeRow> GetBlotterTrades(BlotterFilter filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));

            var summaries = _repository.GetTradeSystemSummaries(
                filter.FromTradeDate,
                filter.ToTradeDate,
                filter.ProductType,
                filter.SourceType,
                filter.CounterpartyCode,
                filter.TraderId,
                filter.MaxRows);

            var rows = new List<BlotterTradeRow>(summaries.Count);

            foreach (var summary in summaries)
            {
                var row = MapToBlotterRow(summary);
                rows.Add(row);
            }

            return rows;
        }


        /// <summary>
        /// Bestämmer om en trade-rad ska vara editerbar i blottern
        /// baserat på status och delete-flaggor.
        /// v1: icke-deleterad trade + länk och status i "tidigt" läge.
        /// </summary>
        private static bool CalculateCanEdit(TradeSystemSummary summary)
        {
            if (summary == null)
            {
                return false;
            }

            // Om trade eller länk är soft-deletad: aldrig editerbar.
            if (summary.TradeIsDeleted || summary.SystemLinkIsDeleted)
            {
                return false;
            }

            // Enkel v1-regel: tillåt edit i NEW/PENDING/ERROR.
            switch (summary.Status)
            {
                case TradeSystemStatus.New:
                case TradeSystemStatus.Pending:
                case TradeSystemStatus.Error:
                    return true;

                default:
                    return false;
            }
        }

    }
}
