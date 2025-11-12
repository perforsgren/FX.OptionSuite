using System;
using System.Reflection;
using System.Windows.Forms;

namespace FX.UI.WinForms
{
    /// <summary>
    /// En pricer-session: wrapp:ar LegacyPricerView + LegacyPricerPresenter.
    /// - Yta: lägger in LegacyPricerView.
    /// - Kommandorouting: försöker kalla presenter-metoder om de finns (reflektion, no-op annars).
    /// </summary>
    public sealed class PricerSessionControl : UserControl, IDisposable
    {
        private readonly LegacyPricerView _view;
        private readonly LegacyPricerPresenter _presenter;

        public Control ContentRoot => _view;

        public string TabTitle { get; private set; }

        public PricerSessionControl(string tabTitle, LegacyPricerView view, LegacyPricerPresenter presenter)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));

            TabTitle = tabTitle ?? "Session";
            _view = view;
            _presenter = presenter;

            Dock = DockStyle.Fill;

            _view.Dock = DockStyle.Fill;
            Controls.Add(_view);

            _view.PairChanged += (s, e) => UpdateTabTitleFromPair();
            UpdateTabTitleFromPair(); // sätt direkt vid skapande
        }

        private void UpdateTabTitleFromPair()
        {
            var p = _view.ReadPair6();
            if (!string.IsNullOrWhiteSpace(p))
            {
                TabTitle = p;
                if (Parent is TabPage tp)
                    tp.Text = TabTitle;
                else
                    (this.Parent as Control)?.Parent?.Refresh(); // best effort
            }
        }


        // ---- Kommandon (anropas från workspace-meny/toolbar) ----

        public void RepriceAll()
        {
            // Försök metodnamn som ofta finns; no-op om de saknas.
            TryCallPresenter("RepriceAll");
            TryCallPresenter("Reprice"); // fallback
        }

        public void AddLeg()
        {
            TryCallPresenter("AddLeg");
        }

        public void CloneActiveLeg()
        {
            TryCallPresenter("CloneActiveLeg");
            TryCallPresenter("CloneLeg"); // fallback
        }

        public void RemoveActiveLeg()
        {
            TryCallPresenter("RemoveActiveLeg");
            TryCallPresenter("RemoveLeg"); // fallback
        }

        // ---- Fokus/livscykel ----

        public void OnBecameActive()
        {
            // Här kan du t.ex. meddela presenter om aktiv session om metod finns
            TryCallPresenter("OnSessionActivated");
        }

        public void OnBecameInactive()
        {
            TryCallPresenter("OnSessionDeactivated");
        }

        // ---- Hjälpare ----

        private void TryCallPresenter(string methodName)
        {
            try
            {
                var mi = _presenter.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
                if (mi != null)
                    mi.Invoke(_presenter, null);
            }
            catch
            {
                // tyst no-op; lägg ev. logg om du vill
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _presenter?.Dispose(); } catch { /* best effort */ }
                try { _view?.Dispose(); } catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
