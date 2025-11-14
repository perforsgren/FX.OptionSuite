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
        //public VolSessionControlOLD(string tabTitle, VolManagerView view, VolManagerPresenter presenter)
        //{
        //    if (view == null) throw new ArgumentNullException(nameof(view));
        //    if (presenter == null) throw new ArgumentNullException(nameof(presenter));

        //    TabTitle = tabTitle ?? "Vol Session";
        //    _view = view;
        //    _presenter = presenter;

        //    Dock = DockStyle.Fill;

        //    _view.Dock = DockStyle.Fill;
        //    Controls.Add(_view);
        //}

        /// <summary>
        /// Skapar en vol-session (vy + presenter) och dockar vyn i denna kontroll.
        /// </summary>
        public VolSessionControl(VolManagerView view, VolManagerPresenter presenter, string tabTitle)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));

            TabTitle = string.IsNullOrWhiteSpace(tabTitle) ? "Vol Session" : tabTitle;

            _view = view;
            _presenter = presenter;

            // NY: koppla vy-instansen till presentern (krävs för RefreshPairAndBindAsync m.fl.)
            _presenter.AttachView(_view);

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

        /// <summary>
        /// Refreshar alla pinned par i den här sessionen och binder mot aktuell vy.
        /// force=true bypassar presenter-cachen (t.ex. F5).
        /// </summary>
        public async System.Threading.Tasks.Task RefreshAllAsync(bool force)
        {
            if (_presenter == null || _view == null) return;
            var pairs = _view.SnapshotPinnedPairs();
            await _presenter.RefreshPinnedAndBindAsync(pairs, force).ConfigureAwait(false);
        }

        /// <summary>
        /// Bör anropas när användaren byter vy-läge (Tabs &lt;→ Tiles) inom sessionen.
        /// Rebinder alla pinned mot det nya läget (från cache).
        /// </summary>
        public void NotifyViewModeChanged()
        {
            _presenter?.OnViewModeChanged();
        }

        /// <summary>
        /// Bör anropas när aktiv par-flik byts i Tabs-läget.
        /// </summary>
        public void NotifyActivePairTabChanged()
        {
            var sym = _view?.GetActivePairTabSymbolOrNull();
            if (!string.IsNullOrWhiteSpace(sym))
                _presenter?.OnActivePairTabChanged(sym);
        }

    }
}
