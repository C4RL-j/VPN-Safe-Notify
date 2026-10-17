using System.Threading;
using System.Threading.Tasks;
using System;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

public interface INetworkSnapshotProvider
{
    Task<NetworkSnapshot> ReadAsync(CancellationToken cancellationToken);
}

public interface INetworkChangeSource
{
    event Action? Changed;
}

public interface INetworkChangeAware
{
    void InvalidateNetwork();
}

public interface IConnectivityService
{
    Task<ConnectivityResult> CheckAsync(CancellationToken cancellationToken);
}

public interface IVpnDetectionService
{
    Task<VpnDetectionResult> CheckAsync(CancellationToken cancellationToken);
}

public interface ISettingsService
{
    string SettingsPath { get; }
    string? LastError { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task<bool> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

