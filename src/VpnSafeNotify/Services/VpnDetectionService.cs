using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

public sealed class VpnDetectionService : IVpnDetectionService
{
    private const uint ErrorBufferTooSmall = 603;
    private readonly string[] _additionalKeywords;
    private readonly INetworkSnapshotProvider _networkProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VpnDetectionService(AppSettings settings, INetworkSnapshotProvider? networkProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _additionalKeywords = (string[])(settings.AdditionalVpnAdapterKeywords ?? Array.Empty<string>()).Clone();
        _networkProvider = networkProvider ?? new NetworkSnapshotProvider();
    }

    public async Task<VpnDetectionResult> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkSnapshot snapshot;
            try
            {
                snapshot = await _networkProvider.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                snapshot = NetworkSnapshot.Unavailable;
            }

            var evidence = new List<string>();
            var failures = new List<string>();
            bool? netModRunning = null;
            await Task.Run(() =>
            {
                try
                {
                    ReadWindowsVpnConnections(evidence, failures, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add("Windows VPN session inspection could not be completed.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                netModRunning = ReadNetModAmbiguityHint();
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            VpnDetectionResult result = VpnEvidenceClassifier.Classify(snapshot, evidence.Count > 0,
                failures.Count == 0, _additionalKeywords, proxyClientRunning: netModRunning == true);
            if (netModRunning is null)
            {
                failures.Add("NetMod process-presence hint could not be read; proxy-only status is inconclusive.");
                if (result.State == VpnState.Disconnected)
                {
                    result = result with { State = VpnState.Unverified,
                        Detail = "VPN status is unverified because the optional NetMod proxy-only ambiguity check was unavailable." };
                }
            }
            if (failures.Count > 0)
            {
                var diagnostics = new List<string>(result.Diagnostics);
                diagnostics.AddRange(failures);
                result = result with { Diagnostics = diagnostics.AsReadOnly() };
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
    private static bool? ReadNetModAmbiguityHint()
    {
        try
        {
            // Exact process-name presence is only an uncertainty hint. It never confirms
            // a connection. Do not inspect executable paths, command lines, or window titles.
            Process[] processes = Process.GetProcessesByName("netmod");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static void ReadWindowsVpnConnections(List<string> evidence, List<string> failures,
        CancellationToken cancellationToken)
    {
        int structureSize = Marshal.SizeOf<RasConnection>();
        uint bufferSize = 0;
        uint result = RasEnumConnections(IntPtr.Zero, ref bufferSize, out uint count);
        if (result == 0 && count == 0)
        {
            return;
        }

        if (result != ErrorBufferTooSmall)
        {
            throw new Win32Exception(checked((int)result));
        }

        // A connection may appear while enumerating. Retry with the new required size.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bufferSize < structureSize || bufferSize > 1024 * 1024)
            {
                throw new InvalidOperationException("Windows returned an invalid RAS buffer size.");
            }

            int allocatedSize = checked((int)bufferSize);
            IntPtr buffer = Marshal.AllocHGlobal(allocatedSize);
            try
            {
                Marshal.WriteInt32(buffer, structureSize);
                result = RasEnumConnections(buffer, ref bufferSize, out count);
                if (result == ErrorBufferTooSmall)
                {
                    continue;
                }

                if (result != 0)
                {
                    throw new Win32Exception(checked((int)result));
                }

                if (count > allocatedSize / structureSize)
                {
                    throw new InvalidOperationException("Windows returned an invalid RAS connection count.");
                }

                for (int index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RasConnection connection = Marshal.PtrToStructure<RasConnection>(
                        IntPtr.Add(buffer, checked(index * structureSize)));
                    // RAS also includes dial-up sessions. Only VPN device types are relevant.
                    if (!string.Equals(connection.DeviceType, "vpn", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var status = new RasConnectionStatus { Size = (uint)Marshal.SizeOf<RasConnectionStatus>() };
                    uint statusResult = RasGetConnectStatus(connection.Handle, ref status);
                    if (statusResult != 0)
                    {
                        failures.Add($"A Windows VPN connection could not be checked (error {statusResult}).");
                        continue;
                    }

                    string deviceType = string.IsNullOrEmpty(status.DeviceType)
                        ? connection.DeviceType : status.DeviceType;
                    if (VpnEvidenceClassifier.IsConnectedRasVpn(status.State, status.Error, deviceType, status.SubState))
                    {
                        evidence.Add("Connected Windows VPN session.");
                    }
                    else if (status.State != 0x2001)
                    {
                        failures.Add("A Windows VPN session is connecting, reconnecting, or has an inconclusive state.");
                    }
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        failures.Add("Windows VPN connections changed repeatedly during the check.");
    }

    [DllImport("rasapi32.dll", EntryPoint = "RasEnumConnectionsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RasEnumConnections(IntPtr connections, ref uint bufferSize, out uint connectionCount);

    [DllImport("rasapi32.dll", EntryPoint = "RasGetConnectStatusW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RasGetConnectStatus(IntPtr connection, ref RasConnectionStatus status);

    // Microsoft Ras.h explicitly uses pshpack4.h, including on x64.
    // RASCONNW: 1388 bytes on x64, 1384 on x86; RASCONNSTATUSW: 608 on both.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct RasConnection
    {
        public uint Size;
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string EntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Phonebook;
        public uint SubEntry;
        public Guid EntryId;
        public uint Flags;
        public uint LuidLow;
        public int LuidHigh;
        public Guid CorrelationId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct RasConnectionStatus
    {
        public uint Size;
        public int State;
        public uint Error;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string PhoneNumber;
        public RasTunnelEndpoint LocalEndpoint;
        public RasTunnelEndpoint RemoteEndpoint;
        public int SubState;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RasTunnelEndpoint
    {
        public uint Type;
        public uint Address0;
        public uint Address1;
        public uint Address2;
        public uint Address3;
    }
}
