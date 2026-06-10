using CommunityToolkit.Mvvm.ComponentModel;

namespace LawVersion.UI.Models;

public partial class DocumentItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(IsLockedByOther))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private string _currentOwner = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(IsLockedByOther))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private bool _isOwnerMe = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharingText))]
    private string _sharingSummary = "Privado (Local)";

    public bool IsLocked => !string.IsNullOrEmpty(CurrentOwner);
    
    public bool IsLockedByOther => !string.IsNullOrEmpty(CurrentOwner) && !IsOwnerMe;
    
    public string StatusColor => !string.IsNullOrEmpty(CurrentOwner)
        ? (IsOwnerMe ? "#89B4FA" : "#EF5350")
        : "#66BB6A";
    
    public string StatusText => !string.IsNullOrEmpty(CurrentOwner)
        ? (IsOwnerMe ? "Você está editando" : $"Editando: {CurrentOwner}")
        : "Livre para edição";
    
    public string SharingText => SharingSummary;
    
    public string StatusIcon => !string.IsNullOrEmpty(CurrentOwner)
        ? (IsOwnerMe 
            ? "M14.06,9L15,9.94L5.92,19H5V18.08L14.06,9M17.66,3C17.41,3 17.15,4 16.96,4.19L15.13,6.02L18.98,9.87L20.81,8.04C21.2,7.65 21.2,7 20.81,6.61L17.39,3.19C17.2,3 16.95,3 16.66,3M14.06,6.19L3,17.25V21H6.75L17.81,9.94L14.06,6.19Z"
            : "M12,17V15H14V17H12M18,20V10H6V20H18M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z")
        : "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z";
}