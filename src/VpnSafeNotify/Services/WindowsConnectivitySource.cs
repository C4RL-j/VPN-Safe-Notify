using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VpnSafeNotify.Services;

public sealed record WindowsConnectivityObservation(int? Flags, string Detail)
{
    public bool ReportsInternet => Flags.HasValue && (Flags.Value & (0x40 | 0x400)) != 0;
}

public interface IWindowsConnectivitySource
{
    Task<WindowsConnectivityObservation> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads Network List Manager's existing assessment; starts no app-owned request.</summary>
public sealed class WindowsConnectivitySource : IWindowsConnectivitySource
{
    private static readonly Guid NetworkListManagerClassId = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public Task<WindowsConnectivityObservation> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private static WindowsConnectivityObservation Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object? manager = null;
        try
        {
            Type? managerType = Type.GetTypeFromCLSID(NetworkListManagerClassId, throwOnError: true);
            manager = Activator.CreateInstance(managerType!);
            if (manager is null) { return new(null, "Windows connectivity information is unavailable."); }
            dynamic networkListManager = manager;
            int connectivity = (int)networkListManager.GetConnectivity();
            token.ThrowIfCancellationRequested();
            return new(connectivity, $"Windows Network List Manager connectivity flags: 0x{connectivity:X}. " +
                ((connectivity & (0x40 | 0x400)) != 0
                    ? "Windows reports IPv4 or IPv6 internet connectivity."
                    : "Windows has not confirmed internet access; local connectivity alone is inconclusive."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new(null, $"Windows connectivity information is unavailable (0x{exception.HResult:X8}).");
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) { Marshal.FinalReleaseComObject(manager); }
        }
    }
}
