using System;
using System.Threading.Tasks;
using System.Windows;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, Task<string?>> _apply;
    private readonly Action<EdgeEffect> _preview;
    private AppSettings _source;
    private bool _applying;
    private bool _closed;

    public SettingsWindow(AppSettings settings, Func<AppSettings, Task<string?>> apply, Action<EdgeEffect> preview)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _source = SettingsService.Normalize(settings);
        InitializeComponent();
        LoadDraft(_source);
        Closed += (_, _) => _closed = true;
    }

    private void LoadDraft(AppSettings settings)
    {
        RedDurationSlider.Value = settings.RedAlertDurationSeconds;
        RepeatSlider.Value = settings.RepeatIntervalSeconds;
        ThicknessSlider.Value = settings.BorderThickness;
        OpacitySlider.Value = Math.Round(settings.BorderOpacity * 100, MidpointRounding.AwayFromZero);
        AllMonitorsCheckBox.IsChecked = settings.AllMonitors;
        UnverifiedCheckBox.IsChecked = settings.WarnWhenVpnUnverified;
        SoundCheckBox.IsChecked = settings.WarningSoundEnabled;
        NotificationsCheckBox.IsChecked = settings.NotificationsEnabled;
        LaunchCheckBox.IsChecked = settings.LaunchAtSignIn;
        MinimizedCheckBox.IsChecked = settings.StartMinimized;
    }

    private AppSettings ReadDraft() => SettingsService.Normalize(_source with
    {
        RedAlertDurationSeconds = (int)Math.Round(RedDurationSlider.Value),
        RepeatIntervalSeconds = (int)Math.Round(RepeatSlider.Value),
        BorderThickness = (int)Math.Round(ThicknessSlider.Value),
        BorderOpacity = Math.Round(OpacitySlider.Value) / 100,
        AllMonitors = AllMonitorsCheckBox.IsChecked == true,
        WarnWhenVpnUnverified = UnverifiedCheckBox.IsChecked == true,
        WarningSoundEnabled = SoundCheckBox.IsChecked == true,
        NotificationsEnabled = NotificationsCheckBox.IsChecked == true,
        LaunchAtSignIn = LaunchCheckBox.IsChecked == true,
        StartMinimized = MinimizedCheckBox.IsChecked == true
    });

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        _applying = true;
        SetBusy(true);
        var draft = ReadDraft();
        try
        {
            string? error = await _apply(draft);
            if (_closed) return;
            if (error is null)
            {
                _source = draft;
                LoadDraft(draft);
                StatusMessage.Text = "Settings saved and applied.";
            }
            else StatusMessage.Text = error;
        }
        catch (Exception exception)
        {
            if (!_closed) StatusMessage.Text = "Could not apply settings. " + exception.Message;
        }
        finally
        {
            _applying = false;
            if (!_closed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        DraftPanel.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        DefaultsButton.IsEnabled = !busy;
        if (busy) StatusMessage.Text = "Applying settings…";
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        LoadDraft(new AppSettings());
        StatusMessage.Text = "Defaults restored in this window. Select Apply to save them.";
    }

    private void PreviewRed_Click(object sender, RoutedEventArgs e) => _preview(EdgeEffect.Red);
    private void PreviewGreen_Click(object sender, RoutedEventArgs e) => _preview(EdgeEffect.Green);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
