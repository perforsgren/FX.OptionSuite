using System;
using System.Windows.Forms;

namespace FX.UI.WinForms
{
    /// <summary>Kontrakt för en hostbar app-instans (t.ex. en pricer).</summary>
    public interface IAppInstance : IDisposable
    {
        string Title { get; }
        UserControl View { get; }

        void OnActivated();
        void OnDeactivated();
    }

    /// <summary>Kontrakt som Form1 implementerar för att kunna ta emot app-instanser.</summary>
    public interface IAppShellHost
    {
        void Attach(IAppInstance app);     // visa/aktivera i shell
        void Close(IAppInstance app);      // stäng specifik instans
    }
}
