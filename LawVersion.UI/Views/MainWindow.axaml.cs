using Avalonia.Controls;
using LawVersion.UI.ViewModels;

namespace LawVersion.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Cleanup();
        }
        base.OnClosing(e);
    }
}