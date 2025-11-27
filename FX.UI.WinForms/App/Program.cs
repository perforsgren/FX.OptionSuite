using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using FX.Services;                 // AddFxServices(), FxRuntime
using FX.UI.WinForms;              // Form1, IAppInstance, PricerAppInstance, LegacyPricerView/Presenter
using System.Runtime.InteropServices;
using FX.Core.Domain.MarketData;
using FX.Services.MarketData;

namespace FX.UI.WinForms
{
    static class Program
    {

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);


        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var services = new ServiceCollection();

            // Shell (huvudfönster) – vi fortsätter att starta Form1 via DI
            services.AddTransient<Form1>();

            // Pricer – transienta (en egen vy/presenter per session/tab)
            services.AddTransient<LegacyPricerView>();
            services.AddTransient<LegacyPricerPresenter>();

            // Registrera själva app-instansen (IAppInstance) för Pricer
            services.AddTransient<PricerAppInstance>();

            services.AddTransient<VolAppInstance>();

            services.AddTransient<BlotterAppInstance>();

            // Registrera EN global MarketStore för hela appen
            services.AddSingleton<IMarketStore, MarketStore>();

            // FX-tjänster (din runtime m.m.)
            services.AddFxServices();

            // registrera orchestrator + feeder
            services.AddFxPricing();

            using (var sp = services.BuildServiceProvider())
            {
                // Starta runtime
                sp.GetRequiredService<FxRuntime>();

                // Starta huvudfönstret (Form1)
                Application.Run(sp.GetRequiredService<Form1>());
            }
        }
    }
}
