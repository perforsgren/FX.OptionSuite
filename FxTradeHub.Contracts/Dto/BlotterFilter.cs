using System;

namespace FxTradeHub.Contracts.Dtos
{
    /// <summary>
    /// Filter för att ladda trades till blottern.
    /// Alla string-fält kan vara tomma eller null = ingen filtrering.
    /// </summary>
    public sealed class BlotterFilter
    {
        public DateTime? FromTradeDate { get; set; }
        public DateTime? ToTradeDate { get; set; }

        /// <summary>
        /// Om du vill filtrera på produkt (SPOT, FWD, OPTION_VANILLA etc).
        /// Tomt/null = alla.
        /// </summary>
        public string ProductType { get; set; }

        /// <summary>
        /// FIX, EMAIL, MANUAL ...
        /// Tomt/null = alla.
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// MX3-kortkod för motpart. Tomt/null = alla.
        /// </summary>
        public string CounterpartyCode { get; set; }

        /// <summary>
        /// TraderId om du vill filtrera på ”mina trades”.
        /// Tomt/null = alla.
        /// </summary>
        public string TraderId { get; set; }

        /// <summary>
        /// Kan användas för server-side ”mina trades”-logik.
        /// Kan vara null.
        /// </summary>
        public string CurrentUserId { get; set; }

        /// <summary>
        /// Övre gräns för antal rader. Null = ingen limit.
        /// </summary>
        public int? MaxRows { get; set; }

        public BlotterFilter()
        {
            ProductType = null;
            SourceType = null;
            CounterpartyCode = null;
            TraderId = null;
            CurrentUserId = null;
            MaxRows = null;
        }
    }
}
