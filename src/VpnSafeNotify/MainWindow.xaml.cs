using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using VpnSafeNotify.ViewModels;
using VpnSafeNotify.Models;

namespace VpnSafeNotify;

public partial class MainWindow : Window
{
    private readonly Func<Task> _toggleMonitoring;
    private readonly Func<bool, Task> _setNotifications;
    private readonly Action _showDiagnostics;
    private readonly Action<EdgeEffect> _previewEffect;
    private readonly Func<bool, Task> _setWarnWhenVpnUnverified;
    private readonly Action _openSettings;
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel viewModel, Func<Task> toggleMonitoring, Func<bool, Task> setNotifications,
        Action showDiagnostics, Action<EdgeEffect>? previewEffect = null,
        Func<bool, Task>? setWarnWhenVpnUnverified = null, Action? openSettings = null)
    {
        _toggleMonitoring = toggleMonitoring;
        _setNotifications = setNotifications;
        _showDiagnostics = showDiagnostics;
        _previewEffect = previewEffect ?? (_ => { });
        _setWarnWhenVpnUnverified = setWarnWhenVpnUnverified ?? (_ => Task.CompletedTask);
        _openSettings = openSettings ?? (() => { });
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int darkMode = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref darkMode, sizeof(int));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            foreach (Window owned in OwnedWindows) owned.Hide();
            Hide();
        }
        base.OnClosing(e);
    }

    public void Open()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e) => await _toggleMonitoring();
    private async void Notifications_Click(object sender, RoutedEventArgs e) => await _setNotifications(NotificationsCheckBox.IsChecked == true);
    private async void WarnWhenVpnUnverified_Click(object sender, RoutedEventArgs e) =>
        await _setWarnWhenVpnUnverified(WarnWhenVpnUnverifiedCheckBox.IsChecked == true);
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _showDiagnostics();
    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void PreviewRed_Click(object sender, RoutedEventArgs e) => _previewEffect(EdgeEffect.Red);
    private void PreviewGreen_Click(object sender, RoutedEventArgs e) => _previewEffect(EdgeEffect.Green);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

