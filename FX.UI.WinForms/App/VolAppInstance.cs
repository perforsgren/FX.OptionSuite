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
        /// Skapar en Volatility Manager-appinstans. Bygger sessions via factory
        /// och injicerar både read- och write-repository till presentern.
        /// </summary>
        public VolAppInstance(IServiceProvider sp)
        {
            _sp = sp ?? throw new ArgumentNullException(nameof(sp));

            // SessionFactory: identiskt mönster som Pricer – skapa presenter och vy via DI
            Func<int, VolSessionControl> factory = (idx) =>
            {
                //var repo = _sp.GetRequiredService<IVolRepository>();
                var readRepo = _sp.GetRequiredService<IVolRepository>();
                var writeRepo = _sp.GetRequiredService<IVolWriteRepository>();
                var presenter = new VolManagerPresenter(readRepo, writeRepo);
                var view = new VolManagerView();

                // Koppling mellan vy och presenter (behåll SetPresenter om du använder den internt i vyn)
                view.SetPresenter(presenter);
                view.InitializeTabbedLayout(); // flikläge med pinned pairs

                var tabTitle = $"Vol Session {idx}";
                // VIKTIGT: rätt ordning på argumenten → (view, presenter, title)
                return new VolSessionControl(view, presenter, tabTitle);
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

        /// <summary>
        /// Disposar workspace. Ingen tvingad save här, för på stängning kan TabPages redan vara tömda
        /// och då skulle vi skriva över workspace.json med "Sessions=[]".
        /// </summary>
        public void Dispose()
        {
            try
            {
                _workspace?.Dispose();
            }
            catch
            {
                // best effort
            }
        }

    }
}
