using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using FX.Services;                 // AddFxServices(), FxRuntime
using FX.UI.WinForms;              // Form1, IAppInstance, PricerAppInstance, LegacyPricerView/Presenter
using System.Runtime.InteropServices;
using FX.Core.Domain.MarketData;

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

            // *** Ny rad: registrera själva app-instansen (IAppInstance) för Pricer ***
            services.AddTransient<PricerAppInstance>();

            // FX-tjänster (din runtime m.m.)
            services.AddFxServices();

            // registrera orchestrator + feeder
            services.AddFxPricing();

            using (var sp = services.BuildServiceProvider())
            {
                // Starta din runtime (oförändrat)
                sp.GetRequiredService<FxRuntime>();

                // Starta huvudfönstret (Form1) – oförändrat
                Application.Run(sp.GetRequiredService<Form1>());
            }
        }
    }
}
