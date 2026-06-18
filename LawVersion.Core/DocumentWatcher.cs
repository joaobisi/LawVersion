using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LawVersion.Core;

public class DocumentWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly IVersionControlService _versionService;
    private readonly ILogger<DocumentWatcher> _logger;
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new();
    private readonly HashSet<string> _syncingFiles = new();
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1500);

    public event Action<object, FileSystemEventArgs>? FileChanged;
    public event Action<string, bool>? FileLockChanged;

    public DocumentWatcher(string path, IVersionControlService versionService, ILogger<DocumentWatcher> logger)
    {
        _versionService = versionService;
        _logger = logger;

        _watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*",  // Captura todos os arquivos (inclusive locks do LibreOffice)
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, e) => OnFileAction(e);
        _watcher.Created += (_, e) => OnFileAction(e);
        _watcher.Deleted += (_, e) => OnFileAction(e);
        _watcher.Renamed += (_, e) => OnFileAction(e);
        
        _logger.LogInformation("Monitoramento iniciado na pasta: {Path}", path);
    }

    public void BeginSync(string fileName) => _syncingFiles.Add(fileName);
    public void EndSync(string fileName) => _syncingFiles.Remove(fileName);

    private void OnFileAction(FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Name)) return;
        if (_syncingFiles.Contains(e.Name)) return;

        // Tenta extrair o nome original se for um lock file
        var originalFile = ExtractOriginalFileName(e.Name);
        
        if (originalFile != null)
        {
            // Notifica mudança de estado do arquivo (lock file)
            bool isLocked = e.ChangeType != WatcherChangeTypes.Deleted;
            FileLockChanged?.Invoke(originalFile, isLocked);
            _logger.LogDebug("Lock detectado: {File} | Estado: {Status}", originalFile, isLocked);
        }
        else if (e.Name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) && !IsLockFileName(e.Name))
        {
            // Debounce antes de fazer commit do arquivo .docx
            DebouncedCommit(e);
        }
    }

    // Extrai o nome do arquivo original a partir de um nome de lock file.
    // Suporta padrões do MS Word (~$) e LibreOffice (.~lock.).
    // Retorna null se não for um lock file.
    private string? ExtractOriginalFileName(string fileName)
    {
        if (fileName.StartsWith("~$"))
        {
            return FindOriginalFileForWordLock(fileName);
        }

        // Verifica se é um lock file do LibreOffice
        if (fileName.StartsWith(".~lock."))
        {
            var withoutPrefix = fileName[7..];
            var hashIndex = withoutPrefix.IndexOf('#');
            return hashIndex >= 0 ? withoutPrefix[..hashIndex] : withoutPrefix;
        }

        return null;
    }

    private string? FindOriginalFileForWordLock(string lockFileName)
    {
        var suffix = lockFileName[2..]; // Ex: "ticao.docx"
        
        try
        {
            var watchPath = _watcher.Path;
            if (!Directory.Exists(watchPath)) return null;
            
            var files = Directory.GetFiles(watchPath, "*.docx");
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (IsLockFileName(name)) continue;
                
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    // O Word substitui as 2 primeiras letras, então o tamanho do lock file
                    // é igual ao do original (ex: Peticao.docx e ~$ticao.docx possuem 12 caracteres).
                    // Para arquivos curtos (ex: a.docx -> ~$a.docx), o lock file tem 2 caracteres a mais.
                    if (name.Length == lockFileName.Length || name.Length + 2 == lockFileName.Length)
                    {
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Erro ao buscar arquivo original para o lock file {LockFile}", lockFileName);
        }
        
        return null;
    }

    private static bool IsLockFileName(string fileName)
    {
        return fileName.StartsWith("~$") || fileName.StartsWith(".~lock.");
    }

    // Debounce: espera 1.5s sem novos eventos antes de efetivamente fazer o commit.
    // Evita dezenas de commits para um único Save do Word/LibreOffice.
    private void DebouncedCommit(FileSystemEventArgs e)
    {
        var fileName = e.Name!;
        
        _debounceTimers.AddOrUpdate(
            fileName,
            // Factory: primeiro evento para este arquivo
            _ => CreateCommitTimer(fileName, e),
            // Update: evento repetido — reseta o timer
            (_, existingTimer) =>
            {
                existingTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
                return existingTimer;
            });
    }

    private Timer CreateCommitTimer(string fileName, FileSystemEventArgs e)
    {
        return new Timer(_ =>
        {
            try
            {
                _versionService.CommitFile(fileName, "Salvo");
                FileChanged?.Invoke(this, e);
                _logger.LogInformation("Arquivo versionado (debounced): {File}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao commitar arquivo: {File}", fileName);
            }
            finally
            {
                if (_debounceTimers.TryRemove(fileName, out var timer))
                {
                    timer.Dispose();
                }
            }
        }, null, DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        
        // Limpa todos os timers de debounce pendentes
        foreach (var kvp in _debounceTimers)
        {
            kvp.Value.Dispose();
        }
        _debounceTimers.Clear();
        
        _logger.LogWarning("Watcher encerrado.");
        GC.SuppressFinalize(this);
    }
}