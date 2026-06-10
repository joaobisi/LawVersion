using CommunityToolkit.Mvvm.ComponentModel;

namespace LawVersion.UI.Models;

public partial class DocumentItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private string _currentOwner = string.Empty;

    public bool IsLocked => !string.IsNullOrEmpty(CurrentOwner);
    
    public string StatusColor => IsLocked ? "#E53935" : "#43A047";
}