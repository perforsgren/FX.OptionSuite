using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Pricer som app-instans för Shell.
    /// - Exponerar en workspace-kontroll som View (meny + toolbar + session-flikar).
    /// - Skapar nya sessioner via DI: LegacyPricerView + LegacyPricerPresenter (transienta).
    /// - Håller inga shell-detaljer; Shell hostar bara denna View.
    /// </summary>
    public sealed class PricerAppInstance : IAppInstance
    {
        private readonly IServiceProvider _sp;
        private readonly PricerWorkspaceControl _workspace;

        public PricerAppInstance(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
            _sp = serviceProvider;

            // Workspace får en fabrik som skapar en ny session vid behov.
            _workspace = new PricerWorkspaceControl(NewSessionFactory);
        }

        /// <summary>
        /// Fabrikmetod för nya sessioner (en per tab).
        /// Skapar vy + presenter via DI och kopplar dem.
        /// </summary>
        private PricerSessionControl NewSessionFactory(int index)
        {
            // 1) Skapa vy via DI (transient)
            var view = _sp.GetRequiredService<LegacyPricerView>();

            // 2) Skapa presenter med SAMMA vy injicerad i konstruktorn
            var presenter = ActivatorUtilities.CreateInstance<LegacyPricerPresenter>(_sp, view);

            // 3) Bygg session (wrapp:ar vy + presenter)
            var session = new PricerSessionControl("Session " + index, view, presenter);
            return session;
        }

        /// <summary>Titel för dokumentfliken i Shell.</summary>
        public string Title => "Pricer";

        /// <summary>Root-kontrollen som Shell hostar (hela appens UI).</summary>
        public UserControl View => _workspace;

        /// <summary>Kallas av Shell när dokumentet blir aktivt.</summary>
        public void OnActivated() => _workspace.OnActivated();

        /// <summary>Kallas av Shell när dokumentet blir inaktivt.</summary>
        public void OnDeactivated() => _workspace.OnDeactivated();

        /// <summary>Städar alla sessions (disposar vy/presenter per session).</summary>
        public void Dispose()
        {
            _workspace?.Dispose();
        }
    }
}
