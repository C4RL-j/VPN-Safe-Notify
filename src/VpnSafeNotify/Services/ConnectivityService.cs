using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

/// <summary>Combines Windows' assessment with a rate-limited, body-free HTTPS fallback.</summary>
public sealed class ConnectivityService : IConnectivityService, INetworkChangeAware, IDisposable
{
    public static readonly Uri ProbeAddress = new("https://www.gstatic.com/generate_204");
    public static readonly TimeSpan MinimumProbeInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(4);
    public const int MaximumResponseHeaderKilobytes = 4;

    private readonly INetworkSnapshotProvider _networkProvider;
    private readonly IWindowsConnectivitySource _windows;
    private readonly TimeProvider _clock;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _networkRevision;
    private bool _hasAttempt;
    private long _attemptTimestamp;
    private DateTimeOffset _attemptUtc;
    private ConnectivityResult? _cachedProbe;
    private string? _probeSignature;
    private long _probeRevision;
    private int _disposed;

    public ConnectivityService(INetworkSnapshotProvider? networkProvider = null,
        TimeProvider? timeProvider = null, HttpMessageHandler? probeHandler = null,
        IWindowsConnectivitySource? windowsSource = null)
    {
        _networkProvider = networkProvider ?? new NetworkSnapshotProvider();
        _clock = timeProvider ?? TimeProvider.System;
        _windows = windowsSource ?? new WindowsConnectivitySource();
        _client = new HttpClient(probeHandler ?? CreateProbeHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    // Network events invalidate evidence, but can never bypass the data-use rate limit.
    public void InvalidateNetwork() => Interlocked.Increment(ref _networkRevision);

    public async Task<ConnectivityResult> CheckAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken token = lifetime.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            long revision = Interlocked.Read(ref _networkRevision);
            Task<NetworkSnapshot> networkTask = ReadNetworkAsync(token);
            Task<WindowsConnectivityObservation> windowsTask = ReadWindowsAsync(token);
            await Task.WhenAll(networkTask, windowsTask).ConfigureAwait(false);
            NetworkSnapshot network = await networkTask.ConfigureAwait(false);
            WindowsConnectivityObservation windows = await windowsTask.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var diagnostics = new List<string>(network.Diagnostics)
            {
                windows.Detail,
                "Fallback: HTTPS GET https://www.gstatic.com/generate_204; expected empty 204; " +
                "4-second deadline; at most one attempt per 120 seconds; direct connection, no proxy discovery/PAC, retries, redirects, cookies or credentials. " +
                "Response headers capped at 4 KiB; response bodies are not read or drained."
            };

            if (revision != Interlocked.Read(ref _networkRevision))
            {
                return Result(InternetState.Unknown,
                    "The network changed while Windows connectivity was being read. Waiting for the network to settle.", diagnostics);
            }

            bool usable = network.Adapters.Any(IsUsableAdapter);
            bool adaptersCertain = network.AdaptersComplete && network.Adapters.All(adapter =>
                adapter.InspectionComplete && adapter.Status is not (OperationalStatus.Unknown or OperationalStatus.Testing));
            if (adaptersCertain && !usable)
            {
                return Result(InternetState.Offline,
                    "No operational non-loopback adapter has a usable internet address.", diagnostics);
            }

            if (windows.ReportsInternet)
            {
                return Result(InternetState.Online, "Windows reports internet access (IPv4 or IPv6).", diagnostics);
            }

            string signature = NetworkSignature(network);
            if (_hasAttempt)
            {
                TimeSpan age = _clock.GetElapsedTime(_attemptTimestamp);
                if (age < MinimumProbeInterval)
                {
                    double remaining = Math.Ceiling((MinimumProbeInterval - age).TotalSeconds);
                    diagnostics.Add($"Last fallback attempt: {_attemptUtc:O}; next permitted in {remaining:0} seconds (monotonic rate limit).");
                    bool sameNetwork = revision == _probeRevision && signature == _probeSignature &&
                        revision == Interlocked.Read(ref _networkRevision);
                    if (_cachedProbe is not null && sameNetwork)
                    {
                        diagnostics.Add("Using the bounded fallback result cached for this network.");
                        diagnostics.Add("Cached assessment: " + _cachedProbe.Detail);
                        return _cachedProbe with { Diagnostics = diagnostics.AsReadOnly() };
                    }

                    diagnostics.Add("The network changed or the previous attempt was cancelled; earlier online evidence is invalidated.");
                    return Result(InternetState.Unknown,
                        "Windows has not confirmed internet access. Waiting for the low-data fallback interval after a network change or cancelled check.", diagnostics);
                }
            }

            // Count attempts even when cancelled or inconclusive: churn must not produce extra traffic.
            _hasAttempt = true;
            _attemptTimestamp = _clock.GetTimestamp();
            _attemptUtc = _clock.GetUtcNow();
            _probeSignature = signature;
            _probeRevision = revision;
            _cachedProbe = null;
            diagnostics.Add($"Last fallback attempt: {_attemptUtc:O}; next attempt permitted after 120 seconds.");
            ConnectivityResult probe = await ProbeAsync(token, diagnostics).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (revision != Interlocked.Read(ref _networkRevision))
            {
                return Result(InternetState.Unknown,
                    "The network changed during the internet check. Earlier reachability evidence was discarded.", diagnostics);
            }

            _cachedProbe = probe;
            return probe;
        }
        finally
        {
            // SendAsync is awaited directly, including cancellation, before another check can enter.
            _gate.Release();
        }
    }

