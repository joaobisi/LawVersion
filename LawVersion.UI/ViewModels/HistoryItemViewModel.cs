using CommunityToolkit.Mvvm.ComponentModel;

namespace LawVersion.UI.ViewModels;

public partial class HistoryItemViewModel : ObservableObject
{
    [ObservableProperty] private string _versionTag = string.Empty;
    [ObservableProperty] private string _sha = string.Empty;
    [ObservableProperty] private string _date = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _rawLine = string.Empty;

    public string DisplayText => $"{VersionTag} | {Date} | {Message}";
}
