using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class InternetChecks
{
    public static int AssertionCount { get; private set; }
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        AssertionCount = 0;
        await PassiveClassificationAsync();
        await ProbeClassificationAsync();
        await CacheAndNetworkChangesAsync();
        await DeadlineAsync();
        await CancellationAndConcurrencyAsync();
    }

    private static async Task PassiveClassificationAsync()
    {
        foreach (int flags in new[] { 0x40, 0x400, 0x440 })
        {
            var handler = new Handler((_, _) => throw new InvalidOperationException("No HTTP fallback expected."));
            using var service = Service(new SnapshotSource(), new Clock(), handler, new WindowsSource(flags));
            Expect((await service.CheckAsync(default)).State == InternetState.Online, "Windows IPv4/IPv6 internet flags establish online.");
            Expect(handler.Calls == 0, "Windows-confirmed online must not send an owned request.");
        }

        foreach (NetworkSnapshot snapshot in new[]
        {
            Network(Array.Empty<NetworkAdapterSnapshot>()),
            Network(new[] { Adapter() with { Status = OperationalStatus.Down } }),
            Network(new[] { Adapter() with { Addresses = Array.Empty<IPAddress>() } }),
            Network(new[] { Adapter() with { Addresses = new[] { IPAddress.Parse("169.254.5.2"), IPAddress.Parse("fe80::1") } } }),
            Network(new[] { Adapter() with { InterfaceType = NetworkInterfaceType.Loopback, Addresses = new[] { IPAddress.Loopback } } })
        })
        {
            var handler = SuccessHandler();
            using var service = Service(new SnapshotSource(snapshot), new Clock(), handler, new WindowsSource(0));
            Expect((await service.CheckAsync(default)).State == InternetState.Offline, "Confident absence of an active usable interface is offline.");
            Expect(handler.Calls == 0, "No usable interface must not waste fallback traffic.");
        }

        foreach (NetworkSnapshot snapshot in new[]
        {
            Network(Array.Empty<NetworkAdapterSnapshot>()) with { AdaptersComplete = false },
            Network(new[] { Adapter() with { InspectionComplete = false, Addresses = Array.Empty<IPAddress>() } }),
            Network(new[] { Adapter() with { Status = OperationalStatus.Unknown } }),
            Network(new[] { Adapter() with { Status = OperationalStatus.Testing } })
        })
        {
            using var service = Service(new SnapshotSource(snapshot), new Clock(), ErrorHandler(), new WindowsSource(0));
            Expect((await service.CheckAsync(default)).State == InternetState.Unknown, "Incomplete adapter evidence must not force offline when a probe fails.");
        }

        foreach (int? flags in new int?[] { 0, 1, 2, 0x10, 0x20, 0x100, 0x200, null })
        {
            var handler = ErrorHandler();
            using var service = Service(new SnapshotSource(), new Clock(), handler, new WindowsSource(flags));
            Expect((await service.CheckAsync(default)).State == InternetState.Unknown, "Connected adapter/local Windows flags and failed fallback do not prove online or offline.");
            Expect(handler.Calls == 1, "Inconclusive Windows information permits one bounded fallback.");
        }

        using (var service = Service(new SnapshotSource(), new Clock(), SuccessHandler(), new WindowsSource(null) { Throws = true }))
        {
            Expect((await service.CheckAsync(default)).State == InternetState.Online, "A working fallback can recover unavailable Windows assessment.");
        }
        using (var service = Service(new SnapshotSource { Throws = true }, new Clock(), ErrorHandler(), new WindowsSource(0)))
        {
            Expect((await service.CheckAsync(default)).State == InternetState.Unknown, "Failed adapter inspection must not force offline.");
        }
    }

    private static async Task ProbeClassificationAsync()
    {
        foreach ((HttpStatusCode status, long? length, bool chunked, bool encoded, InternetState expected) in new[]
        {
            (HttpStatusCode.NoContent, (long?)0, false, false, InternetState.Online),
            (HttpStatusCode.NoContent, null, false, false, InternetState.Online),
            (HttpStatusCode.NoContent, (long?)1024, false, false, InternetState.Unknown),
            (HttpStatusCode.NoContent, (long?)0, true, false, InternetState.Unknown),
            (HttpStatusCode.NoContent, (long?)0, false, true, InternetState.Unknown),
            (HttpStatusCode.OK, (long?)0, false, false, InternetState.Unknown),
            (HttpStatusCode.OK, (long?)500_000_000, false, false, InternetState.Unknown),
            (HttpStatusCode.Redirect, (long?)0, false, false, InternetState.Unknown),
            (HttpStatusCode.ProxyAuthenticationRequired, (long?)0, false, false, InternetState.Unknown),
            (HttpStatusCode.ServiceUnavailable, (long?)0, false, false, InternetState.Unknown)
        })
        {
            var content = new MustNotReadContent(length);
            var handler = new Handler((request, _) =>
            {
                Expect(request.RequestUri == ConnectivityService.ProbeAddress, "Fallback must use the fixed HTTPS connectivity endpoint, not an external-IP service.");
                Expect(request.Method == HttpMethod.Get && request.Content is null, "Fallback is one content-free GET.");
                Expect(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"), "Fallback must carry no cookies or authorization.");
                var response = new HttpResponseMessage(status) { Content = content };
                if (chunked) { response.Headers.TransferEncodingChunked = true; }
                if (encoded) { response.Content.Headers.ContentEncoding.Add("gzip"); }
                if (status == HttpStatusCode.Redirect) { response.Headers.Location = new Uri("https://example.invalid/sign-in?secret=neverlog"); }
                return Task.FromResult(response);
            });
            using var service = Service(new SnapshotSource(), new Clock(), handler, new WindowsSource(0));
            ConnectivityResult result = await service.CheckAsync(default);
            Expect(result.State == expected, $"HTTP {(int)status} / content semantics must classify conservatively.");
            Expect(content.Reads == 0 && content.WasDisposed, "Response content must be disposed without reading or buffering, including large portal pages.");
            Expect(handler.Calls == 1, "Unexpected HTTP responses must not cause retries.");
            Expect(!string.Join(" ", result.Diagnostics).Contains("neverlog", StringComparison.Ordinal), "Diagnostics must not expose redirect locations or response content.");
        }

        var failedHandler = new Handler((_, _) => throw new HttpRequestException("https://user:secret@example.invalid/private"));
        using var failedService = Service(new SnapshotSource(), new Clock(), failedHandler, new WindowsSource(0));
        ConnectivityResult failed = await failedService.CheckAsync(default);
        Expect(failed.State == InternetState.Unknown, "DNS/TLS/HTTP exceptions must produce unknown, not offline.");
        Expect(!string.Join(" ", failed.Diagnostics).Contains("secret", StringComparison.Ordinal), "Diagnostics must not collect exception URLs or secrets.");
    }

    private static async Task CacheAndNetworkChangesAsync()
    {
        var source = new SnapshotSource();
        var clock = new Clock();
        var handler = SuccessHandler();
        using var service = Service(source, clock, handler, new WindowsSource(0));
        Expect((await service.CheckAsync(default)).State == InternetState.Online, "Initial fallback succeeds.");
        for (int index = 0; index < 8; index++)
        {
            Expect((await service.CheckAsync(default)).State == InternetState.Online, "Repeated polls reuse positive fallback evidence on the same network.");
        }
        Expect(handler.Calls == 1, "Normal polling must not produce frequent requests.");
        clock.Advance(TimeSpan.FromSeconds(119));
        clock.SetUtc(DateTimeOffset.Parse("2099-01-01T00:00:00Z"));
        Expect((await service.CheckAsync(default)).State == InternetState.Online && handler.Calls == 1, "Wall-clock jumps must not bypass monotonic request spacing.");
        service.InvalidateNetwork();
        Expect((await service.CheckAsync(default)).State == InternetState.Unknown && handler.Calls == 1, "Network events invalidate cached online evidence without bypassing the hard request gate.");
        for (int index = 0; index < 20; index++) { service.InvalidateNetwork(); }
        Expect((await service.CheckAsync(default)).State == InternetState.Unknown && handler.Calls == 1, "Event bursts must not issue extra requests.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Expect((await service.CheckAsync(default)).State == InternetState.Online && handler.Calls == 2, "A new probe is permitted at the exact 120-second boundary.");
        source.Value = source.Value with { Adapters = new[] { Adapter() with { Addresses = new[] { IPAddress.Parse("10.5.0.2") } } } };
        Expect((await service.CheckAsync(default)).State == InternetState.Unknown && handler.Calls == 2, "Changed interface/address signature invalidates cached reachability without a new event.");
        clock.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAsync(default);
        source.Value = source.Value with { Routes = new[] { new NetworkRouteSnapshot(IPAddress.Any, 0, 1, 5) } };
        Expect((await service.CheckAsync(default)).State == InternetState.Unknown && handler.Calls == 3, "Changed routing signature invalidates old online evidence.");
        Expect((await service.CheckAsync(default)).Diagnostics.Any(item => item.Contains("Last fallback attempt:", StringComparison.Ordinal)), "Diagnostics expose the last attempt and rate gate.");

        var errorClock = new Clock();
        var errorHandler = ErrorHandler();
        using var errorService = Service(new SnapshotSource(), errorClock, errorHandler, new WindowsSource(0));
        await errorService.CheckAsync(default);
        errorClock.Advance(TimeSpan.FromSeconds(119));
        Expect((await errorService.CheckAsync(default)).State == InternetState.Unknown && errorHandler.Calls == 1, "Failures must be cached to avoid repeated downloads/retries.");
        errorClock.Advance(TimeSpan.FromSeconds(1));
        await errorService.CheckAsync(default);
        Expect(errorHandler.Calls == 2, "A failed fallback can be retried only after the hard interval.");

        var pending = Source<HttpResponseMessage>();
        var pendingHandler = new Handler((_, _) => pending.Task);
        using var changedService = Service(new SnapshotSource(), new Clock(), pendingHandler, new WindowsSource(0));
        Task<ConnectivityResult> check = changedService.CheckAsync(default);
        await pendingHandler.Entered.Task.WaitAsync(TestDeadline);
        changedService.InvalidateNetwork();
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        Expect((await check).State == InternetState.Unknown, "A response arriving after a network change must be discarded.");
    }

    private static async Task DeadlineAsync()
    {
        var clock = new Clock();
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var service = Service(new SnapshotSource(), clock, handler, new WindowsSource(0));
        Task<ConnectivityResult> task = service.CheckAsync(default);
        await handler.Entered.Task.WaitAsync(TestDeadline);
        clock.Advance(TimeSpan.FromMilliseconds(3999));
        Expect(!task.IsCompleted, "The configured probe deadline must not fire early.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Expect((await task.WaitAsync(TestDeadline)).State == InternetState.Unknown, "The exact 4-second deadline cancels a pending request and returns unknown.");
        Expect(handler.Active == 0, "Timeout completion must wait until the real HTTP operation has unwound.");
        await service.CheckAsync(default);
        Expect(handler.Calls == 1, "Timeout must still consume the rate-limited request attempt.");
    }

    private static async Task CancellationAndConcurrencyAsync()
    {
        var clock = new Clock();
        var cancellationObserved = Source<bool>();
        var mayUnwind = Source<bool>();
        var handler = new Handler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                await mayUnwind.Task;
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var service = Service(new SnapshotSource(), clock, handler, new WindowsSource(0));
        using var cancellation = new CancellationTokenSource();
        Task<ConnectivityResult> first = service.CheckAsync(cancellation.Token);
        await handler.Entered.Task.WaitAsync(TestDeadline);
        cancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TestDeadline);
        Task<ConnectivityResult> second = service.CheckAsync(default);
        Expect(!first.IsCompleted && !second.IsCompleted && handler.Calls == 1, "Cancellation must hold the gate until the outstanding request actually unwinds.");
        mayUnwind.SetResult(true);
        await ExpectCancellationAsync(first, "Shutdown/pause cancellation must propagate rather than publish a detection result.");
        Expect((await second.WaitAsync(TestDeadline)).State == InternetState.Unknown, "A resumed check must respect the cancelled attempt's rate limit.");
        Expect(handler.MaximumActive == 1 && handler.Active == 0, "Cancelled checks and resumed checks must never overlap HTTP requests.");

        var pending = Source<HttpResponseMessage>();
        var concurrentHandler = new Handler((_, _) => pending.Task);
        using var concurrent = Service(new SnapshotSource(), new Clock(), concurrentHandler, new WindowsSource(0));
        Task<ConnectivityResult>[] checks = Enumerable.Range(0, 12).Select(_ => concurrent.CheckAsync(default)).ToArray();
        await concurrentHandler.Entered.Task.WaitAsync(TestDeadline);
        Expect(concurrentHandler.Calls == 1, "Concurrent callers must share one serialized probe attempt.");
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        ConnectivityResult[] results = await Task.WhenAll(checks).WaitAsync(TestDeadline);
        Expect(results.All(result => result.State == InternetState.Online), "Queued same-network checks must reuse the completed positive result.");
        Expect(concurrentHandler.Calls == 1 && concurrentHandler.MaximumActive == 1, "Concurrent polling must not overlap or repeat HTTP probes.");

        var cancelledHandler = SuccessHandler();
        using var cancelled = Service(new SnapshotSource(), new Clock(), cancelledHandler, new WindowsSource(0));
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await ExpectCancellationAsync(cancelled.CheckAsync(alreadyCancelled.Token), "An already-cancelled check must not run.");
        Expect(cancelledHandler.Calls == 0, "An already-cancelled check must send no request.");

        var disposeHandler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var disposable = Service(new SnapshotSource(), new Clock(), disposeHandler, new WindowsSource(0));
        Task<ConnectivityResult> disposingCheck = disposable.CheckAsync(default);
        await disposeHandler.Entered.Task.WaitAsync(TestDeadline);
        disposable.Dispose();
        await ExpectCancellationAsync(disposingCheck, "Disposing the service must cancel its outstanding request.");
        Expect(disposeHandler.Active == 0, "Service shutdown must not leave HTTP work running after cancellation completes.");
    }

    private static ConnectivityService Service(SnapshotSource source, Clock clock, Handler handler, WindowsSource windows) => new(source, clock, handler, windows);
    private static Handler SuccessHandler() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
    private static Handler ErrorHandler() => new((_, _) => throw new HttpRequestException("Endpoint unavailable."));
    private static NetworkAdapterSnapshot Adapter() => new("interface-1", "Ethernet", "Ethernet adapter", NetworkInterfaceType.Ethernet,
        OperationalStatus.Up, 1, 1, new[] { IPAddress.Parse("192.168.2.10") });
    private static NetworkSnapshot Network(IReadOnlyList<NetworkAdapterSnapshot> adapters) => new(adapters, Array.Empty<NetworkRouteSnapshot>(), true, true, false);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Expect(bool condition, string message)
    {
        AssertionCount++;
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static async Task ExpectCancellationAsync(Task task, string message)
    {
        try { await task.WaitAsync(TestDeadline); }
        catch (OperationCanceledException) { Expect(true, message); return; }
        Expect(false, message);
    }

    private sealed class SnapshotSource : INetworkSnapshotProvider
    {
        public NetworkSnapshot Value { get; set; }
        public bool Throws { get; init; }
        public SnapshotSource(NetworkSnapshot? value = null) => Value = value ?? Network(new[] { Adapter() });
        public Task<NetworkSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Throws) { throw new IOException("Inspection denied."); }
            return Task.FromResult(Value);
        }
    }

    private sealed class WindowsSource : IWindowsConnectivitySource
    {
        private readonly int? _flags;
        public bool Throws { get; init; }
        public WindowsSource(int? flags) => _flags = flags;
        public Task<WindowsConnectivityObservation> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Throws) { throw new InvalidOperationException("Unavailable."); }
            return Task.FromResult(new WindowsConnectivityObservation(_flags, "Test Windows connectivity assessment."));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private int _calls;
        private int _active;
        private int _maximumActive;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public TaskCompletionSource<bool> Entered { get; } = Source<bool>();
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            if (active > _maximumActive) { _maximumActive = active; }
            Entered.TrySetResult(true);
            try { return await _respond(request, token); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class MustNotReadContent : HttpContent
    {
        public int Reads { get; private set; }
        public bool WasDisposed { get; private set; }
        public MustNotReadContent(long? length) { Headers.ContentLength = length; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            throw new InvalidOperationException("Response body must never be read.");
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;
        private DateTimeOffset _utc = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_timers) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() { lock (_timers) { return _utc; } }
        public void SetUtc(DateTimeOffset value) { lock (_timers) { _utc = value; } }
        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> due;
            lock (_timers)
            {
                _ticks += duration.Ticks;
                _utc += duration;
                due = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due) { timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue; }
            }
            foreach (ManualTimer timer in due) { timer.Fire(); }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_timers) { _timers.Add(timer); }
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class ManualTimer : ITimer
        {
            private readonly Clock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private bool _disposed;
            public long Due { get; set; } = long.MaxValue;
            public long Period { get; private set; }
            public ManualTimer(Clock clock, TimerCallback callback, object? state) { _clock = clock; _callback = callback; _state = state; }
            public bool Change(TimeSpan due, TimeSpan period)
            {
                lock (_clock._timers)
                {
                    if (_disposed) { return false; }
                    Due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock._ticks + due.Ticks;
                    Period = period.Ticks;
                    return true;
                }
            }
            public void Fire() { if (!_disposed) { _callback(_state); } }
            public void Dispose() { lock (_clock._timers) { _disposed = true; Due = long.MaxValue; _clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
