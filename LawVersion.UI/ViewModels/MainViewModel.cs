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
using Avalonia.Controls.ApplicationLifetimes;

namespace LawVersion.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly P2PManager? _p2P;

    [ObservableProperty] private string _lawyerName = string.Empty;
    [ObservableProperty] private string _statusMessage = "Sistema Conectado";
    [ObservableProperty] private bool _isLogged = true;
    
    [ObservableProperty] private bool _isHistoryVisible = false;
    [ObservableProperty] private string _selectedDocumentName = string.Empty;
    [ObservableProperty] private bool _isSelectedDocumentLockedByOther = false;
    public ObservableCollection<HistoryItemViewModel> SelectedFileHistoryLines { get; } = new();

    [ObservableProperty] private bool _isConfirmRestoreVisible = false;
    [ObservableProperty] private string _confirmRestoreMessage = string.Empty;
    private HistoryItemViewModel? _pendingRestoreItem;

    [ObservableProperty] private bool _isCompletionNotificationVisible = false;
    [ObservableProperty] private string _completionNotificationMessage = string.Empty;

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
        _p2P.OnFileCompleted += (fileName, sender) =>
        {
            Dispatcher.UIThread.Post(() => {
                RefreshFiles();
                if (sender != LawyerName)
                {
                    CompletionNotificationMessage = $"O documento '{fileName}' foi concluído por {sender} e removido do painel.";
                    IsCompletionNotificationVisible = true;
                }
            });
        };
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
        IsSelectedDocumentLockedByOther = doc.IsLockedByOther;
        SelectedFileHistoryLines.Clear();
        
        var historyList = _p2P.GetFileHistory(doc.Name);
        var validCommits = historyList.Where(l => l.Contains('|')).ToList();

        if (validCommits.Any())
        {
            int total = validCommits.Count;
            for (int i = 0; i < total; i++)
            {
                var line = validCommits[i];
                var parts = line.Split('|');
                var sha = parts[0].Trim();
                var date = parts[1].Trim();
                var msg = string.Join("|", parts.Skip(2)).Trim();

                var verNum = total - i;
                SelectedFileHistoryLines.Add(new HistoryItemViewModel
                {
                    VersionTag = $"v{verNum}",
                    Sha = sha,
                    Date = date,
                    Message = msg,
                    RawLine = line
                });
            }
        }
        else
        {
            var displayMsg = historyList.FirstOrDefault() ?? "Sem registros de alterações ainda.";
            SelectedFileHistoryLines.Add(new HistoryItemViewModel
            {
                VersionTag = "-",
                Sha = string.Empty,
                Date = string.Empty,
                Message = displayMsg,
                RawLine = string.Empty
            });
        }
        
        IsHistoryVisible = true;
    }

    [RelayCommand]
    private void CloseHistory()
    {
        IsHistoryVisible = false;
    }

    [RelayCommand]
    private void ViewVersion(HistoryItemViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Sha) || string.IsNullOrEmpty(SelectedDocumentName) || _p2P is null) return;

        try
        {
            var ext = Path.GetExtension(SelectedDocumentName);
            var baseName = Path.GetFileNameWithoutExtension(SelectedDocumentName);
            
            var tempFileName = $"{baseName}_{item.VersionTag}{ext}";
            var tempPath = Path.Combine(Path.GetTempPath(), tempFileName);

            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }

            _p2P.ExtractFileToVersion(SelectedDocumentName, item.Sha, tempPath);
            File.SetAttributes(tempPath, FileAttributes.ReadOnly);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", tempPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", tempPath);

            StatusMessage = $"Visualizando {tempFileName} (somente leitura)...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao visualizar versão: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreVersion(HistoryItemViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Sha) || string.IsNullOrEmpty(SelectedDocumentName) || _p2P == null) return;

        _pendingRestoreItem = item;
        ConfirmRestoreMessage = $"Você está prestes a reverter o documento '{SelectedDocumentName}' para a versão {item.VersionTag}.\n\nIsso substituirá o arquivo de trabalho atual para você e todos os colegas na rede. Deseja continuar?";
        IsConfirmRestoreVisible = true;
    }

    [RelayCommand]
    private void ConfirmRestore()
    {
        if (_pendingRestoreItem == null || string.IsNullOrEmpty(SelectedDocumentName) || _p2P == null) return;

        try
        {
            var sha = _pendingRestoreItem.Sha;
            var tag = _pendingRestoreItem.VersionTag;

            _p2P.RestoreFileToVersion(SelectedDocumentName, sha);
            StatusMessage = $"Versão {tag} ({sha}) do arquivo '{SelectedDocumentName}' restaurada com sucesso!";
            
            IsConfirmRestoreVisible = false;
            IsHistoryVisible = false;
            _pendingRestoreItem = null;
            
            RefreshFiles();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao restaurar: {ex.Message}";
            IsConfirmRestoreVisible = false;
            IsHistoryVisible = false;
            _pendingRestoreItem = null;
        }
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _pendingRestoreItem = null;
        IsConfirmRestoreVisible = false;
    }

    [RelayCommand]
    private void CloseCompletionNotification()
    {
        IsCompletionNotificationVisible = false;
        CompletionNotificationMessage = string.Empty;
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

    [RelayCommand]
    private async Task CompleteDocument(DocumentItem? doc)
    {
        if (doc is null || _p2P is null) return;

        // Validar lock
        var owner = _p2P.GetFileOwner(doc.Name);
        if (!string.IsNullOrEmpty(owner) && owner != LawyerName)
        {
            StatusMessage = $"Não é possível concluir: o arquivo está bloqueado por {owner}.";
            return;
        }

        // Obter Window principal via ApplicationLifetime
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow is null) return;

            var topLevel = TopLevel.GetTopLevel(mainWindow);
            if (topLevel?.StorageProvider is null) return;

            // Abrir diálogo de salvar arquivo
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exportar Versão Final do Documento",
                SuggestedFileName = doc.Name,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Documentos Word") { Patterns = new[] { "*.docx" } }
                }
            });

            if (file is not null)
            {
                var destinationPath = Uri.UnescapeDataString(file.Path.LocalPath);
                try
                {
                    await _p2P.CompleteFileAsync(doc.Name, destinationPath);
                    CompletionNotificationMessage = $"O documento '{doc.Name}' foi concluído e exportado com sucesso!";
                    IsCompletionNotificationVisible = true;
                    RefreshFiles();
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Erro ao concluir documento: {ex.Message}";
                }
            }
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