    private async Task<ConnectivityResult> ProbeAsync(CancellationToken token, List<string> diagnostics)
    {
        using var deadline = new CancellationTokenSource(ProbeDeadline, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeAddress)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.ConnectionClose = true;
        try
        {
            // Never read/buffer/copy content. Even a portal's large body is not consumed.
            using HttpResponseMessage response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                return Result(InternetState.Unknown, "The bounded internet check timed out; internet access is unconfirmed.", diagnostics);
            }

            bool empty = response.Content.Headers.ContentLength is null or 0 &&
                response.Content.Headers.ContentEncoding.Count == 0 &&
                !response.Headers.TransferEncoding.Any();
            if (response.StatusCode == HttpStatusCode.NoContent && empty)
            {
                diagnostics.Add("The HTTPS endpoint returned the expected empty 204 response with normal certificate validation.");
                return Result(InternetState.Online,
                    "Internet reachability confirmed by a bounded HTTPS check. This does not verify VPN routing.", diagnostics);
            }

            diagnostics.Add($"Fallback response: HTTP {(int)response.StatusCode}; unexpected content or status was not accepted and no body was read.");
            return Result(InternetState.Unknown,
                "The internet check was inconclusive (unexpected response or possible sign-in portal).", diagnostics);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            diagnostics.Add("The fallback reached its deadline or the HTTP operation was cancelled; this does not prove the PC is offline.");
            return Result(InternetState.Unknown, "The bounded internet check timed out; internet access is unconfirmed.", diagnostics);
        }
        catch (HttpRequestException)
        {
            diagnostics.Add("The HTTPS fallback failed (DNS, connection, TLS or HTTP error). No response content or exception URLs are collected.");
            return Result(InternetState.Unknown,
                "The internet check could not confirm access. The check endpoint may be unavailable or blocked.", diagnostics);
        }
    }

    private async Task<NetworkSnapshot> ReadNetworkAsync(CancellationToken token)
    {
        try { return await _networkProvider.ReadAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return NetworkSnapshot.Unavailable; }
    }

    private async Task<WindowsConnectivityObservation> ReadWindowsAsync(CancellationToken token)
    {
        try { return await _windows.ReadAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(null, "Windows connectivity information is unavailable."); }
    }

    private static bool IsUsableAdapter(NetworkAdapterSnapshot adapter) =>
        adapter.InterfaceType != NetworkInterfaceType.Loopback && adapter.Status == OperationalStatus.Up &&
        adapter.Addresses.Any(VpnEvidenceClassifier.IsMeaningfulAddress);

    private static string NetworkSignature(NetworkSnapshot snapshot)
    {
        // Only held in memory. Addresses are deliberately absent from user-visible diagnostics.
        var value = new StringBuilder();
        value.Append(snapshot.AdaptersComplete).Append(snapshot.RoutesComplete).Append(snapshot.ProxyConfigured);
        foreach (NetworkAdapterSnapshot adapter in snapshot.Adapters.OrderBy(adapter => adapter.Id, StringComparer.Ordinal))
        {
            value.Append('|').Append(adapter.Id).Append(':').Append(adapter.Status).Append(':').Append(adapter.InspectionComplete);
            foreach (IPAddress address in adapter.Addresses.OrderBy(address => address.ToString(), StringComparer.Ordinal))
            {
                value.Append('/').Append(address);
            }
        }

        foreach (NetworkRouteSnapshot route in snapshot.Routes.OrderBy(route => route.InterfaceIndex)
                     .ThenBy(route => route.Destination.ToString(), StringComparer.Ordinal).ThenBy(route => route.PrefixLength)
                     .ThenBy(route => route.Metric))
        {
            value.Append('|').Append(route.InterfaceIndex).Append(':').Append(route.Destination).Append('/')
                .Append(route.PrefixLength).Append(':').Append(route.Metric).Append(':').Append(route.InterfaceMetric)
                .Append(':').Append(route.IsUsable).Append(':').Append(route.IsOnLink).Append(':').Append(route.IsLocal);
        }

        return value.ToString();
    }

    private static ConnectivityResult Result(InternetState state, string detail, List<string> diagnostics) =>
        new(state, detail) { Diagnostics = diagnostics.AsReadOnly() };

    private static SocketsHttpHandler CreateProbeHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        // Automatic proxy/PAC discovery can fetch additional data and has different time bounds.
        // A required proxy can therefore make this direct fallback inconclusive; NLM is still used.
        UseProxy = false,
        ConnectTimeout = ProbeDeadline,
        MaxResponseHeadersLength = MaximumResponseHeaderKilobytes,
        MaxResponseDrainSize = 0,
        MaxConnectionsPerServer = 1,
        ActivityHeadersPropagator = null
        // TLS/certificate validation keeps platform defaults. No bypass is installed.
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _lifetime.Cancel();
        _client.Dispose();
        // Leave synchronization objects alive while a cancelled CheckAsync unwinds.
    }
}
