// ============================================================
// SPRINT 1 – STEG 6: BlotterView (UserControl)
// Varför:  Visa bokade affärer genom att lyssna på TradeBooked-events.
// Vad:     Enkel DataGridView som fylls när event kommer.
// Klar när:Vyn kan ta emot TradeBooked och lägga till en rad.
// ============================================================
using System;
using System.Windows.Forms;
using FX.Core.Interfaces;

namespace FX.UI.WinForms
{
    public sealed class BlotterView : UserControl
    {
        private readonly IMessageBus _bus;
        private IDisposable _subBooked, _subErr;
        private DataGridView grid;

        public BlotterView(IMessageBus bus)
        {
            _bus = bus;
            BuildUi();
            Wire();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _subBooked?.Dispose();
                _subErr?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildUi()
        {
            Dock = DockStyle.Fill;
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            Controls.Add(grid);

            grid.Columns.Add("TradeId", "TradeId");
            grid.Columns.Add("Pair6", "Pair");
            grid.Columns.Add("TimeUtc", "Tid (UTC)");
        }

        private void Wire()
        {
            _subBooked = _bus.Subscribe<FX.Messages.Events.TradeBooked>(evt =>
            {
                BeginInvoke((Action)(() => grid.Rows.Insert(0, evt.TradeId, evt.Pair6, evt.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss"))));
            });

            _subErr = _bus.Subscribe<FX.Messages.Events.ErrorOccurred>(evt =>
            {
                // (valfritt) pop-up/toast
            });
        }
    }
}
