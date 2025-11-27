//using System;
//using System.Data;
//using FxTradeHub.Domain.Entities;
//using FxTradeHub.Domain.Interfaces;
//using MySql.Data.MySqlClient;

//namespace FxTradeHub.Data.MySql.Repositories
//{
//    /// <summary>
//    /// MySQL-implementation av IStpRepository mot schemat trade_stp.
//    /// Den här klassen använder ren ADO.NET (MySqlConnection/MySqlCommand).
//    /// </summary>
//    public sealed class MySqlStpRepository : IStpRepository
//    {
//        private readonly string _connectionString;

//        /// <summary>
//        /// Skapa ett nytt repo med given connection string.
//        /// Exempel: "Server=srv78506;Port=3306;Database=trade_stp;User Id=fxopt;Password=...;SslMode=None;TreatTinyAsBoolean=false;"
//        /// </summary>
//        public MySqlStpRepository(string connectionString)
//        {
//            if (string.IsNullOrWhiteSpace(connectionString))
//                throw new ArgumentException("Connection string must not be empty.", "connectionString");

//            _connectionString = connectionString;
//        }

//        /// <summary>
//        /// Skapar en ny MySqlConnection. Används internt per operation.
//        /// </summary>
//        private MySqlConnection CreateConnection()
//        {
//            return new MySqlConnection(_connectionString);
//        }

//        public long InsertMessageIn(MessageIn message)
//        {
//            // Implementeras i steg B3.
//            throw new NotImplementedException("InsertMessageIn is not implemented yet.");
//        }

//        public long InsertTrade(Trade trade)
//        {
//            // Implementeras i steg B2.
//            throw new NotImplementedException("InsertTrade is not implemented yet.");
//        }

//        public long InsertTradeSystemLink(TradeSystemLink link)
//        {
//            // Implementeras i steg B3.
//            throw new NotImplementedException("InsertTradeSystemLink is not implemented yet.");
//        }

//        public long InsertWorkflowEvent(TradeWorkflowEvent evt)
//        {
//            // Implementeras i steg B3.
//            throw new NotImplementedException("InsertWorkflowEvent is not implemented yet.");
//        }
//    }
//}
