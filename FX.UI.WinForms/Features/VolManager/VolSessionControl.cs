using System;
using System.Windows.Forms;
using FX.UI.WinForms.Features.VolManager;

namespace FX.UI.WinForms
{
    /// <summary>
    /// En vol-session: wrapper runt VolManagerView + VolManagerPresenter,
    /// analogt med PricerSessionControl.
    /// - Yta: lägger in VolManagerView (flikläge är redan initierat i AppInstance).
    /// - Exponerar TabTitle som workspace visar i fliken (kan bytas via rename).
    /// </summary>
    public sealed class VolSessionControl : UserControl, IDisposable
    {
        private readonly VolManagerView _view;
        private readonly VolManagerPresenter _presenter;

        /// <summary>Den titel som visas på fliken i workspace.</summary>
        public string TabTitle { get; set; }

        /// <summary>
        /// Skapar en vol-session med given vy och presenter (skapade via DI i VolAppInstance).
        /// </summary>
        /// <param name="tabTitle">Förvald fliktitel, t.ex. "Vol Session 1".</param>
        /// <param name="view">VolManagerView som huserar UI.</param>
        /// <param name="presenter">VolManagerPresenter som läser från DB.</param>
        public VolSessionControl(string tabTitle, VolManagerView view, VolManagerPresenter presenter)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));

            TabTitle = tabTitle ?? "Vol Session";
            _view = view;
            _presenter = presenter;

            Dock = DockStyle.Fill;

            _view.Dock = DockStyle.Fill;
            Controls.Add(_view);
        }

        /// <summary>
        /// Frigör UI-resurser för vol-sessionen. I MVP-1 äger vi inte presenter med externa resurser,
        /// så vi Disposar endast vyn. (Presenter saknar Dispose och ska inte Disposas här.)
        /// </summary>
        /// <param name="disposing">True om kallad från Dispose(); false om från finalizer.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _view?.Dispose(); } catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
