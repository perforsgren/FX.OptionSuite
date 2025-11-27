using System;
using FxTradeHub.Data.MySql.Repositories;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Stp.ConsoleTest
{
    /// <summary>
    /// Enkel konsolapp för att manuellt testa STP-repository:
    /// - InsertTrade
    /// - InsertTradeSystemLink
    /// - InsertWorkflowEvent
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Startpunkt för konsolappen.
        /// Kör först ett enkelt insert-test och därefter ett read-test.
        /// </summary>
        /// <param name="args">Kommandoradsargument (används inte).</param>
        private static void Main(string[] args)
        {
            try
            {
                RunSimpleStpInsertTest();

                Console.WriteLine();
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine();

                RunReadSummaryTest();

                Console.WriteLine();
                Console.WriteLine("Klar. Tryck valfri tangent för att avsluta.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ett fel inträffade:");
                Console.WriteLine(ex.ToString());
                Console.WriteLine();
                Console.WriteLine("Tryck valfri tangent för att avsluta.");
            }

            Console.ReadKey();
        }

        private static void RunReadSummaryTestOLD()
        {
            Console.WriteLine("== STP Read test ==");

            // 1) Connection string mot din dev-databas.
            string username = "fxopt";
            string password = "fxopt987";

            // Rekommenderad MySQL-sträng (ersätt "fxvol" med din faktiska DB, t.ex. "fxoptions" om det är den du använder)
            var connectionString =
                "Server=srv78506;Port=3306;Database=trade_stp;" +
                "User Id=" + username + ";" +
                "Password=" + password + ";" +
                "Connection Timeout=15;TreatTinyAsBoolean=false;";

            var repo = new MySqlStpRepository(connectionString);

            var rows = repo.GetAllTradeSystemSummaries();

            Console.WriteLine("Antal rader: " + rows.Count);
            foreach (var row in rows)
            {
                Console.WriteLine(
                    $"{row.StpTradeId} {row.TradeId} {row.ProductType} {row.CurrencyPair} " +
                    $"{row.BuySell} {row.Notional} {row.NotionalCurrency} " +
                    $"{row.SystemCode} {row.Status}");
            }
        }

        /// <summary>
        /// Enkel read-test som hämtar alla TradeSystemSummary-rader från databasen
        /// via MySqlStpRepository.GetAllTradeSystemSummaries() och skriver ut en
        /// kort rad per (trade, system).
        /// </summary>
        private static void RunReadSummaryTest()
        {
            Console.WriteLine("== STP Read test ==");
            Console.WriteLine();

            // 1) Connection string mot din dev-databas.
            string username = "fxopt";
            string password = "fxopt987";

            // Rekommenderad MySQL-sträng (ersätt "fxvol" med din faktiska DB, t.ex. "fxoptions" om det är den du använder)
            var connectionString =
                "Server=srv78506;Port=3306;Database=trade_stp;" +
                "User Id=" + username + ";" +
                "Password=" + password + ";" +
                "Connection Timeout=15;TreatTinyAsBoolean=false;";

            var repo = new MySqlStpRepository(connectionString);

            var summaries = repo.GetAllTradeSystemSummaries();

            Console.WriteLine("Antal (trade, system)-rader: " + summaries.Count);
            Console.WriteLine();

            foreach (var s in summaries)
            {
                // Exempeloutput: 2 TEST-1234 SPOT EURSEK BUY 1000000 EUR MX3 NEW
                Console.WriteLine(
                    "{0} {1} {2} {3} {4} {5} {6} {7} {8}",
                    s.StpTradeId,
                    s.TradeId,
                    s.ProductType,
                    s.CurrencyPair,
                    s.BuySell,
                    s.Notional,
                    s.NotionalCurrency,
                    s.SystemCode,
                    s.Status);
            }
        }



        // ------------------------------------------------------------
        // Tests / Manual STP tests
        // ------------------------------------------------------------

        /// <summary>
        /// Enkel manuell test av STP-repository:
        /// - Skapar en Trade
        /// - Skapar en TradeSystemLink
        /// - Skapar en TradeWorkflowEvent
        /// Kontrollera sedan raderna i databasen (trade, tradesystemlink, tradeworkflowevent).
        /// </summary>
        private static void RunSimpleStpInsertTest()
        {
            // 1) Connection string mot din dev-databas.
            string username = "fxopt";
            string password = "fxopt987";

            // Rekommenderad MySQL-sträng (ersätt "fxvol" med din faktiska DB, t.ex. "fxoptions" om det är den du använder)
            var connectionString =
                "Server=srv78506;Port=3306;Database=trade_stp;" +
                "User Id=" + username + ";" +
                "Password=" + password + ";" +
                "Connection Timeout=15;TreatTinyAsBoolean=false;";

            var repo = new MySqlStpRepository(connectionString);

            Console.WriteLine("== STP Insert test ==");
            Console.WriteLine();

            // 2) Skapa en minimal Trade
            var trade = new Trade
            {
                TradeId = "TEST-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ProductType = ProductType.Spot,
                SourceType = "TEST",
                SourceVenueCode = "MANUAL",
                CounterpartyCode = "TESTCP",
                TraderId = "TESTTRDR",
                CurrencyPair = "EURSEK",
                TradeDate = DateTime.Today,
                ExecutionTimeUtc = DateTime.UtcNow,
                BuySell = "BUY",
                Notional = 1_000_000m,
                NotionalCurrency = "EUR",
                SettlementDate = DateTime.Today.AddDays(2),
                IsDeleted = false,
                // PortfolioMx3 kan sättas om du vill att det ska synas i MX3-flödet direkt.
                // PortfolioMx3 = "TESTPORT"
            };

            long stpTradeId = repo.InsertTrade(trade);
            Console.WriteLine("Trade inserted, StpTradeId = " + stpTradeId);

            // 3) Skapa en TradeSystemLink kopplad till samma trade
            var link = new TradeSystemLink
            {
                StpTradeId = stpTradeId,
                SystemCode = SystemCode.Mx3,
                ExternalTradeId = string.Empty,          // ingen extern id ännu
                Status = TradeSystemStatus.New,
                ErrorCode = string.Empty,
                ErrorMessage = string.Empty,
                PortfolioCode = "TESTPORT",
                BookFlag = true,
                StpMode = "AUTO",
                ImportedBy = Environment.UserName,
                BookedBy = string.Empty,
                FirstBookedUtc = null,
                LastBookedUtc = null,
                StpFlag = true,
                CreatedUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow,
                IsDeleted = false
            };

            long systemLinkId = repo.InsertTradeSystemLink(link);
            Console.WriteLine("TradeSystemLink inserted, SystemLinkId = " + systemLinkId);

            // 4) Skapa en TradeWorkflowEvent
            var evt = new TradeWorkflowEvent
            {
                StpTradeId = stpTradeId,
                SystemCode = SystemCode.Mx3,
                EventType = "TEST_INSERT",
                Description = "Manual test insert from FxTradeHub.Stp.ConsoleTest",
                FieldName = "PortfolioMx3",
                OldValue = null,
                NewValue = "TESTPORT",
                EventTimeUtc = DateTime.UtcNow,
                InitiatorId = Environment.UserName
            };

            long workflowEventId = repo.InsertWorkflowEvent(evt);
            Console.WriteLine("TradeWorkflowEvent inserted, WorkflowEventId = " + workflowEventId);
        }
    }
}
