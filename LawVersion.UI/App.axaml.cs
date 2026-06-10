using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using LawVersion.Core;
using LawVersion.Network;
using LawVersion.Network.Services;
using LawVersion.UI.ViewModels;
using LawVersion.UI.Views;

namespace LawVersion.UI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? Array.Empty<string>();
            string lawyerName = args.Length > 0 ? args[0] : "Advogado_Padrao";
            int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5000;

            // Definição da raiz de forma agnóstica ao sistema operacional.
            string rootPath;
            if (args.Length > 2)
            {
                rootPath = args[2];
            }
            else
            {
                rootPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
                    "LawVersionDocuments"
                );
            }

            if (!Directory.Exists(rootPath)) Directory.CreateDirectory(rootPath);

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole(); // No Ubuntu, os logs aparecem no terminal
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            var gitService = new VersionControlService(rootPath);
            var discovery = new DiscoveryService(); 
            var p2PServer = new P2PServer();        

            var manager = new P2PManager(
                rootPath, 
                lawyerName, 
                port, 
                gitService, 
                discovery, 
                p2PServer,
                loggerFactory
            );

            manager.InitializeSystem(); 

            var viewModel = new MainViewModel(manager);
            
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}