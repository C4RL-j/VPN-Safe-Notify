using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

/// <summary>One detection cycle at a time, triggered by settled network changes or a slow fallback.</summary>
public sealed class MonitoringService : IAsyncDisposable
{
    private readonly IConnectivityService _connectivity;
    private readonly IVpnDetectionService _vpn;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _debounce;
    private readonly INetworkChangeSource _changes;
    private readonly bool _ownsChanges;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _cycle;
    private Task? _worker;
    private MonitoringSnapshot? _latest;
    private long _revision;
    private long _lastChangeTimestamp;
    private volatile bool _stopping;

    public MonitoringService(IConnectivityService connectivity, IVpnDetectionService vpn, TimeSpan interval,
        INetworkChangeSource? networkChanges = null, TimeSpan? debounce = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _connectivity = connectivity;
        _vpn = vpn;
        _interval = interval;
        _changes = networkChanges ?? new SystemNetworkChangeSource();
        _ownsChanges = networkChanges is null;
    }

    public bool IsRunning => !_stopping && _cancellation is { IsCancellationRequested: false };
    public MonitoringSnapshot? LatestSnapshot => Volatile.Read(ref _latest);
    public string ChangeNotificationsDetail => (_changes is SystemNetworkChangeSource source ? source.Status :
        "Network changes trigger checks after the settling interval.") +
        $" Settling: {_debounce.TotalSeconds:0.##} seconds; local fallback: {_interval.TotalSeconds:0.##} seconds.";
    public event Action<MonitoringSnapshot>? SnapshotAvailable;
    public event Action? NetworkChanging;

    public void RequestStop()
    {
        lock (_sync)
        {
            _stopping = true;
            // Cancel the actual detector token before parent WaitAsync continuations can
            // dispose the linked source and unregister its parent cancellation callback.
            _cycle?.Cancel();
            _cancellation?.Cancel();
            Volatile.Write(ref _latest, null);
        }
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsRunning) return;
            lock (_sync)
            {
                while (_wake.Wait(0)) { }
                Volatile.Write(ref _latest, null);
                _revision++;
                _stopping = false;
                _lastChangeTimestamp = TimeProvider.System.GetTimestamp();
                _cancellation = new CancellationTokenSource();
            }
            _changes.Changed += OnNetworkChanged;
            var token = _cancellation.Token;
            _worker = Task.Run(() => RunAsync(token), token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_cancellation is null) return;
            RequestStop();
            // Native notification cancellation must run outside our callback's lock.
            _changes.Changed -= OnNetworkChanged;
            if (_worker is not null)
            {
                try { await _worker; }
                catch (OperationCanceledException) { }
            }
            lock (_sync)
            {
                _cancellation.Dispose();
                _cancellation = null;
                _worker = null;
                Volatile.Write(ref _latest, null);
            }
        }
        finally { _lifecycle.Release(); }
    }

    private void OnNetworkChanged()
    {
        lock (_sync)
        {
            if (_stopping || _cancellation is null || _cancellation.IsCancellationRequested) return;
            _revision++;
            _lastChangeTimestamp = TimeProvider.System.GetTimestamp();
            Volatile.Write(ref _latest, null);
            if (_connectivity is INetworkChangeAware aware) aware.InvalidateNetwork();
            _cycle?.Cancel();
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        try { NetworkChanging?.Invoke(); }
        catch (Exception exception) { Debug.WriteLine($"Network presentation failed: {exception}"); }
    }

    private async Task WaitForStableNetworkAsync(CancellationToken token)
    {
        while (true)
        {
            long timestamp;
            lock (_sync) timestamp = _lastChangeTimestamp;
            var remaining = _debounce - TimeProvider.System.GetElapsedTime(timestamp);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, token);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_stopping)
        {
            await WaitForStableNetworkAsync(token);
            CancellationTokenSource cycle;
            long revision;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_stopping) return;
                // Close the race between finishing the delay and an incoming change.
                if (TimeProvider.System.GetElapsedTime(_lastChangeTimestamp) < _debounce) continue;
                while (_wake.Wait(0)) { }
                revision = _revision;
                cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
                _cycle = cycle;
            }

            bool published = false;
            try
            {
                var internetTask = ReadInternetAsync(cycle.Token);
                var vpnTask = ReadVpnAsync(cycle.Token);
                // Network changes cancel the cycle, then await actual detector completion.
                // Shutdown alone may stop waiting for an uncooperative native OS read.
                await Task.WhenAll(internetTask, vpnTask).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                MonitoringSnapshot? snapshot = null;
                lock (_sync)
                {
                    if (revision == _revision && !cycle.IsCancellationRequested)
                    {
                        snapshot = new MonitoringSnapshot(internetTask.Result, vpnTask.Result, DateTimeOffset.Now);
                        Volatile.Write(ref _latest, snapshot);
                    }
                }
                if (snapshot is not null)
                {
                    published = true;
                    try { SnapshotAvailable?.Invoke(snapshot); }
                    catch (Exception exception) { Debug.WriteLine($"Status presentation failed: {exception}"); }
                }
            }
            catch (OperationCanceledException) when (cycle.IsCancellationRequested && !token.IsCancellationRequested) { }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_cycle, cycle)) _cycle = null;
                    cycle.Dispose();
                }
            }
            if (published) await _wake.WaitAsync(_interval, token);
        }
    }

    private async Task<ConnectivityResult> ReadInternetAsync(CancellationToken token)
    {
        try { return await _connectivity.CheckAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new ConnectivityResult(InternetState.Unknown, "Internet status is temporarily unavailable."); }
    }

    private async Task<VpnDetectionResult> ReadVpnAsync(CancellationToken token)
    {
        try { return await _vpn.CheckAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new VpnDetectionResult(VpnState.Unverified, "VPN detection is temporarily unavailable.", Array.Empty<string>()); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_ownsChanges && _changes is IDisposable disposable) disposable.Dispose();
        // A canceled native read can still be returning; retain these small managed gates
        // so its finally block cannot race disposal.
    }
}

