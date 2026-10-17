using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush Neutral = MakeBrush("#AAB5CC");
    private static readonly Brush Good = MakeBrush("#75DFB5");
    private static readonly Brush Warning = MakeBrush("#FFC675");
    private static readonly Brush Danger = MakeBrush("#FF8D91");
    private static readonly Brush Accent = MakeBrush("#91B8FF");
    private bool _running;
    private bool _busy;
    private MonitoringSnapshot? _snapshot;
    private bool _notificationsEnabled;
    private bool _warnWhenVpnUnverified = true;
    private string _settingsMessage = string.Empty;
    private string _networkChangeDetail = string.Empty;
    private DateTimeOffset? _pauseUntil;
    private int _redAlertDurationSeconds = 6;
    private int _repeatIntervalSeconds = 60;

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool NotificationsEnabled { get => _notificationsEnabled; set { _notificationsEnabled = value; Changed(); } }
    public bool WarnWhenVpnUnverified
    {
        get => _warnWhenVpnUnverified;
        set
        {
            if (_warnWhenVpnUnverified == value) return;
            _warnWhenVpnUnverified = value;
            Changed();
            Changed(nameof(SummaryDetail));
        }
    }
    public string SettingsMessage { get => _settingsMessage; set { _settingsMessage = value; Changed(); } }
    public string NetworkChangeDetail { get => _networkChangeDetail; set { _networkChangeDetail = value; Changed(nameof(DiagnosticsText)); } }
    public bool CanToggle => !_busy;
    public string ToggleButtonText => _running ? "Pause Monitoring" : "Start Monitoring";
    public string MonitoringStatus => _busy ? "Updating…" : _running ? "Active" :
        _pauseUntil is { } until ? $"Paused until {until.ToLocalTime():HH:mm}" : "Paused";
    public Brush MonitoringBrush => _running ? Accent : Neutral;
    public string InternetStatus => !_running ? "Not checking" : _snapshot?.Internet.State switch
    {
        InternetState.Online => "Online", InternetState.Offline => "Offline", _ => "Unknown"
    };
    public Brush InternetBrush => !_running ? Neutral : _snapshot?.Internet.State == InternetState.Online ? Good : Neutral;
    public string InternetDetail => !_running ? "Resume monitoring to refresh internet status." : _snapshot?.Internet.Detail ?? "Waiting for the network to settle, then checking internet access…";
    public string VpnStatus => !_running ? "Not checking" : _snapshot?.Vpn.State switch
    {
        VpnState.Connected => "Connected", VpnState.Disconnected => "Disconnected", _ => "Unverified"
    };
    public Brush VpnBrush => !_running || _snapshot is null || _snapshot.Internet.State == InternetState.Offline ? Neutral : _snapshot.Vpn.State switch
    {
        VpnState.Connected => Good,
        VpnState.Disconnected => _snapshot.Internet.State == InternetState.Online ? Danger : Neutral,
        _ => Warning
    };
    public string VpnDetail => !_running ? "Resume monitoring to refresh VPN evidence." : _snapshot?.Vpn.Detail ?? "Waiting to check active tunnels and routes…";
    public string LastChecked => _snapshot is null ? "No current check. Network changes settle for about two seconds." :
        $"Last checked {_snapshot.CheckedAt.ToLocalTime():HH:mm:ss} · {(_running ? "Network changes trigger fresh checks" : "Status may have changed while paused")}";
    public string Summary => !_running ? "Monitoring is paused" : _snapshot switch
    {
        null => "Waiting for a stable connection…",
        { NeedsWarning: true, Vpn.State: VpnState.Unverified } => "Internet available. VPN is unverified.",
        { NeedsWarning: true } => "Internet available. VPN disconnected.",
        { Internet.State: InternetState.Offline } => "No usable network connection",
        { Internet.State: InternetState.Unknown } => "Internet access is unconfirmed",
        { Vpn.State: VpnState.Connected } => "Active VPN tunnel detected",
        _ => "Waiting for connection status"
    };
    public string SummaryDetail => !_running ? _pauseUntil is { } until
        ? $"Checks and warnings are paused. Monitoring resumes at {until.ToLocalTime():HH:mm}. Resume early from the tray or this window."
        : "Checks and warnings are paused. Start monitoring when you’re ready." : _snapshot switch
    {
        null => "Brief network changes are allowed to settle before any warning.",
        { NeedsWarning: true, Vpn.State: VpnState.Unverified } => _warnWhenVpnUnverified
            ? $"Detection is uncertain. Red warnings pulse for {DurationText} every {IntervalText} while internet access is confirmed. Check your VPN app."
            : "Detection is uncertain. Red warnings for Unverified are off; optional desktop notifications can still explain why.",
        { NeedsWarning: true } => $"A red screen-edge warning pulses for {DurationText} and repeats every {IntervalText} while this condition continues.",
        { Internet.State: InternetState.Offline } => "No usable active network interface was found. Waiting for a network change.",
        { Internet.State: InternetState.Unknown } => "The available evidence cannot confirm internet access. Failed checks do not automatically mean Offline.",
        _ => "A tunnel is active. Full internet traffic protection remains unverified."
    };
    public Brush SummaryBrush => !_running || _snapshot is null ? Neutral : _snapshot.NeedsWarning ?
        (_snapshot.Vpn.State == VpnState.Disconnected ? Danger : Warning) :
        _snapshot.Internet.State == InternetState.Online && _snapshot.Vpn.State == VpnState.Connected ? Good : Neutral;
    public IReadOnlyList<AdapterDiagnostic> DiagnosticAdapters => _running && _snapshot is not null
        ? _snapshot.Vpn.Adapters : Array.Empty<AdapterDiagnostic>();

    public string DiagnosticsText
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(_networkChangeDetail);
            if (!_running) return text.Append("Monitoring is paused. Resume to collect current detection reasons.").ToString();
            if (_snapshot is null) return text.Append("Network settling or detection is in progress. Previous results are not treated as current.").ToString();
            text.AppendLine($"Checked {_snapshot.CheckedAt.ToLocalTime():HH:mm:ss}");
            text.AppendLine($"Internet: {_snapshot.Internet.State} — {_snapshot.Internet.Detail}");
            foreach (var reason in _snapshot.Internet.Diagnostics) text.AppendLine("• " + reason);
            text.AppendLine();
            text.AppendLine($"VPN: {_snapshot.Vpn.State} — {_snapshot.Vpn.Detail}");
            foreach (var reason in _snapshot.Vpn.Diagnostics) text.AppendLine("• " + reason);
            foreach (var evidence in _snapshot.Vpn.Evidence) text.AppendLine("• " + evidence);
            text.AppendLine("Traffic protection: " + _snapshot.Vpn.TrafficProtection);
            text.Append("A phone hotspot is an ordinary PC uplink. This app cannot establish whether the phone's VPN carries tethered traffic.");
            return text.ToString();
        }
    }

    public void SetBusy(bool busy) { _busy = busy; Refresh(); }
    public void SetPauseUntil(DateTimeOffset? until) { _pauseUntil = until; Refresh(); }
    public void ApplySettings(AppSettings settings)
    {
        NotificationsEnabled = settings.NotificationsEnabled;
        WarnWhenVpnUnverified = settings.WarnWhenVpnUnverified;
        _redAlertDurationSeconds = settings.RedAlertDurationSeconds;
        _repeatIntervalSeconds = settings.RepeatIntervalSeconds;
        Refresh();
    }
    private string DurationText => _redAlertDurationSeconds == 6 ? "six seconds" : $"{_redAlertDurationSeconds} seconds";
    private string IntervalText => _repeatIntervalSeconds == 60 ? "minute" : $"{_repeatIntervalSeconds} seconds";
    public void SetRunning(bool running) { _running = running; _snapshot = null; Refresh(); }
    public void SetChecking() { _snapshot = null; Refresh(); }
    public void SetSnapshot(MonitoringSnapshot snapshot) { _snapshot = snapshot; Refresh(); }
    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    private static Brush MakeBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}

