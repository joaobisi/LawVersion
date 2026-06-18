using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace LawVersion.Core;

public class VersionControlService : IVersionControlService
{
    private string _repoPath;
    private readonly ILogger<VersionControlService>? _logger;
    private readonly object _gitLock = new();

    public VersionControlService(string repoPath, ILogger<VersionControlService>? logger = null)
    {
        _repoPath = repoPath;
        _logger = logger;
    }
    
    public void InitializeRepository(string workingDirectory)
    {
        _repoPath = workingDirectory;

        if (!Directory.Exists(_repoPath))
        {
            Directory.CreateDirectory(_repoPath);
        }

        var gitAttributesPath = Path.Combine(_repoPath, ".gitattributes");
        bool isNew = !Repository.IsValid(_repoPath);

        if (isNew)
        {
            Repository.Init(_repoPath);
        }

        using var repo = new Repository(_repoPath);

        if (isNew)
        {
            // Configura identidade padrão do Git
            repo.Config.Set("user.name", "LawVersion");
            repo.Config.Set("user.email", "sistema@lawversion.local");
        }

        // Garante o arquivo .gitattributes para que o Git não corrompa arquivos binários (.docx)
        if (!File.Exists(gitAttributesPath))
        {
            try
            {
                File.WriteAllText(gitAttributesPath, "*.docx binary\n");
                Commands.Stage(repo, ".gitattributes");
                var signature = CreateSignature();
                repo.Commit("Adiciona .gitattributes para protecao de binarios", signature, signature);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Falha ao gravar ou commitar .gitattributes inicial");
            }
        }

        if (isNew)
        {
            // Cria um commit inicial vazio se nenhum commit foi feito
            try
            {
                var signature = CreateSignature();
                repo.Commit("Repositório LawVersion Inicializado", signature, signature, 
                    new CommitOptions { AllowEmptyCommit = true });
            }
            catch (Exception) { /* Ignora se já tiver commits */ }
            
            _logger?.LogInformation("Repositório Git inicializado em: {Path}", _repoPath);
        }
    }

    public void CommitFile(string fileName, string message)
    {
        // Lock para evitar corrupção do index com commits simultâneos
        lock (_gitLock)
        {
            try
            {
                using var repo = new Repository(_repoPath);
                
                // Adiciona o arquivo ao stage
                Commands.Stage(repo, fileName);
                
                // Verifica se há mudanças staged antes de fazer commit
                var status = repo.RetrieveStatus(fileName);
                if (status == FileStatus.Unaltered || status == FileStatus.Nonexistent)
                {
                    _logger?.LogDebug("Nenhuma mudança para commitar: {File}", fileName);
                    return;
                }
                
                var signature = CreateSignature();
                repo.Commit(message, signature, signature);
                
                _logger?.LogInformation("Arquivo versionado via LibGit2Sharp: {File}", fileName);
            }
            catch (EmptyCommitException)
            {
                // Sem mudanças reais para commitar — silenciar
                _logger?.LogDebug("Commit vazio ignorado para: {File}", fileName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao commitar arquivo: {File}", fileName);
            }
        }
    }

    public List<string> GetCommitHistory(string fileName)
    {
        try
        {
            using var repo = new Repository(_repoPath);
            
            var commits = repo.Commits
                .Where(c => c.Parents.Count() > 0 ? 
                    c.Tree[fileName] != null && 
                    (c.Parents.First().Tree[fileName] == null || 
                     c.Tree[fileName].Target.Id != c.Parents.First().Tree[fileName]?.Target.Id) :
                    c.Tree[fileName] != null)
                .Take(50) // Limita para performance
                .Select(c => $"{c.Id.ToString(7)} | {c.Author.When:dd/MM/yyyy} | {c.MessageShort}")
                .ToList();

            return commits.Count > 0 
                ? commits 
                : new List<string> { "Nenhum histórico encontrado para este arquivo." };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Erro ao buscar histórico de: {File}", fileName);
            return new List<string> { $"Erro ao buscar histórico: {ex.Message}" };
        }
    }

    public void RestoreFileVersion(string fileName, string commitSha)
    {
        var fullPath = Path.Combine(_repoPath, fileName);
        ExtractFileVersion(fileName, commitSha, fullPath);
        _logger?.LogInformation("Arquivo {File} restaurado para a versão {Sha}", fileName, commitSha);
    }

    public void ExtractFileVersion(string fileName, string commitSha, string destinationPath)
    {
        lock (_gitLock)
        {
            try
            {
                using var repo = new Repository(_repoPath);
                var commit = repo.Lookup<Commit>(commitSha) 
                    ?? repo.Commits.FirstOrDefault(c => c.Sha.StartsWith(commitSha, StringComparison.OrdinalIgnoreCase));

                if (commit == null)
                {
                    throw new Exception($"Commit '{commitSha}' não encontrado.");
                }

                var entry = commit.Tree[fileName];
                if (entry == null)
                {
                    throw new Exception($"Arquivo '{fileName}' não encontrado no commit '{commitSha}'.");
                }

                var blob = entry.Target as Blob;
                if (blob == null)
                {
                    throw new Exception($"Objeto de arquivo inválido no commit '{commitSha}'.");
                }

                // Cria o diretório de destino se não existir
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Sobrescreve/salva os bytes no caminho de destino
                using var stream = blob.GetContentStream();
                using var fileStream = File.Create(destinationPath);
                stream.CopyTo(fileStream);

                _logger?.LogInformation("Arquivo {File} extraído do commit {Sha} para {Dest}", fileName, commitSha, destinationPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao extrair versão {Sha} do arquivo {File} para {Dest}", commitSha, fileName, destinationPath);
                throw;
            }
        }
    }

    private static Signature CreateSignature()
    {
        return new Signature("LawVersion", "sistema@lawversion.local", DateTimeOffset.Now);
    }
}