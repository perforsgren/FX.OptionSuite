// ============================================================
// SPRINT 1 – STEG 6: SurfaceView (UserControl)
// Varför:  Användaren kan mata in volnoder och trigga rebuild.
// Vad:     DataGridView med Tenor/Label/Vol + Rebuild-knapp.
// Klar när:Klick på Rebuild publicerar RebuildVolSurface och vi ser SurfaceUpdated.
// ============================================================
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using FX.Core.Interfaces;
using FX.Messages.Commands;
using FX.Messages.Dtos;

namespace FX.UI.WinForms
{
    public sealed class SurfaceView : UserControl
    {
        private readonly IMessageBus _bus;
        private IDisposable _subSurf, _subErr;
        TextBox txtPair;
        DataGridView grid;
        Button btnAdd, btnRebuild;
        Label lblStatus;

        public SurfaceView(IMessageBus bus)
        {
            _bus = bus;
            BuildUi();
            Wire();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _subSurf?.Dispose();
                _subErr?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildUi()
        {
            Dock = DockStyle.Fill;

            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            Controls.Add(top);
            top.Controls.Add(new Label { Text = "Pair (6):", AutoSize = true, Left = 8, Top = 12 });
            txtPair = new TextBox { Left = 70, Top = 8, Width = 100, Text = "EURSEK" };
            top.Controls.Add(txtPair);
            btnAdd = new Button { Text = "Add row", Left = 180, Top = 8, Width = 90 };
            btnRebuild = new Button { Text = "Rebuild Surface", Left = 280, Top = 8, Width = 140 };
            top.Controls.Add(btnAdd);
            top.Controls.Add(btnRebuild);
            lblStatus = new Label { Left = 430, Top = 12, AutoSize = true, ForeColor = Color.DimGray };
            top.Controls.Add(lblStatus);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            Controls.Add(grid);

            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tenor", Name = "Tenor" });  // t.ex. 1M
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Label", Name = "Label" });  // ATM/25D/10D/RR/BF
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Vol (dec)", Name = "Vol" }); // 0.10

            // startexempel
            grid.Rows.Add("1M", "ATM", "0.10");
        }

        private void Wire()
        {
            btnAdd.Click += (s, e) => grid.Rows.Add("1M", "ATM", "0.10");

            btnRebuild.Click += (s, e) =>
            {
                var cmd = new RebuildVolSurface { Pair6 = txtPair.Text.Trim(), Reason = "ui" };
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var tenor = (row.Cells["Tenor"].Value ?? "").ToString().Trim();
                    var label = (row.Cells["Label"].Value ?? "").ToString().Trim();
                    var volTxt = (row.Cells["Vol"].Value ?? "").ToString().Trim();
                    double vol;
                    if (!double.TryParse(volTxt, NumberStyles.Any, CultureInfo.InvariantCulture, out vol))
                        continue;
                    cmd.Nodes.Add(new VolNodeDto { Tenor = tenor, Label = label, Vol = vol });
                }
                lblStatus.Text = "Skickar RebuildVolSurface…";
                _bus.Publish(cmd);
            };

            _subSurf = _bus.Subscribe<FX.Messages.Events.SurfaceUpdated>(evt =>
            {
                BeginInvoke((Action)(() => lblStatus.Text = $"SurfaceUpdated: {evt.Pair6} -> {evt.SurfaceId}"));
            });

            _subErr = _bus.Subscribe<FX.Messages.Events.ErrorOccurred>(evt =>
            {
                BeginInvoke((Action)(() => lblStatus.Text = "ERROR: " + evt.Message));
            });
        }
    }
}
