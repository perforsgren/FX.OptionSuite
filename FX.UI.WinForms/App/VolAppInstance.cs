using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using FX.Core.Interfaces;
using FX.UI.WinForms.Features.VolManager;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Vol Manager som app-instans för Shell, med samma mönster som PricerAppInstance.
    /// - Exponerar en workspace-kontroll som View (meny + sessionsflikar).
    /// - Skapar nya vol-sessions via DI: VolManagerView + VolManagerPresenter (transienta).
    /// - Håller inga shell-detaljer; Shell hostar bara denna View.
    /// </summary>
    public sealed class VolAppInstance : IAppInstance
    {
        private readonly IServiceProvider _sp;
        private readonly VolWorkspaceControl _workspace;

        /// <summary>
        /// Skapar en VolAppInstance som Shell kan hosta i ett dokumentfönster.
        /// </summary>
        /// <param name="sp">DI-container med IVolRepository registrerad.</param>
        public VolAppInstance(IServiceProvider sp)
        {
            _sp = sp ?? throw new ArgumentNullException(nameof(sp));

            // SessionFactory: identiskt mönster som pricer – skapa presenter och vy via DI
            Func<int, VolSessionControl> factory = (idx) =>
            {
                var repo = _sp.GetRequiredService<IVolRepository>();
                var presenter = new VolManagerPresenter(repo);
                var view = new VolManagerView();
                view.SetPresenter(presenter);
                view.InitializeTabbedLayout(); // flikläge med pinned pairs

                var tabTitle = $"Vol Session {idx}";
                return new VolSessionControl(tabTitle, view, presenter);
            };

            _workspace = new VolWorkspaceControl(factory);
        }

        /// <summary>Titel som Shell använder på dokumentet (kan bytas i workspace om du vill).</summary>
        public string Title => "Volatility Manager";

        /// <summary>Den hostbara vyn: workspace med meny/toolbar + sessionsflikar.</summary>
        public UserControl View => _workspace;

        /// <summary>Kallas av Shell när dokumentet blir aktivt.</summary>
        public void OnActivated() => _workspace.OnActivated();

        /// <summary>Kallas av Shell när dokumentet blir inaktivt.</summary>
        public void OnDeactivated() => _workspace.OnDeactivated();

        /// <summary>Disposar workspace (stänger alla sessions och underliggande vy/presenter).</summary>
        public void Dispose()
        {
            _workspace?.Dispose();
        }
    }
}
