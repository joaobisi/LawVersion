namespace LawVersion.Core;

public interface IVersionControlService
{
    void InitializeRepository(string workingDirectory);
    
    void CommitFile(string fileName, string message);
    
    List<string> GetCommitHistory(string fileName);

    void RestoreFileVersion(string fileName, string commitSha);
}