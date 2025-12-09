using System;
using FxTradeHub.Contracts.Dtos;
using System.Collections.Generic;
using FxTradeHub.Data.MySql.Repositories;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Services;

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
        /// Connection string mot trade_stp-databasen som används av både
        /// MySqlStpRepository och MySqlStpLookupRepository i testerna.
        /// OBS: Uppdatera värdet så det matchar din miljö.
        /// </summary>
        private const string TradeStpConnectionString =
            "Server=Server=srv78506;Port=3306;Database=trade_stp;User Id=fxopt;Password=fxopt987;Connection Timeout=15;TreatTinyAsBoolean=false;";


        /// <summary>
        /// Startpunkt för STP-konsoltesterna:
        /// - Insert-test (Trade + SystemLink + WorkflowEvent)
        /// - Read-test (blotter-läsning)
        /// - Lookup-test (expiry cut, Calypso-bok, broker-mapping)
        /// </summary>
        /// <param name="args">Kommandoradsargument (används ej).</param>
        private static void Main(string[] args)
        {
            Console.WriteLine("== STP Insert test ==");
            try
            {
                RunSimpleStpInsertTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ett fel inträffade i insert-testet:");
                Console.WriteLine(ex.ToString());
            }

            try
            {
                RunBlotterReadTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ett fel inträffade i read-testet:");
                Console.WriteLine(ex.ToString());
            }

            try
            {
                RunLookupTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ett fel inträffade i lookup-testet:");
                Console.WriteLine(ex.ToString());
            }

            Console.WriteLine();
            Console.WriteLine("Klar. Tryck valfri tangent för att avsluta.");
            Console.ReadKey();
        }


        /// <summary>
        /// Skapar ett MySqlStpRepository med connectionstring mot trade_stp.
        /// </summary>
        private static IStpRepository CreateRepository()
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

            return new MySqlStpRepository(connectionString);
        }

        /// <summary>
        /// Skapar ett IStpLookupRepository baserat på MySqlStpLookupRepository
        /// med samma connection string som övriga STP-tester.
        /// </summary>
        /// <returns>En instans av IStpLookupRepository.</returns>
        private static IStpLookupRepository CreateLookupRepository()
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


            return new MySqlStpLookupRepository(connectionString);
        }

        /// <summary>
        /// Enkel test av lookup-repository med:
        /// - expiry cut per valutapar,
        /// - Calypso-bok per trader,
        /// - broker-mapping per (venue, extern brokerkod).
        /// Skriver ut resultat till konsolen.
        /// </summary>
        private static void RunLookupTest()
        {
            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("== STP Lookup test ==");
            Console.WriteLine();

            var lookupRepository = CreateLookupRepository();

            // 1) Expiry cut för ett valutapar
            var expiryRule = lookupRepository.GetExpiryCutByCurrencyPair("EURSEK");

            if (expiryRule != null && expiryRule.IsActive)
            {
                Console.WriteLine(
                    "Expiry cut för {0}: {1} (Kommentar: {2})",
                    expiryRule.CurrencyPair,
                    expiryRule.ExpiryCut,
                    string.IsNullOrEmpty(expiryRule.Comment) ? "-" : expiryRule.Comment);
            }
            else
            {
                Console.WriteLine("Ingen aktiv expiry cut-regel hittades för EURSEK.");
            }

            Console.WriteLine();

            // 2) Calypso-bok per trader
            var calypsoRule = lookupRepository.GetCalypsoBookByTraderId("P901PEF");

            if (calypsoRule != null && calypsoRule.IsActive)
            {
                Console.WriteLine(
                    "Calypso-bok för trader {0}: {1} (Kommentar: {2})",
                    calypsoRule.TraderId,
                    calypsoRule.CalypsoBook,
                    string.IsNullOrEmpty(calypsoRule.Comment) ? "-" : calypsoRule.Comment);
            }
            else
            {
                Console.WriteLine("Ingen aktiv Calypso-bok-regel hittades för trader PEF.");
            }

            Console.WriteLine();

            // 3) Broker-mapping för (venue, extern brokerkod)
            var brokerMapping = lookupRepository.GetBrokerMapping("VOLBROKER", "TRADITION");

            if (brokerMapping != null && brokerMapping.IsActive)
            {
                Console.WriteLine(
                    "Broker-mapping: SourceVenue={0}, External={1}, Normalized={2} (Kommentar: {3})",
                    brokerMapping.SourceVenueCode,
                    brokerMapping.ExternalBrokerCode,
                    brokerMapping.NormalizedBrokerCode,
                    string.IsNullOrEmpty(brokerMapping.Comment) ? "-" : brokerMapping.Comment);
            }
            else
            {
                Console.WriteLine("Ingen aktiv broker-mapping hittades för VOLBROKER / TRADITION.");
            }

            Console.WriteLine();
            Console.WriteLine("Klar med STP Lookup test. Tryck valfri tangent för att fortsätta.");
        }


        /// <summary>
        /// Enkel läs-test för blottern:
        /// - Skapar ett BlotterFilter för senaste dagarna.
        /// - Använder BlotterReadService för att hämta blotterrader.
        /// - Skriver ut några nyckelfält till konsolen.
        /// </summary>
        private static void RunBlotterReadTest()
        {
            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("== STP Read test (Blotter) ==");
            Console.WriteLine();

            // Enkelt filter: senaste 7 dagarna, max 100 rader.
            var filter = new BlotterFilter
            {
                FromTradeDate = DateTime.Today.AddDays(-7),
                ToTradeDate = DateTime.Today.AddDays(1),
                ProductType = null,        // alla produkter
                SourceType = null,         // alla källor
                CounterpartyCode = null,   // alla motparter
                TraderId = null,           // alla traders
                MaxRows = 100
            };

            // Här återanvänder vi samma sätt att skapa repository
            // som du använder i RunSimpleStpInsertTest().
            IStpRepository repository = CreateRepository();

            // Skapa blotter-lässervice
            var blotterService = new BlotterReadService(repository);

            // Hämta blotterrader
            List<BlotterTradeRow> rows = blotterService.GetBlotterTrades(filter);

            Console.WriteLine("Antal (trade, system)-rader: " + rows.Count);
            Console.WriteLine();

            int index = 1;
            foreach (var row in rows)
            {
                // Vi håller utskriften enkel men informativ:
                // TradeId, produkt, ccypair, riktning, belopp, system, status, portfolio.
                Console.WriteLine(
                    string.Format(
                        "{0} {1} {2} {3} {4} {5:0.##} {6} {7} {8} {9}",
                        index,
                        row.TradeId,
                        row.ProductType,
                        row.CcyPair,
                        row.BuySell,
                        row.Notional,
                        row.NotionalCcy,
                        row.SystemCode,
                        row.SystemStatus,
                        GetPortfolioForSystem(row)));

                index++;
            }

            Console.WriteLine();
            Console.WriteLine("Klar med STP Read test. Tryck valfri tangent för att fortsätta.");
        }

        /// <summary>
        /// Returnerar en portfolio-sträng för utskrift beroende på system.
        /// - MX3 => PortfolioMx3
        /// - CALYPSO => CalypsoPortfolio
        /// - övriga => SystemPortfolioCode
        /// </summary>
        private static string GetPortfolioForSystem(BlotterTradeRow row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            if (string.Equals(row.SystemCode, "Mx3", StringComparison.OrdinalIgnoreCase))
            {
                return row.PortfolioMx3 ?? string.Empty;
            }

            if (string.Equals(row.SystemCode, "Calypso", StringComparison.OrdinalIgnoreCase))
            {
                return row.CalypsoPortfolio ?? string.Empty;
            }

            // fallback: mer generellt fält
            return row.SystemPortfolioCode ?? string.Empty;
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

            long workflowEventId = repo.InsertTradeWorkflowEvent(evt);
            Console.WriteLine("TradeWorkflowEvent inserted, WorkflowEventId = " + workflowEventId);
        }
    }
}
