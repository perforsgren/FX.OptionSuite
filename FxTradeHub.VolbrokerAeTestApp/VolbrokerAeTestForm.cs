using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Windows.Forms;
using FxTradeHub.Data.MySql.Repositories;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Domain.Parsing;
using MySql.Data.MySqlClient;
using FxSharedConfig;

namespace FxTradeHub.VolbrokerAeTestApp
{
    /// <summary>
    /// Enkel WinForms-form för att testa Volbroker FIX AE-parsning från trade_stp.MessageIn.
    /// </summary>
    public sealed class VolbrokerAeTestForm : Form
    {
        // *** Justera denna connection string till ditt trade_stp-schema. ***

        private string TradeStpConnectionString = AppDbConfig.GetConnectionString("trade_stp");

        private readonly TextBox _txtMessageInId;
        private readonly TextBox _txtReceivedUtc;
        private readonly TextBox _txtSourceInfo;
        private readonly TextBox _txtRawPayload;
        private readonly Button _btnLoadLatest;
        private readonly Button _btnParse;
        private readonly TextBox _txtResult;
        private readonly DataGridView _gridTrades;

        private MessageIn _currentMessage;

        /// <summary>
        /// Skapar en ny instans av testform för Volbroker FIX AE.
        /// </summary>
        public VolbrokerAeTestForm()
        {
            Text = "Volbroker AE Test (MessageIn → Parse)";
            Width = 1200;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;

            // Kontroller – enkel layout med paneler.
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80
            };

            var lblMessageInId = new Label
            {
                Text = "MessageInId:",
                AutoSize = true,
                Location = new Point(10, 10)
            };

            _txtMessageInId = new TextBox
            {
                Location = new Point(110, 8),
                Width = 100,
                ReadOnly = true
            };

            var lblReceivedUtc = new Label
            {
                Text = "ReceivedUtc:",
                AutoSize = true,
                Location = new Point(230, 10)
            };

            _txtReceivedUtc = new TextBox
            {
                Location = new Point(320, 8),
                Width = 180,
                ReadOnly = true
            };

            var lblSourceInfo = new Label
            {
                Text = "Source:",
                AutoSize = true,
                Location = new Point(520, 10)
            };

            _txtSourceInfo = new TextBox
            {
                Location = new Point(580, 8),
                Width = 250,
                ReadOnly = true
            };

            _btnLoadLatest = new Button
            {
                Text = "Hämta senaste AE",
                Location = new Point(10, 40),
                Width = 180
            };
            _btnLoadLatest.Click += BtnLoadLatestOnClick;

            _btnParse = new Button
            {
                Text = "Parsa",
                Location = new Point(210, 40),
                Width = 100
            };
            _btnParse.Click += BtnParseOnClick;

            topPanel.Controls.Add(lblMessageInId);
            topPanel.Controls.Add(_txtMessageInId);
            topPanel.Controls.Add(lblReceivedUtc);
            topPanel.Controls.Add(_txtReceivedUtc);
            topPanel.Controls.Add(lblSourceInfo);
            topPanel.Controls.Add(_txtSourceInfo);
            topPanel.Controls.Add(_btnLoadLatest);
            topPanel.Controls.Add(_btnParse);

            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 220
            };

