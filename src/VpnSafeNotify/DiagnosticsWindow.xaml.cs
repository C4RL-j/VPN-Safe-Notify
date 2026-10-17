using System.Windows;
using VpnSafeNotify.ViewModels;

namespace VpnSafeNotify;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
