using System;
using System.Windows.Forms;
using FX.UI.WinForms.Features.Blotter;

namespace FX.UI.WinForms
{
    /// <summary>
    /// App-instansen för FX Trade Blotter.
    /// Hanterar livscykel och kopplar ihop BlotterWorkspaceControl med BlotterPresenter.
    /// </summary>
    public sealed class BlotterAppInstance : IAppInstance, IDisposable
    {
        private readonly BlotterWorkspaceControl _workspace;
        private readonly BlotterPresenter _presenter;

        /// <summary>
        /// Skapar en ny blotter-appinstans.
        /// BlotterPresenter tillhandahålls via DI eller anropande kod.
        /// </summary>
        /// <param name="presenter">Presenter som äger blotter-logiken.</param>
        public BlotterAppInstance(
            BlotterWorkspaceControl workspace,
            BlotterPresenter presenter)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

            _workspace.Initialize(_presenter);
        }

        /// <summary>
        /// Titel som visas i dock-fönstrets rubrik.
        /// </summary>
        public string Title => "Trade Blotter";

        /// <summary>
        /// Rotkontrollen som shellen hostar i ett DockContent.
        /// </summary>
        public UserControl View => _workspace;

        /// <summary>
        /// Anropas när användaren aktiverar blotterns fönster.
        /// </summary>
        public void OnActivated()
        {
            _workspace?.OnActivated();
        }

        /// <summary>
        /// Anropas när blotterns fönster lämnar fokus.
        /// </summary>
        public void OnDeactivated()
        {
            _workspace?.OnDeactivated();
        }

        /// <summary>
        /// Frigör resurser associerade med blottern.
        /// </summary>
        public void Dispose()
        {
            _workspace?.Dispose();
        }
    }
}
