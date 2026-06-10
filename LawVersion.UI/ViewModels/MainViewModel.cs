using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LawVersion.Core;
using LawVersion.UI.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Controls;

namespace LawVersion.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly P2PManager? _p2P;

    [ObservableProperty] private string _lawyerName = string.Empty;
    [ObservableProperty] private string _statusMessage = "Sistema Conectado";
    [ObservableProperty] private bool _isLogged = true;
    [ObservableProperty] private string _selectedFileHistory = string.Empty;

    public ObservableCollection<DocumentItem> Documents { get; } = new();

    public MainViewModel(P2PManager p2P)
    {
        _p2P = p2P;
        _lawyerName = _p2P.LawyerName;
        
        ConfigurarEventos();
        
        _ = _p2P.StartNetworkAsync();
        
        // Chamada direta para carregar os arquivos na abertura
        RefreshFiles();
        StatusMessage = $"Bem-vindo, {_lawyerName} | Pasta: {_p2P.WorkingDirectory}";
    }

    public MainViewModel() { _p2P = null; }

    private void ConfigurarEventos()
    {
        if (_p2P == null) return;

        // Quando a pasta mudar, atualizamos a lista
        _p2P.OnFolderChanged += () => RefreshFiles();
        
        _p2P.OnPeerDetected += (name, ip) => 
        {
            Dispatcher.UIThread.Post(() => StatusMessage = $"Colega online detectado: {name} em {ip}");
        };

        _p2P.OnFileLocked += (fileName, owner) => AtualizarEstadoLock(fileName, owner);
        _p2P.OnFileUnlocked += (fileName) => AtualizarEstadoLock(fileName, string.Empty);
    }

    private void AtualizarEstadoLock(string fileName, string owner)
    {
        Dispatcher.UIThread.Post(() => {
            var doc = Documents.FirstOrDefault(d => d.Name == fileName);
            if (doc != null) 
            {
                doc.CurrentOwner = owner;
                // Força a atualização da cor no ícone
                OnPropertyChanged(nameof(Documents));
            }
        });
    }

    [RelayCommand]
    private void ShowHistory(DocumentItem? doc)
    {
        if (doc is null || _p2P is null) return;
        var historyList = _p2P.GetFileHistory(doc.Name);
        SelectedFileHistory = $"--- Histórico Git: {doc.Name} ---\n" + 
                             (historyList.Any() ? string.Join("\n", historyList) : "Sem registros.");
    }

    [RelayCommand]
    private async Task ImportDocument(object? parentWindow)
    {
        // O Avalonia passa o objeto, precisamos converter para Window
        var parent = parentWindow as Window;
        if (parent == null || _p2P == null) return;

        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel?.StorageProvider is null) return;

        var selectedFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Selecionar Documentos Jurídicos",
            FileTypeFilter = new[] { new FilePickerFileType("Documentos Word") { Patterns = new[] { "*.docx" } } },
            AllowMultiple = true
        });

        if (selectedFiles.Count > 0)
        {
            foreach (var file in selectedFiles)
            {
                try 
                {
                    // No Ubuntu, LocalPath pode vir com caracteres de escape (%20 para espaço)
                    // O UnescapeDataString limpa isso para o File.Copy
                    string sourcePath = Uri.UnescapeDataString(file.Path.LocalPath);
                    var destination = Path.Combine(_p2P.WorkingDirectory, file.Name);
                
                    File.Copy(sourcePath, destination, true);
                }
                catch (Exception ex) 
                { 
                    StatusMessage = $"Erro: {ex.Message}"; 
                }
            }

            // Atualiza a lista imediatamente
            RefreshFiles();
            StatusMessage = $"{selectedFiles.Count} documento(s) importado(s).";
        }
    }

    [RelayCommand]
    public void RefreshFiles()
    {
        // Executamos na Thread da UI para evitar erros de coleção
        Dispatcher.UIThread.Post(() =>
        {
            if (_p2P == null || !Directory.Exists(_p2P.WorkingDirectory)) return;
            
            var filesOnDisk = Directory.GetFiles(_p2P.WorkingDirectory)
                .Select(Path.GetFileName)
                .Where(f => f != null && f.EndsWith(".docx") && !f.StartsWith("~$") && !f.StartsWith(".~lock"))
                .ToList();

            // Sincroniza a coleção Observable com o disco
            var toRemove = Documents.Where(d => !filesOnDisk.Contains(d.Name)).ToList();
            foreach (var item in toRemove) Documents.Remove(item);

            foreach (var fileName in filesOnDisk)
            {
                if (fileName is not null && Documents.All(d => d.Name != fileName))
                    Documents.Add(new DocumentItem { Name = fileName });
            }
        });
    }

    public void Cleanup() => _p2P?.Dispose();
}