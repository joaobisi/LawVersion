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

        if (!Repository.IsValid(_repoPath))
        {
            Repository.Init(_repoPath);
            
            // Configura identidade padrão do Git
            using var repo = new Repository(_repoPath);
            repo.Config.Set("user.name", "LawVersion");
            repo.Config.Set("user.email", "sistema@lawversion.local");
            
            // Cria um commit inicial vazio para evitar erros de 'log' em repositórios novos
            var signature = CreateSignature();
            repo.Commit("Repositório LawVersion Inicializado", signature, signature, 
                new CommitOptions { AllowEmptyCommit = true });
            
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
                .Select(c => $"{c.Id.ToString(7)} | {c.Author.When:yyyy-MM-dd} | {c.MessageShort}")
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

    private static Signature CreateSignature()
    {
        return new Signature("LawVersion", "sistema@lawversion.local", DateTimeOffset.Now);
    }
}