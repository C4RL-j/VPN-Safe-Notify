using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VpnSafeNotify.Infrastructure;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;
using VpnSafeNotify.ViewModels;

namespace VpnSafeNotify;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private TrayIconService? _tray;
    private MonitoringService? _monitoring;
    private ConnectivityService? _connectivity;
    private AlertController? _alerts;
    private ISettingsService _settingsService = new SettingsService();
    private AppSettings _settings = new();
    private readonly MainViewModel _viewModel = new();
    private MainWindow? _window;
    private DiagnosticsWindow? _diagnostics;
    private SettingsWindow? _settingsWindow;
    private TimedPauseController? _timedPause;
    private StartupService _startup = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private long _pauseVersion;
    private bool _exiting;
    private bool _changingMonitoring;
    private bool _ready;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var smoke = PortableSmokeProfile.FromArguments(e.Args);
            if (smoke is not null)
            {
                _settingsService = new SettingsService(smoke.SettingsPath);
                _startup = new StartupService(registrySubKey: smoke.StartupRegistryPath);
            }
            _singleInstance = new SingleInstanceService(smoke?.Token);
            if (!_singleInstance.IsFirstInstance)
            {
                _singleInstance.SignalExistingInstance();
                Shutdown();
                return;
            }

            _settings = await _settingsService.LoadAsync();
            _viewModel.ApplySettings(_settings);
            _viewModel.SettingsMessage = _settingsService.LastError ?? string.Empty;
            bool? startupRegistered = _startup.IsEnabled();
            if (startupRegistered is null)
                _viewModel.SettingsMessage += " " + _startup.LastError;
            else if (startupRegistered != _settings.LaunchAtSignIn)
                _viewModel.SettingsMessage += " Startup registration differs from the saved preference. Apply Settings to update it.";
            var network = new NetworkSnapshotProvider();
            _connectivity = new ConnectivityService(network);
            _monitoring = new MonitoringService(_connectivity, new VpnDetectionService(_settings, network),
                TimeSpan.FromSeconds(_settings.PollIntervalSeconds));
            _window = new MainWindow(_viewModel, ToggleMonitoringAsync, SetNotificationsAsync, OpenDiagnostics,
                PreviewEffect, SetWarnWhenVpnUnverifiedAsync, OpenSettings);
            MainWindow = _window;
            _tray = new TrayIconService(OpenWindow, () => _ = ToggleMonitoringAsync(), () => _ = ExitAsync(),
                () => _ = PauseFor15MinutesAsync(), () => _ = ResumeMonitoringAsync(), OpenSettings);
            _window.Icon = Imaging.CreateBitmapSourceFromHIcon(_tray.WindowIcon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            _alerts = new AlertController(new ScreenEdgeOverlayHost(), new DispatcherAlertTimer(),
                _tray.ShowWarning, _settings.AlertCooldownSeconds,
                playWarningSound: () => System.Media.SystemSounds.Exclamation.Play());
            _alerts.ApplySettings(_settings);
            _timedPause = new TimedPauseController(new DispatcherAlertTimer(), () => _ = ResumeAfterPauseAsync());
            _monitoring.SnapshotAvailable += OnSnapshotAvailable;
            _monitoring.NetworkChanging += OnNetworkChanging;
            _singleInstance.StartListening(() =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(OpenWindow));
            });
            _ready = true;
            if (!_settings.StartMinimized) _window.Show();
            if (_settings.MonitoringEnabled)
                await SetMonitoringAsync(true, persist: false);
            else
                RefreshMonitoringState();
        }
        catch (Exception exception)
        {
            MessageBox.Show("VPN Safe Notify could not start.\n\n" + exception.Message,
                "VPN Safe Notify", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OpenWindow()
    {
        if (!_exiting) _window?.Open();
    }

    private void PreviewEffect(EdgeEffect effect)
    {
        if (!_exiting) _alerts?.Preview(effect);
    }

    private void OpenDiagnostics()
    {
        if (_exiting || _window is null) return;
        if (_diagnostics is null)
        {
            new WindowInteropHelper(_window).EnsureHandle();
            _diagnostics = new DiagnosticsWindow(_viewModel) { Owner = _window };
            _diagnostics.Closed += (_, _) => _diagnostics = null;
        }
        _diagnostics.Show();
        _diagnostics.Activate();
    }

    private void OpenSettings()
    {
        if (_exiting || _window is null) return;
        if (_settingsWindow is null)
        {
            new WindowInteropHelper(_window).EnsureHandle();
            _settingsWindow = new SettingsWindow(_settings, ApplySettingsAsync, PreviewEffect) { Owner = _window };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void OnNetworkChanging()
    {
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting || _monitoring?.IsRunning != true || _monitoring.LatestSnapshot is not null) return;
                _viewModel.SetChecking();
                _tray?.Update(true, null, _changingMonitoring);
                _alerts?.SuspendForNetworkChange();
            }));
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
    }

    private void OnSnapshotAvailable(MonitoringSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Ignore queued work from an old or paused monitoring loop.
                if (_exiting || _monitoring?.IsRunning != true || !ReferenceEquals(snapshot, _monitoring.LatestSnapshot)) return;
                _viewModel.SetSnapshot(snapshot);
                _tray?.Update(true, snapshot, _changingMonitoring);
                _alerts?.Evaluate(snapshot, _settings.NotificationsEnabled);
            }));
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
    }

    private async Task ToggleMonitoringAsync()
    {
        if (!_ready || _exiting || _changingMonitoring || _monitoring is null) return;
        CancelTimedPause();
        await SetMonitoringAsync(!_monitoring.IsRunning);
    }

    private void CancelTimedPause()
    {
        ++_pauseVersion;
        _timedPause?.Cancel();
        _viewModel.SetPauseUntil(null);
    }

    private async Task ResumeMonitoringAsync()
    {
        if (!_ready || _exiting || _changingMonitoring || _monitoring is null) return;
        CancelTimedPause();
        await SetMonitoringAsync(true);
    }

    private async Task PauseFor15MinutesAsync()
    {
        if (!_ready || _exiting || _changingMonitoring || _monitoring?.IsRunning != true) return;
        CancelTimedPause();
        long version = _pauseVersion;
        // A timed pause is session-only; preserve the saved monitoring preference.
        await SetMonitoringAsync(false, persist: false);
        if (_exiting || version != _pauseVersion || _monitoring.IsRunning) return;
        _timedPause?.PauseFor(TimeSpan.FromMinutes(15));
        _viewModel.SetPauseUntil(_timedPause?.ResumeAt);
        UpdateTray();
    }

    private async Task ResumeAfterPauseAsync()
    {
        long version = _pauseVersion;
        while (_changingMonitoring && !_exiting) await Task.Delay(50);
        if (!_exiting && version == _pauseVersion) await ResumeMonitoringAsync();
    }

    private async Task SetMonitoringAsync(bool enabled, bool persist = true)
    {
        if (_monitoring is null || _changingMonitoring || _exiting) return;
        _changingMonitoring = true;
        _viewModel.SetBusy(true);
        _tray?.Update(_monitoring.IsRunning, _monitoring.LatestSnapshot, true);
        try
        {
            _alerts?.SetMonitoring(enabled);
            if (enabled) await _monitoring.StartAsync();
            else await _monitoring.StopAsync();
            RefreshMonitoringState();
            if (persist)
                await ChangeSettingsAsync(current => current with { MonitoringEnabled = enabled });
        }
        catch (Exception exception)
        {
            _viewModel.SettingsMessage = "Monitoring could not be updated: " + exception.Message;
            RefreshMonitoringState();
        }
        finally
        {
            _changingMonitoring = false;
            _viewModel.SetBusy(false);
            UpdateTray();
        }
    }

    private void RefreshMonitoringState()
    {
        var running = _monitoring?.IsRunning == true;
        _viewModel.SetRunning(running);
        _viewModel.NetworkChangeDetail = _monitoring?.ChangeNotificationsDetail ?? string.Empty;
        if (running && _monitoring?.LatestSnapshot is { } snapshot)
            _viewModel.SetSnapshot(snapshot);
        UpdateTray();
    }

    private void UpdateTray() => _tray?.Update(_monitoring?.IsRunning == true,
        _monitoring?.LatestSnapshot, _changingMonitoring, _timedPause?.ResumeAt);

    private Task SetNotificationsAsync(bool enabled) =>
        ChangeSettingsAsync(current => current with { NotificationsEnabled = enabled });

    private Task SetWarnWhenVpnUnverifiedAsync(bool enabled) =>
        ChangeSettingsAsync(current => current with { WarnWhenVpnUnverified = enabled });

    private Task<string?> ApplySettingsAsync(AppSettings requested) =>
        ChangeSettingsAsync(current => requested with
        {
            MonitoringEnabled = current.MonitoringEnabled,
            PollIntervalSeconds = current.PollIntervalSeconds,
            AlertCooldownSeconds = current.AlertCooldownSeconds,
            AdditionalVpnAdapterKeywords = current.AdditionalVpnAdapterKeywords
        }, synchronizeStartup: true);

    private async Task<string?> ChangeSettingsAsync(Func<AppSettings, AppSettings> change, bool synchronizeStartup = false)
    {
        await _settingsGate.WaitAsync();
        try
        {
            if (!_ready || _exiting) return "The application is shutting down.";
            var previous = _settings;
            var candidate = SettingsService.Normalize(change(previous));
            if (!await _settingsService.SaveAsync(candidate))
            {
                _viewModel.ApplySettings(previous);
                return _viewModel.SettingsMessage = _settingsService.LastError ?? "Settings could not be saved.";
            }
            if (synchronizeStartup && !_startup.TrySetEnabled(candidate.LaunchAtSignIn))
            {
                string message = _startup.LastError ?? "Startup registration could not be updated.";
                if (!await _settingsService.SaveAsync(previous))
                    message += " The previous settings could not be restored: " + _settingsService.LastError;
                _viewModel.ApplySettings(previous);
                return _viewModel.SettingsMessage = message;
            }
            _settings = candidate;
            _viewModel.ApplySettings(_settings);
            _alerts?.ApplySettings(_settings);
            _viewModel.SettingsMessage = string.Empty;
            UpdateTray();
            return null;
        }
        finally { _settingsGate.Release(); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _ready = false;
        CancelTimedPause();
        _timedPause?.Dispose();
        _alerts?.SetMonitoring(false);
        _viewModel.SetBusy(true);
        _tray?.Update(false, null, true);
        try
        {
            if (_monitoring is not null)
            {
                _monitoring.SnapshotAvailable -= OnSnapshotAvailable;
                _monitoring.NetworkChanging -= OnNetworkChanging;
                await _monitoring.DisposeAsync();
            }
            // Finish an already accepted atomic settings save before the dispatcher stops.
            await _settingsGate.WaitAsync();
            _settingsGate.Release();
        }
        finally
        {
            _alerts?.Dispose();
            if (_window is not null) _window.AllowClose = true;
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        CancelTimedPause();
        _timedPause?.Dispose();
        _alerts?.SetMonitoring(false);
        _alerts?.Dispose();
        _monitoring?.RequestStop();
        _connectivity?.Dispose();
        if (_window is not null) _window.AllowClose = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _timedPause?.Dispose();
        _alerts?.Dispose();
        _monitoring?.RequestStop();
        _connectivity?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

