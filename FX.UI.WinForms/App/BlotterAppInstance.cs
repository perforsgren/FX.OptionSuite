using System;
using System.Windows.Forms;
using FX.UI.WinForms;

namespace FX.UI.WinForms
{
    /// <summary>
    /// App-instansen för FX Trade Blotter.
    /// Hanterar livscykel och exponerar <see cref="BlotterWorkspaceControl"/> till shellen.
    /// </summary>
    public sealed class BlotterAppInstance : IAppInstance
    {
        private readonly BlotterWorkspaceControl _workspace;

        /// <summary>
        /// Initierar blottern som en egen app-instans i FX OptionSuite.
        /// </summary>
        public BlotterAppInstance()
        {
            _workspace = new BlotterWorkspaceControl();
        }

        /// <summary>Titel som visas i dock-fönstrets rubrik.</summary>
        public string Title => "Trade Blotter";

        /// <summary>Rotkontrollen som shellen hostar i ett DockContent.</summary>
        public UserControl View => _workspace;

        /// <summary>Anropas när användaren aktiverar blotterns fönster.</summary>
        public void OnActivated()
        {
            _workspace?.OnActivated();
        }

        /// <summary>Anropas när blotterns fönster lämnar fokus.</summary>
        public void OnDeactivated()
        {
            _workspace?.OnDeactivated();
        }

        /// <summary>Frigör resurser associerade med blottern.</summary>
        public void Dispose()
        {
            _workspace?.Dispose();
        }
    }
}