            _txtRawPayload = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                ReadOnly = true
            };

            splitMain.Panel1.Controls.Add(_txtRawPayload);

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 80
            };

            var lblResult = new Label
            {
                Text = "Resultat:",
                AutoSize = true,
                Location = new Point(10, 10)
            };

            _txtResult = new TextBox
            {
                Location = new Point(80, 8),
                Width = 600,
                ReadOnly = true
            };

            bottomPanel.Controls.Add(lblResult);
            bottomPanel.Controls.Add(_txtResult);

            _gridTrades = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
            };

            splitMain.Panel2.Controls.Add(_gridTrades);
            splitMain.Panel2.Controls.Add(bottomPanel);

            Controls.Add(splitMain);
            Controls.Add(topPanel);
        }

        /// <summary>
        /// Klickhändelse för knappen som hämtar senaste Volbroker AE från MessageIn-tabellen.
        /// </summary>
        private void BtnLoadLatestOnClick(object sender, EventArgs e)
        {
            try
            {
                _currentMessage = LoadLatestVolbrokerAeFromMessageIn();

                if (_currentMessage == null)
                {
                    _txtMessageInId.Text = string.Empty;
                    _txtReceivedUtc.Text = string.Empty;
                    _txtSourceInfo.Text = string.Empty;
                    _txtRawPayload.Text = string.Empty;
                    _txtResult.Text = "Ingen Volbroker AE hittades i MessageIn.";
                    _gridTrades.DataSource = null;
                    return;
                }

                _txtMessageInId.Text = _currentMessage.MessageInId.ToString();
                _txtReceivedUtc.Text = _currentMessage.ReceivedUtc.ToString("yyyy-MM-dd HH:mm:ss.fff");
                _txtSourceInfo.Text =
                    $"{_currentMessage.SourceType}/{_currentMessage.SourceVenueCode} MsgType={_currentMessage.FixMsgType}";
                _txtRawPayload.Text = _currentMessage.RawPayload;

                _txtResult.Text = "Meddelande hämtat. Klicka på 'Parsa' för att testa Volbroker AE-parsern.";
                _gridTrades.DataSource = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Fel vid hämtning av MessageIn: " + ex.Message,
                    "Fel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Klickhändelse för knappen som parsar nuvarande MessageIn med VolbrokerFixAeParser.
        /// </summary>
        private void BtnParseOnClick(object sender, EventArgs e)
        {
            if (_currentMessage == null)
            {
                _txtResult.Text = "Inget MessageIn hämtat ännu.";
                return;
            }

            try
            {
                // Använd samma connectionstring för lookup-repot.
                IStpLookupRepository lookupRepository =
                    new MySqlStpLookupRepository(TradeStpConnectionString);

                var parser = new VolbrokerFixAeParser(lookupRepository);

                var parseResult = parser.Parse(_currentMessage);

                if (!parseResult.Success)
                {
                    _txtResult.Text = "Parse FAILED: " + parseResult.ErrorMessage;
                    _gridTrades.DataSource = null;
                    return;
                }

                var trades = new List<Trade>();

                foreach (var parsed in parseResult.Trades)
                {
                    if (parsed.Trade != null)
                    {
                        trades.Add(parsed.Trade);
                    }
                }

                _txtResult.Text = $"Parse OK. Antal trades: {trades.Count}";

                // Visa relevanta kolumner i grid.
                _gridTrades.DataSource = trades;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Fel vid parsning: " + ex.Message,
                    "Fel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Hämtar senaste Volbroker FIX AE-meddelelse från trade_stp.MessageIn-tabellen.
        /// Filtrerar på SourceType=FIX, SourceVenueCode=VOLBROKER och FixMsgType=AE.
        /// </summary>
        /// <returns>Senaste MessageIn-raden eller null om ingen hittas.</returns>
        private MessageIn LoadLatestVolbrokerAeFromMessageIn()
        {
            const string sql = @"
SELECT
    MessageInId,
    SourceType,
    SourceVenueCode,
    SessionKey,
    ReceivedUtc,
    SourceTimestamp,
    IsAdmin,
    ParsedFlag,
    ParsedUtc,
    ParseError,
    RawPayload,
    EmailSubject,
    EmailFrom,
    EmailTo,
    FixMsgType,
    FixSeqNum,
    ExternalCounterpartyName,
    ExternalTradeKey
FROM trade_stp.MessageIn
WHERE SourceType = 'FIX'
  AND SourceVenueCode = 'VOLBROKER'
  AND FixMsgType = 'AE'
ORDER BY MessageInId DESC
LIMIT 1;"
            ;

            using (var connection = new MySqlConnection(TradeStpConnectionString))
            {
                connection.Open();

                using (var command = new MySqlCommand(sql, connection))
                {
                    using (var reader = command.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        var msg = new MessageIn
                        {
                            MessageInId = reader.GetInt64(reader.GetOrdinal("MessageInId")),
                            SourceType = reader["SourceType"] as string,
                            SourceVenueCode = reader["SourceVenueCode"] as string,
                            SessionKey = reader["SessionKey"] as string,
                            ReceivedUtc = reader.GetDateTime(reader.GetOrdinal("ReceivedUtc")),
                            SourceTimestamp = reader["SourceTimestamp"] == DBNull.Value
                                ? (DateTime?)null
                                : reader.GetDateTime(reader.GetOrdinal("SourceTimestamp")),
                            IsAdmin = Convert.ToInt32(reader["IsAdmin"]) == 1,
                            ParsedFlag = Convert.ToInt32(reader["ParsedFlag"]) == 1,
                            ParsedUtc = reader["ParsedUtc"] == DBNull.Value
                                ? (DateTime?)null
                                : reader.GetDateTime(reader.GetOrdinal("ParsedUtc")),
                            ParseError = reader["ParseError"] as string,
                            RawPayload = reader["RawPayload"] as string,
                            EmailSubject = reader["EmailSubject"] as string,
                            EmailFrom = reader["EmailFrom"] as string,
                            EmailTo = reader["EmailTo"] as string,
                            FixMsgType = reader["FixMsgType"] as string,
                            FixSeqNum = reader["FixSeqNum"] == DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(reader["FixSeqNum"]),
                            ExternalCounterpartyName = reader["ExternalCounterpartyName"] as string,
                            ExternalTradeKey = reader["ExternalTradeKey"] as string
                        };

                        return msg;
                    }
                }
            }
        }
    }
}
