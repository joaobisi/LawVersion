using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    
    [ObservableProperty] private bool _isHistoryVisible = false;
    [ObservableProperty] private string _selectedDocumentName = string.Empty;
    public ObservableCollection<string> SelectedFileHistoryLines { get; } = new();

    [ObservableProperty] private bool _isShareModalVisible = false;
    [ObservableProperty] private DocumentItem? _sharingDocument = null;

    [ObservableProperty] private bool _isImportModalVisible = false;
    [ObservableProperty] private string _importFilePath = string.Empty;
    [ObservableProperty] private string _importFileName = string.Empty;

    public ObservableCollection<string> ActivePeers { get; } = new();
    public ObservableCollection<SharePeerItem> SharePeers { get; } = new();
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
        
        // Inicializa pares online já conhecidos
        foreach (var peer in _p2P.GetActivePeerNames())
        {
            if (!ActivePeers.Contains(peer))
                ActivePeers.Add(peer);
        }
    }

    public MainViewModel() { _p2P = null; }

    private void ConfigurarEventos()
    {
        if (_p2P == null) return;

        // Quando a pasta mudar, atualizamos a lista
        _p2P.OnFolderChanged += () => RefreshFiles();
        
        _p2P.OnPeerDetected += (name, ip) => 
        {
            Dispatcher.UIThread.Post(() => {
                StatusMessage = $"Colega online detectado: {name} em {ip}";
                if (!ActivePeers.Contains(name))
                {
                    ActivePeers.Add(name);
                }
            });
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
                doc.IsOwnerMe = (!string.IsNullOrEmpty(owner) && owner == LawyerName);
                // Força a atualização da cor no ícone
                OnPropertyChanged(nameof(Documents));
            }
        });
    }

    [RelayCommand]
    private void ShowHistory(DocumentItem? doc)
    {
        if (doc is null || _p2P is null) return;
        
        SelectedDocumentName = doc.Name;
        SelectedFileHistoryLines.Clear();
        
        var historyList = _p2P.GetFileHistory(doc.Name);
        if (historyList.Any())
        {
            foreach (var line in historyList)
            {
                SelectedFileHistoryLines.Add(line);
            }
        }
        else
        {
            SelectedFileHistoryLines.Add("Sem registros de alterações ainda.");
        }
        
        IsHistoryVisible = true;
    }

    [RelayCommand]
    private void CloseHistory()
    {
        IsHistoryVisible = false;
    }

    [RelayCommand]
    private void OpenShareModal(DocumentItem? doc)
    {
        if (doc == null || _p2P == null) return;
        SharingDocument = doc;
        
        SharePeers.Clear();
        var sharedList = _p2P.GetSharedPeersForFile(doc.Name);
        foreach (var peer in ActivePeers)
        {
            bool isShared = sharedList.Contains(peer);
            SharePeers.Add(new SharePeerItem(peer, isShared));
        }
        
        IsShareModalVisible = true;
    }

    [RelayCommand]
    private void CloseShareModal()
    {
        IsShareModalVisible = false;
        SharingDocument = null;
        SharePeers.Clear();
    }

    [RelayCommand]
    private async Task ConfirmShare(SharePeerItem? peerItem)
    {
        if (SharingDocument != null && peerItem != null && _p2P != null)
        {
            await _p2P.ShareFileWithAsync(SharingDocument.Name, peerItem.Name);
            StatusMessage = $"Arquivo {SharingDocument.Name} compartilhado com {peerItem.Name}!";
            
            // Atualiza o item localmente para refletir o estado de compartilhado imediatamente
            var index = SharePeers.IndexOf(peerItem);
            if (index >= 0)
            {
                SharePeers[index] = new SharePeerItem(peerItem.Name, true);
            }
            
            RefreshFiles();
        }
    }

    [RelayCommand]
    private void OpenImportModal()
    {
        ImportFilePath = string.Empty;
        ImportFileName = string.Empty;
        IsImportModalVisible = true;
    }

    [RelayCommand]
    private void CloseImportModal()
    {
        IsImportModalVisible = false;
    }

    [RelayCommand]
    private async Task SelectImportFile(object? parentWindow)
    {
        var parent = parentWindow as Window;
        if (parent == null || _p2P == null) return;

        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel?.StorageProvider is null) return;

        var selectedFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Selecionar Documento Jurídico",
            FileTypeFilter = new[] { new FilePickerFileType("Documentos Word") { Patterns = new[] { "*.docx" } } },
            AllowMultiple = false
        });

        if (selectedFiles.Count > 0)
        {
            var file = selectedFiles[0];
            ImportFilePath = Uri.UnescapeDataString(file.Path.LocalPath);
            ImportFileName = file.Name;
        }
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (string.IsNullOrEmpty(ImportFilePath) || _p2P == null) return;

        try
        {
            var destination = Path.Combine(_p2P.WorkingDirectory, ImportFileName);
            File.Copy(ImportFilePath, destination, true);
            StatusMessage = $"Documento '{ImportFileName}' importado com sucesso.";
            IsImportModalVisible = false;
            RefreshFiles();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao importar: {ex.Message}";
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
                if (fileName is not null)
                {
                    var existing = Documents.FirstOrDefault(d => d.Name == fileName);
                    var sharedPeers = _p2P.GetSharedPeersForFile(fileName);
                    var summary = sharedPeers.Count > 0 
                        ? $"Compartilhado com: {string.Join(", ", sharedPeers)}" 
                        : "Privado (Local)";
                    
                    var owner = _p2P.GetFileOwner(fileName);
                    var isMe = (!string.IsNullOrEmpty(owner) && owner == LawyerName);

                    if (existing == null)
                    {
                        Documents.Add(new DocumentItem 
                        { 
                            Name = fileName, 
                            SharingSummary = summary,
                            CurrentOwner = owner,
                            IsOwnerMe = isMe
                        });
                    }
                    else
                    {
                        existing.SharingSummary = summary;
                        existing.CurrentOwner = owner;
                        existing.IsOwnerMe = isMe;
                    }
                }
            }
        });
    }

    [RelayCommand]
    private void OpenDocument(DocumentItem? doc)
    {
        if (doc is null || _p2P is null) return;
        
        var fullPath = Path.Combine(_p2P.WorkingDirectory, doc.Name);
        if (!File.Exists(fullPath)) return;
        
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", fullPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", fullPath);
            
            StatusMessage = $"Abrindo {doc.Name}...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao abrir: {ex.Message}";
        }
    }

    public void Cleanup() => _p2P?.Dispose();
}

public class SharePeerItem
{
    public string Name { get; }
    public bool IsAlreadyShared { get; }
    public bool CanShare => !IsAlreadyShared;
    public string ButtonText => IsAlreadyShared ? "✓ Compartilhado" : "Compartilhar";
    public string ButtonColor => IsAlreadyShared ? "#45475A" : "#89B4FA";
    public string ButtonTextColor => IsAlreadyShared ? "#A6ADC8" : "#1E1E2E";

    public SharePeerItem(string name, bool isAlreadyShared)
    {
        Name = name;
        IsAlreadyShared = isAlreadyShared;
    }
}