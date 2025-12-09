using System;
using System.Windows.Forms;

namespace FxTradeHub.VolbrokerAeTestApp
{
    /// <summary>
    /// Programklass för mini-testapp som hämtar MessageIn och kör Volbroker AE-parsern.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Applikationens entry point.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Startform för att testa Volbroker FIX AE-parsning.
            Application.Run(new VolbrokerAeTestForm());
        }
    }
}
