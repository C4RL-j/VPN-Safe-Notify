using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class Program
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static int _assertions;

    private static async Task<int> Main(string[] arguments)
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("Warnings: stable snapshots, cooldown boundaries, monotonic time", CheckWarningTimingAsync),
            ("Warnings: unknown, offline, recovery, pause, flaps, notification toggles", CheckWarningTransitionsAsync),
            ("Screen-edge alerts: transitions, animation, cancellation and previews", EdgeAlertChecks.RunAsync),
            ("Daily use: timed pause, cancellation and tray presentation", DailyUseChecks.RunAsync),
            ("Packaging: isolated published-image smoke profile", PackagingProfileChecks.RunAsync),
            ("Settings: missing/corrupt preservation and normalization", CheckSettingsLoadingAsync),
            ("Settings: round trip, concurrent saves, cancellation and write failures", CheckSettingsSavingAsync),
            ("Daily settings: defaults, bounds, restarts and isolated per-user startup", DailySettingsChecks.RunAsync),
            ("VPN evidence: identities, exclusions, addresses and Windows VPN state", CheckEvidenceAsync),
            ("Internet: bounded reachability and conservative state classification", InternetChecks.RunAsync),
            ("VPN: tunnel activation and route evidence classification", VpnRouteChecks.RunAsync),
            ("Windows VPN native structure layout", CheckNativeLayoutAsync),
            ("Monitoring: snapshots, duplicate start, restart and stop", CheckMonitoringLifecycleAsync),
            ("Monitoring: network event debounce and subscription lifetime", CheckMonitoringNetworkEventsAsync),
            ("Monitoring: network changes cancel stale checks without overlap", CheckMonitoringStaleChecksAsync),
            ("Monitoring: native checks ignoring cancellation cannot overlap replacements", CheckMonitoringNoncooperativeChangesAsync),
            ("Monitoring: fallback checks never overlap", CheckMonitoringSerialFallbackAsync),
            ("Monitoring: detector failures become unknown", CheckMonitoringFailuresAsync),
            ("Monitoring: cancellation reaches detectors and stops publication", CheckMonitoringCancellationAsync),
            ("Monitoring: late noncooperative results cannot publish after stop", CheckMonitoringLateResultsAsync)
        };

        int failed = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL {check.Name}: {exception}");
            }
        }

        int passedGroups = checks.Length - failed;
        if (arguments.Contains("--live", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var network = new NetworkSnapshotProvider();
                using var connectivity = new ConnectivityService(network);
                using var changes = new SystemNetworkChangeSource();
                Action onChange = () => { };
                changes.Changed += onChange;
                Console.WriteLine($"LIVE Passive notifications: {changes.Status}");
                changes.Changed -= onChange;
                var internetTask = connectivity.CheckAsync(cancellation.Token);
                var vpnTask = new VpnDetectionService(new AppSettings(), network).CheckAsync(cancellation.Token);
                await Task.WhenAll(internetTask, vpnTask).WaitAsync(cancellation.Token);
                Console.WriteLine($"LIVE Internet={internetTask.Result.State}; VPN={vpnTask.Result.State}; evidence count={vpnTask.Result.Evidence.Count}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL live passive detection: {exception.GetType().Name}");
            }
        }

        Console.WriteLine($"{passedGroups}/{checks.Length} check groups passed; {_assertions + InternetChecks.AssertionCount + VpnRouteChecks.Assertions + EdgeAlertChecks.AssertionCount + DailySettingsChecks.AssertionCount + DailyUseChecks.AssertionCount + PackagingProfileChecks.AssertionCount} assertions; {failed} failures.");
        return failed == 0 ? 0 : 1;
    }

    private static Task CheckWarningTimingAsync()
    {
        var clock = new ManualTimeProvider();
        var policy = new WarningPolicy(30, clock);
        Expect(policy.ShouldWarn(Snapshot(), true), "A confirmed unsafe snapshot must warn after coordinator settling.");
        Expect(!policy.ShouldWarn(Snapshot(), true), "Repeated observations must respect cooldown.");
        clock.SetUtcNow(DateTimeOffset.Parse("2099-01-01T00:00:00Z"));
        Expect(!policy.ShouldWarn(Snapshot(), true), "Wall-clock jumps must not bypass cooldown.");
        clock.Advance(TimeSpan.FromMilliseconds(29999));
        Expect(!policy.ShouldWarn(Snapshot(), true), "Cooldown must not expire early.");
        clock.SetUtcNow(DateTimeOffset.Parse("2000-01-01T00:00:00Z"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Expect(policy.ShouldWarn(Snapshot(), true), "Monotonic cooldown must expire at the boundary despite wall-clock rollback.");

        var lowClock = new ManualTimeProvider();
        var minimum = new WarningPolicy(0, lowClock);
        Expect(minimum.ShouldWarn(Snapshot(), true), "Minimum cooldown still permits the first warning.");
        lowClock.Advance(TimeSpan.FromSeconds(29));
        Expect(!minimum.ShouldWarn(Snapshot(), true), "Minimum cooldown must clamp to 30 seconds.");
        lowClock.Advance(TimeSpan.FromSeconds(1));
        Expect(minimum.ShouldWarn(Snapshot(), true), "Minimum cooldown should expire after 30 seconds.");

        var highClock = new ManualTimeProvider();
        var maximum = new WarningPolicy(int.MaxValue, highClock);
        maximum.ShouldWarn(Snapshot(), true);
        highClock.Advance(TimeSpan.FromSeconds(3599));
        Expect(!maximum.ShouldWarn(Snapshot(), true), "Maximum cooldown must not expire before an hour.");
        highClock.Advance(TimeSpan.FromSeconds(1));
        Expect(maximum.ShouldWarn(Snapshot(), true), "Maximum cooldown must clamp to one hour.");
        return Task.CompletedTask;
    }

    private static Task CheckWarningTransitionsAsync()
    {
        var clock = new ManualTimeProvider();
        var policy = new WarningPolicy(30, clock);
        foreach (var internet in new[] { InternetState.Offline, InternetState.Unknown })
        {
            foreach (var vpn in Enum.GetValues<VpnState>())
            {
                Expect(!policy.ShouldWarn(Snapshot(internet, vpn), true), $"{internet} must never warn.");
                Expect(!policy.ShouldWarn(Snapshot(internet, vpn), true), $"Repeated {internet} must never warn.");
            }
        }

        Expect(policy.ShouldWarn(Snapshot(vpn: VpnState.Unverified), true), "Online with unverified VPN must warn after coordinator settling.");
        Expect(!policy.ShouldWarn(Snapshot(vpn: VpnState.Connected), true), "Connected VPN must suppress warnings.");
        clock.Advance(TimeSpan.FromSeconds(30));
        Expect(policy.ShouldWarn(Snapshot(), true), "A fresh confirmed unsafe snapshot after recovery must warn.");
        policy.Reset();
        Expect(!policy.ShouldWarn(Snapshot(), true), "Pause/reset must preserve cooldown.");
        clock.Advance(TimeSpan.FromSeconds(30));
        Expect(policy.ShouldWarn(Snapshot(), true), "Reset must not suppress warnings beyond cooldown.");
        Expect(!policy.ShouldWarn(Snapshot(InternetState.Offline), true), "Offline transition must not warn.");
        Expect(!policy.ShouldWarn(Snapshot(), true), "Network flap must preserve global cooldown.");
        clock.Advance(TimeSpan.FromSeconds(30));
        Expect(policy.ShouldWarn(Snapshot(), true), "Warning should resume after a network flap and cooldown.");
        Expect(!policy.ShouldWarn(Snapshot(), false), "Disabling notifications must suppress alerts.");
        Expect(!policy.ShouldWarn(Snapshot(), true), "Notification toggle must preserve cooldown.");
        clock.Advance(TimeSpan.FromSeconds(30));
        Expect(policy.ShouldWarn(Snapshot(), true), "Notification toggle must not permanently suppress alerts.");
        return Task.CompletedTask;
    }

    private static async Task CheckSettingsLoadingAsync()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "settings.json");
        var service = new SettingsService(path);
        var defaults = await service.LoadAsync();
        Equal(60, defaults.PollIntervalSeconds, "Missing settings must use a low-frequency fallback default.");
        Expect(defaults.MonitoringEnabled && defaults.NotificationsEnabled, "Default monitoring and notifications should be enabled.");
        Expect(defaults.WarnWhenVpnUnverified, "The unverified VPN warning setting must default to enabled on first launch.");
        Expect(service.LastError is null, "Missing settings are a normal first launch.");
        Expect(!File.Exists(path), "Loading missing settings must not create a file.");

        const string previousVersion = "{\"MonitoringEnabled\":false,\"NotificationsEnabled\":false,\"PollIntervalSeconds\":90,\"AlertCooldownSeconds\":120,\"AdditionalVpnAdapterKeywords\":[\" Existing Vendor \"]}";
        await File.WriteAllTextAsync(path, previousVersion);
        var migrated = await new SettingsService(path).LoadAsync();
        Expect(migrated.WarnWhenVpnUnverified, "A settings document created before the new toggle must default to enabled.");
        Expect(!migrated.MonitoringEnabled && !migrated.NotificationsEnabled, "Loading an older settings document must preserve existing boolean preferences.");
        Equal(90, migrated.PollIntervalSeconds, "Loading an older settings document must preserve the fallback interval.");
        Equal(120, migrated.AlertCooldownSeconds, "Loading an older settings document must preserve the notification cooldown.");
        Expect(migrated.AdditionalVpnAdapterKeywords.SequenceEqual(new[] { "Existing Vendor" }), "Older adapter keywords must continue to normalize normally.");
        Equal(previousVersion, await File.ReadAllTextAsync(path), "Loading older settings must not rewrite the existing document.");

        const string corrupt = "{ original corrupted contents";
        await File.WriteAllTextAsync(path, corrupt);
        defaults = await service.LoadAsync();
        Equal(60, defaults.PollIntervalSeconds, "Corruption must use defaults.");
        Expect(defaults.WarnWhenVpnUnverified, "Corrupt settings must use the enabled default for unverified VPN warnings.");
        Expect(!string.IsNullOrWhiteSpace(service.LastError), "Corruption must expose a useful error.");
        Equal(corrupt, await File.ReadAllTextAsync(path), "Loading corruption must preserve the original file.");
        await File.WriteAllTextAsync(path, "null");
        defaults = await service.LoadAsync();
        Expect(defaults.WarnWhenVpnUnverified, "JSON null settings must use the enabled default for unverified VPN warnings.");
        Expect(!string.IsNullOrWhiteSpace(service.LastError), "JSON null must expose an error.");
        Equal("null", await File.ReadAllTextAsync(path), "JSON null must be preserved during load.");

        var oversized = new AppSettings
        {
            WarnWhenVpnUnverified = false,
            PollIntervalSeconds = int.MinValue,
            AlertCooldownSeconds = int.MaxValue,
            AdditionalVpnAdapterKeywords = new[] { "  alpha ", "ALPHA", " ", new string('x', 90) }
                .Concat(Enumerable.Range(0, 40).Select(index => $" vendor-{index} ")).ToArray()
        };
        var original = JsonSerializer.Serialize(oversized);
        await File.WriteAllTextAsync(path, original);
        var normalized = await service.LoadAsync();
        Expect(!normalized.WarnWhenVpnUnverified, "Normalization must preserve an explicit disabled unverified VPN warning preference.");
        Equal(30, normalized.PollIntervalSeconds, "Fallback interval must clamp to thirty seconds.");
        Equal(3600, normalized.AlertCooldownSeconds, "Cooldown must clamp to one hour.");
        Equal(32, normalized.AdditionalVpnAdapterKeywords.Length, "Adapter keyword count must be bounded.");
        Equal("alpha", normalized.AdditionalVpnAdapterKeywords[0], "Keywords must be trimmed and deduplicated without case sensitivity.");
        Equal(64, normalized.AdditionalVpnAdapterKeywords[1].Length, "Keyword length must be bounded.");
        Expect(normalized.AdditionalVpnAdapterKeywords.All(keyword => keyword == keyword.Trim() && keyword.Length > 0), "Normalized keywords must be nonempty and trimmed.");
        Equal(original, await File.ReadAllTextAsync(path), "Normalization during load must not rewrite a file.");
        Expect(service.LastError is null, "A successful load must clear stale errors.");
    }

    private static async Task CheckSettingsSavingAsync()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "nested", "settings.json");
        var service = new SettingsService(path);
        var intended = new AppSettings
        {
            MonitoringEnabled = false,
            NotificationsEnabled = false,
            WarnWhenVpnUnverified = false,
            PollIntervalSeconds = 1000,
            AlertCooldownSeconds = -100,
            AdditionalVpnAdapterKeywords = new[] { " product ", "PRODUCT" }
        };
        Expect(await service.SaveAsync(intended), "Saving should create its settings directory.");
        var loaded = await new SettingsService(path).LoadAsync();
        Expect(!loaded.MonitoringEnabled && !loaded.NotificationsEnabled, "Boolean settings must round trip.");
        Expect(!loaded.WarnWhenVpnUnverified, "An explicit disabled unverified VPN warning preference must survive a new settings service instance.");
        Equal(600, loaded.PollIntervalSeconds, "Saved fallback interval must clamp to ten minutes.");
        Equal(30, loaded.AlertCooldownSeconds, "Saved cooldown must clamp to 30 seconds.");
        Expect(loaded.AdditionalVpnAdapterKeywords.SequenceEqual(new[] { "product" }), "Saved keywords must be normalized.");

        Expect(await service.SaveAsync(loaded with { WarnWhenVpnUnverified = true }), "The unverified VPN warning preference must be re-enableable.");
        var restarted = await new SettingsService(path).LoadAsync();
        Expect(restarted.WarnWhenVpnUnverified, "An explicit enabled unverified VPN warning preference must survive a new settings service instance.");
        Expect(!restarted.MonitoringEnabled && !restarted.NotificationsEnabled, "Changing the unverified warning preference must preserve monitoring and notification preferences.");
        Equal(loaded.PollIntervalSeconds, restarted.PollIntervalSeconds, "Changing the unverified warning preference must preserve the fallback interval.");
        Equal(loaded.AlertCooldownSeconds, restarted.AlertCooldownSeconds, "Changing the unverified warning preference must preserve the notification cooldown.");
        Expect(restarted.AdditionalVpnAdapterKeywords.SequenceEqual(loaded.AdditionalVpnAdapterKeywords), "Changing the unverified warning preference must preserve adapter keywords.");

        var concurrent = await Task.WhenAll(Enumerable.Range(30, 16)
            .Select(interval => service.SaveAsync(new AppSettings { PollIntervalSeconds = interval })));
        Expect(concurrent.All(saved => saved), "Concurrent saves must all complete safely.");
        loaded = await service.LoadAsync();
        Expect(loaded.PollIntervalSeconds is >= 30 and <= 45, "Concurrent writes must leave one complete valid settings document.");
        Expect(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Completed saves must leave no temporary files.");

        var original = await File.ReadAllTextAsync(path);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectCanceledAsync(() => service.LoadAsync(cancellation.Token));
            await ExpectCanceledAsync(() => service.SaveAsync(new AppSettings(), cancellation.Token));
        }
        Equal(original, await File.ReadAllTextAsync(path), "Canceled settings operations must preserve the existing document.");

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Expect(!await service.SaveAsync(new AppSettings()), "A sharing violation must return false rather than crash.");
            Expect(!string.IsNullOrWhiteSpace(service.LastError), "A write failure must expose an error.");
        }
        Equal(original, await File.ReadAllTextAsync(path), "A failed replacement must preserve the previous settings document.");
        Expect(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Failed saves must clean their own temporary files.");

        var blocker = Path.Combine(temporary.Path, "regular-file");
        await File.WriteAllTextAsync(blocker, "preserve");
        var unwritable = new SettingsService(Path.Combine(blocker, "settings.json"));
        Expect(!await unwritable.SaveAsync(new AppSettings()), "A non-directory parent must fail safely.");
        Expect(!string.IsNullOrWhiteSpace(unwritable.LastError), "A non-directory parent must provide an error.");
        Equal("preserve", await File.ReadAllTextAsync(blocker), "Settings failures must preserve unrelated files.");
    }

    private static Task CheckEvidenceAsync()
    {
        Expect(VpnEvidenceClassifier.IsConnectedRasVpn(0x2000, 0, "VPN"), "Connected RAS VPN must count as evidence.");
        Expect(!VpnEvidenceClassifier.IsConnectedRasVpn(0x2001, 0, "vpn"), "Disconnected RAS VPN must not count.");
        Expect(!VpnEvidenceClassifier.IsConnectedRasVpn(0x2000, 1, "vpn"), "RAS error must not count.");
        Expect(!VpnEvidenceClassifier.IsConnectedRasVpn(0x2000, 0, "modem"), "Dial-up must not count as VPN evidence.");
        Expect(!VpnEvidenceClassifier.IsConnectedRasVpn(0x2000, 0, "vpn", 1), "Reconnecting RAS substate must not count as fully connected.");
        Expect(VpnEvidenceClassifier.IsConnectedRasVpn(0x2000, 0, "vpn", 0x2000), "Connected RAS substate must count.");

        foreach (string identity in new[] { "NetMod", "WireGuard", "Wintun", "OpenVPN", "TAP-Windows Adapter", "Cisco AnyConnect", "tun0 tun" })
            Expect(VpnEvidenceClassifier.IsRecognizedVpnAdapter(identity, "", NetworkInterfaceType.Ethernet), $"Known adapter {identity} must be recognized.");
        foreach (string identity in new[] { "Intel Ethernet", "Wi-Fi", "Saturn Adapter", "Staples Network", "Hyper-V VPN", "VMware VPN", "vEthernet VPN", "Teredo VPN" })
            Expect(!VpnEvidenceClassifier.IsRecognizedVpnAdapter(identity, "", NetworkInterfaceType.Ethernet), $"Ordinary/excluded adapter {identity} must not count.");
        Expect(!VpnEvidenceClassifier.IsRecognizedVpnAdapter("VPN", "", NetworkInterfaceType.Loopback), "Loopback interface type must never count.");
        Expect(VpnEvidenceClassifier.IsRecognizedVpnAdapter("Custom Vendor", "", NetworkInterfaceType.Ethernet, new[] { " custom vendor " }), "Configured vendor keywords must be recognized.");
        foreach (string address in new[] { "0.0.0.0", "127.0.0.1", "169.254.2.3", "224.0.0.1", "255.255.255.255", "::", "::1", "fe80::1", "ff02::1", "::ffff:169.254.2.3" })
            Expect(!VpnEvidenceClassifier.IsMeaningfulAddress(IPAddress.Parse(address)), $"Unusable address {address} must not count.");
        foreach (string address in new[] { "10.0.0.1", "192.168.2.1", "100.64.0.1", "fd00::1", "2001:db8::1", "::ffff:10.0.0.1" })
            Expect(VpnEvidenceClassifier.IsMeaningfulAddress(IPAddress.Parse(address)), $"Usable address {address} must count.");
        return Task.CompletedTask;
    }

    private static Task CheckNativeLayoutAsync()
    {
        var connection = typeof(VpnDetectionService).GetNestedType("RasConnection", BindingFlags.NonPublic);
        var status = typeof(VpnDetectionService).GetNestedType("RasConnectionStatus", BindingFlags.NonPublic);
        Expect(connection is not null && status is not null, "RAS interop structures must exist.");
        Equal(IntPtr.Size == 8 ? 1388 : 1384, Marshal.SizeOf(connection!), "RASCONNW must use Windows SDK packing.");
        Equal(608, Marshal.SizeOf(status!), "RASCONNSTATUSW must match the Windows SDK size.");
        Equal(4, Marshal.OffsetOf(connection!, "Handle").ToInt32(), "RAS handle must follow the DWORD at 4-byte alignment.");
        Equal(564, Marshal.OffsetOf(status!, "LocalEndpoint").ToInt32(), "RAS endpoint offset must match Windows SDK layout.");
        Equal(604, Marshal.OffsetOf(status!, "SubState").ToInt32(), "RAS substate offset must match Windows SDK layout.");
        return Task.CompletedTask;
    }

    private static async Task CheckMonitoringLifecycleAsync()
    {
        var release = Source<bool>();
        var internetEntered = Source<bool>();
        var vpnEntered = Source<bool>();
        var internet = new DelegateConnectivity(async token =>
        {
            internetEntered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return new(InternetState.Online, "test online");
        });
        var vpn = new DelegateVpn(async token =>
        {
            vpnEntered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return new(VpnState.Connected, "test VPN", new[] { "synthetic evidence" });
        });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromMilliseconds(20),
            new ManualNetworkChanges(), TimeSpan.FromMilliseconds(10));
        var first = Source<MonitoringSnapshot>();
        int snapshots = 0;
        int publicationState = 0;
        monitoring.SnapshotAvailable += snapshot =>
        {
            Volatile.Write(ref publicationState, ReferenceEquals(snapshot, monitoring.LatestSnapshot) ? 1 : -1);
            Interlocked.Increment(ref snapshots);
            first.TrySetResult(snapshot);
        };
        await monitoring.StartAsync();
        await Task.WhenAll(internetEntered.Task, vpnEntered.Task).WaitAsync(Deadline);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitoring.StartAsync()));
        Expect(monitoring.IsRunning, "Start must mark monitoring as running.");
        Equal(1, internet.Calls, "Duplicate starts must not spawn connectivity workers.");
        Equal(1, vpn.Calls, "Duplicate starts must not spawn VPN workers.");
        release.TrySetResult(true);
        var snapshot = await first.Task.WaitAsync(Deadline);
        Equal(InternetState.Online, snapshot.Internet.State, "Snapshot must include connectivity results.");
        Equal(VpnState.Connected, snapshot.Vpn.State, "Snapshot must include VPN results.");
        Equal(1, snapshot.Vpn.Evidence.Count, "Snapshot must preserve evidence.");
        Expect(snapshot.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1), "Snapshot must be timestamped.");
        Equal(1, Volatile.Read(ref publicationState), "Published snapshot must be current at event delivery.");
        await monitoring.StopAsync().WaitAsync(Deadline);
        Expect(!monitoring.IsRunning && monitoring.LatestSnapshot is null, "Stop must clear running state and latest snapshot.");
        int stoppedCount = Volatile.Read(ref snapshots);
        await Task.Delay(100);
        Equal(stoppedCount, Volatile.Read(ref snapshots), "No snapshots may publish after Stop completes.");
        await monitoring.StopAsync().WaitAsync(Deadline);

        var restarted = Source<MonitoringSnapshot>();
        monitoring.SnapshotAvailable += value => restarted.TrySetResult(value);
        await monitoring.StartAsync();
        await restarted.Task.WaitAsync(Deadline);
        Expect(monitoring.IsRunning, "Monitoring must be restartable after pause.");
        await monitoring.StopAsync().WaitAsync(Deadline);
        Expect(internet.Calls >= 2 && vpn.Calls >= 2, "Restart must invoke both detectors again.");
    }

    private static async Task CheckMonitoringFailuresAsync()
    {
        var internet = new DelegateConnectivity(_ => throw new InvalidOperationException("synthetic connectivity failure"));
        var vpn = new DelegateVpn(_ => Task.FromException<VpnDetectionResult>(new InvalidOperationException("synthetic VPN failure")));
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromHours(1),
            new ManualNetworkChanges(), TimeSpan.FromMilliseconds(10));
        var observed = Source<MonitoringSnapshot>();
        monitoring.SnapshotAvailable += snapshot => observed.TrySetResult(snapshot);
        await monitoring.StartAsync();
        var snapshot = await observed.Task.WaitAsync(Deadline);
        Equal(InternetState.Unknown, snapshot.Internet.State, "Connectivity exceptions must become unknown.");
        Equal(VpnState.Unverified, snapshot.Vpn.State, "VPN exceptions must become unverified.");
        Expect(!snapshot.NeedsWarning, "Unknown internet must not trigger unsafe warning.");
        Expect(monitoring.IsRunning, "Detector failures must not terminate monitoring.");
        await monitoring.StopAsync().WaitAsync(Deadline);
    }

    private static async Task CheckMonitoringNetworkEventsAsync()
    {
        var changes = new ManualNetworkChanges();
        var internet = new DelegateConnectivity(_ => Task.FromResult(new ConnectivityResult(InternetState.Online, "stable")));
        var vpn = new DelegateVpn(_ => Task.FromResult(new VpnDetectionResult(VpnState.Disconnected, "stable", Array.Empty<string>())));
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromHours(1), changes,
            TimeSpan.FromMilliseconds(120));
        int publications = 0;
        monitoring.SnapshotAvailable += _ => Interlocked.Increment(ref publications);
        Equal(0, changes.SubscriberCount, "Paused monitoring must not subscribe to network events.");
        await monitoring.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref publications) == 1);
        Equal(1, changes.SubscriberCount, "Monitoring must attach exactly one network event subscription.");
        int baseline = internet.Calls;
        changes.Raise();
        await Task.Delay(40);
        changes.Raise();
        await Task.Delay(40);
        changes.Raise();
        await Task.Delay(40);
        Equal(baseline, internet.Calls, "A burst must settle before invoking network detectors.");
        await WaitUntilAsync(() => Volatile.Read(ref publications) == 2);
        await Task.Delay(150);
        Equal(baseline + 1, internet.Calls, "A network event burst must coalesce into one settled check.");
        Equal(internet.Calls, vpn.Calls, "Each settled network event check must refresh both statuses.");
        await monitoring.StopAsync().WaitAsync(Deadline);
        Equal(0, changes.SubscriberCount, "Stop must detach the network event subscription.");
        changes.Raise();
        await Task.Delay(150);
        Equal(baseline + 1, internet.Calls, "Network changes while paused must not trigger checks.");
        await monitoring.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref publications) == 3);
        Equal(1, changes.SubscriberCount, "Resume must attach one subscription again.");
        await monitoring.StopAsync().WaitAsync(Deadline);
        Equal(0, changes.SubscriberCount, "Stopping after resume must detach again.");
    }

    private static async Task CheckMonitoringStaleChecksAsync()
    {
        var changes = new ManualNetworkChanges();
        var internetEntered = Source<bool>();
        var vpnEntered = Source<bool>();
        var internetCanceled = Source<bool>();
        var vpnCanceled = Source<bool>();
        int internetGeneration = 0, vpnGeneration = 0, internetActive = 0, vpnActive = 0;
        int maximumInternetActive = 0, maximumVpnActive = 0;
        var internet = new DelegateConnectivity(async token =>
        {
            int active = Interlocked.Increment(ref internetActive);
            RecordMaximum(ref maximumInternetActive, active);
            try
            {
                if (Interlocked.Increment(ref internetGeneration) == 1)
                {
                    internetEntered.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { internetCanceled.TrySetResult(true); }
                    return new(InternetState.Offline, "obsolete network result");
                }
                return new(InternetState.Online, "replacement network result");
            }
            finally { Interlocked.Decrement(ref internetActive); }
        });
        var vpn = new DelegateVpn(async token =>
        {
            int active = Interlocked.Increment(ref vpnActive);
            RecordMaximum(ref maximumVpnActive, active);
            try
            {
                if (Interlocked.Increment(ref vpnGeneration) == 1)
                {
                    vpnEntered.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { vpnCanceled.TrySetResult(true); }
                    return new(VpnState.Disconnected, "obsolete network result", Array.Empty<string>());
                }
                return new(VpnState.Connected, "replacement network result", new[] { "synthetic active tunnel" });
            }
            finally { Interlocked.Decrement(ref vpnActive); }
        });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromHours(1), changes,
            TimeSpan.FromMilliseconds(120));
        var publication = Source<MonitoringSnapshot>();
        int publications = 0;
        monitoring.SnapshotAvailable += value =>
        {
            Interlocked.Increment(ref publications);
            publication.TrySetResult(value);
        };
        await monitoring.StartAsync();
        await Task.WhenAll(internetEntered.Task, vpnEntered.Task).WaitAsync(Deadline);
        changes.Raise();
        await Task.WhenAll(internetCanceled.Task, vpnCanceled.Task).WaitAsync(Deadline);
        changes.Raise();
        await Task.Delay(30);
        changes.Raise();
        var snapshot = await publication.Task.WaitAsync(Deadline);
        Equal(InternetState.Online, snapshot.Internet.State, "A superseded connectivity result must never be published.");
        Equal(VpnState.Connected, snapshot.Vpn.State, "A superseded VPN result must never be published.");
        Equal(1, Volatile.Read(ref publications), "Canceled checks returning old results must not publish a warning candidate.");
        Equal(2, internet.Calls, "The event burst must produce only one replacement internet check.");
        Equal(2, vpn.Calls, "The event burst must produce only one replacement VPN check.");
        Equal(1, maximumInternetActive, "Internet checks must not overlap across network changes.");
        Equal(1, maximumVpnActive, "VPN checks must not overlap across network changes.");
        await monitoring.StopAsync().WaitAsync(Deadline);
    }

    private static async Task CheckMonitoringSerialFallbackAsync()
    {
        int internetActive = 0, vpnActive = 0, maximumInternetActive = 0, maximumVpnActive = 0;
        var internet = new DelegateConnectivity(async token =>
        {
            int active = Interlocked.Increment(ref internetActive);
            RecordMaximum(ref maximumInternetActive, active);
            try
            {
                await Task.Delay(80, token);
                return new(InternetState.Online, "slow fallback");
            }
            finally { Interlocked.Decrement(ref internetActive); }
        });
        var vpn = new DelegateVpn(async token =>
        {
            int active = Interlocked.Increment(ref vpnActive);
            RecordMaximum(ref maximumVpnActive, active);
            try
            {
                await Task.Delay(80, token);
                return new(VpnState.Disconnected, "slow fallback", Array.Empty<string>());
            }
            finally { Interlocked.Decrement(ref vpnActive); }
        });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromMilliseconds(25),
            new ManualNetworkChanges(), TimeSpan.FromMilliseconds(10));
        int publications = 0;
        monitoring.SnapshotAvailable += _ => Interlocked.Increment(ref publications);
        await monitoring.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref publications) >= 3);
        await monitoring.StopAsync().WaitAsync(Deadline);
        Expect(internet.Calls >= 3 && vpn.Calls >= 3, "Fallback must refresh both detectors without network events.");
        Equal(1, maximumInternetActive, "A slow internet detector must not overlap its next fallback.");
        Equal(1, maximumVpnActive, "A slow VPN detector must not overlap its next fallback.");
        Equal(0, internetActive, "Stop must cancel an active fallback internet check.");
        Equal(0, vpnActive, "Stop must cancel an active fallback VPN check.");
    }

    private static async Task CheckMonitoringNoncooperativeChangesAsync()
    {
        var changes = new ManualNetworkChanges();
        var release = Source<bool>();
        var internetEntered = Source<bool>();
        var vpnEntered = Source<bool>();
        int internetGeneration = 0, vpnGeneration = 0, internetActive = 0, vpnActive = 0;
        int maximumInternetActive = 0, maximumVpnActive = 0;
        var internet = new DelegateConnectivity(async _ =>
        {
            int active = Interlocked.Increment(ref internetActive);
            RecordMaximum(ref maximumInternetActive, active);
            try
            {
                if (Interlocked.Increment(ref internetGeneration) == 1)
                {
                    internetEntered.TrySetResult(true);
                    await release.Task;
                    return new(InternetState.Offline, "obsolete native result");
                }
                return new(InternetState.Online, "fresh native result");
            }
            finally { Interlocked.Decrement(ref internetActive); }
        });
        var vpn = new DelegateVpn(async _ =>
        {
            int active = Interlocked.Increment(ref vpnActive);
            RecordMaximum(ref maximumVpnActive, active);
            try
            {
                if (Interlocked.Increment(ref vpnGeneration) == 1)
                {
                    vpnEntered.TrySetResult(true);
                    await release.Task;
                    return new(VpnState.Disconnected, "obsolete native result", Array.Empty<string>());
                }
                return new(VpnState.Connected, "fresh native result", new[] { "synthetic active tunnel" });
            }
            finally { Interlocked.Decrement(ref vpnActive); }
        });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromHours(1), changes,
            TimeSpan.FromMilliseconds(50));
        var publication = Source<MonitoringSnapshot>();
        int publications = 0;
        monitoring.SnapshotAvailable += value =>
        {
            Interlocked.Increment(ref publications);
            publication.TrySetResult(value);
        };
        await monitoring.StartAsync();
        await Task.WhenAll(internetEntered.Task, vpnEntered.Task).WaitAsync(Deadline);
        changes.Raise();
        try
        {
            await Task.Delay(180);
            Equal(1, internet.Calls, "A replacement internet check must wait for a noncooperative old operation.");
            Equal(1, vpn.Calls, "A replacement VPN check must wait for a noncooperative old operation.");
            Equal(0, Volatile.Read(ref publications), "An obsolete in-flight native observation must not publish.");
        }
        finally { release.TrySetResult(true); }
        var snapshot = await publication.Task.WaitAsync(Deadline);
        Equal(InternetState.Online, snapshot.Internet.State, "Fresh connectivity must replace the obsolete native result.");
        Equal(VpnState.Connected, snapshot.Vpn.State, "Fresh VPN status must replace the obsolete native result.");
        Equal(1, maximumInternetActive, "Native internet checks must stay serial through cancellation.");
        Equal(1, maximumVpnActive, "Native VPN checks must stay serial through cancellation.");
        Equal(1, Volatile.Read(ref publications), "Only the replacement native result may publish.");
        await monitoring.StopAsync().WaitAsync(Deadline);
    }

    private static void RecordMaximum(ref int maximum, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (value <= observed) return;
        } while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(10, cancellation.Token);
    }

    private static async Task CheckMonitoringCancellationAsync()
    {
        // Parent/linked cancellation callbacks can run in either order. Repeating this
        // short case covers the race between stopping the worker and canceling detectors.
        for (int iteration = 0; iteration < 24; iteration++)
            await CheckMonitoringCancellationOnceAsync();
    }

    private static async Task CheckMonitoringCancellationOnceAsync()
    {
        var internetEntered = Source<bool>();
        var vpnEntered = Source<bool>();
        var internetCanceled = Source<bool>();
        var vpnCanceled = Source<bool>();
        CancellationToken internetToken = default;
        CancellationToken vpnToken = default;
        var internet = new DelegateConnectivity(async token =>
        {
            internetToken = token;
            internetEntered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) internetCanceled.TrySetResult(true); }
            return new(InternetState.Online, "unreachable");
        });
        var vpn = new DelegateVpn(async token =>
        {
            vpnToken = token;
            vpnEntered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) vpnCanceled.TrySetResult(true); }
            return new(VpnState.Disconnected, "unreachable", Array.Empty<string>());
        });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromHours(1),
            new ManualNetworkChanges(), TimeSpan.FromMilliseconds(10));
        int snapshots = 0;
        monitoring.SnapshotAvailable += _ => Interlocked.Increment(ref snapshots);
        await monitoring.StartAsync();
        await Task.WhenAll(internetEntered.Task, vpnEntered.Task).WaitAsync(Deadline);
        await monitoring.StopAsync().WaitAsync(Deadline);
        Expect(internetToken.IsCancellationRequested && vpnToken.IsCancellationRequested,
            "Stop must cancel the actual detector tokens before discarding the linked cancellation source.");
        await Task.WhenAll(internetCanceled.Task, vpnCanceled.Task).WaitAsync(Deadline);
        Equal(0, snapshots, "Canceled in-flight detectors must not publish snapshots.");
        Expect(!monitoring.IsRunning, "Canceled monitoring must stop promptly.");
    }

    private static async Task CheckMonitoringLateResultsAsync()
    {
        var internetResult = Source<ConnectivityResult>();
        var vpnResult = Source<VpnDetectionResult>();
        var internetEntered = Source<bool>();
        var vpnEntered = Source<bool>();
        var internet = new DelegateConnectivity(_ => { internetEntered.TrySetResult(true); return internetResult.Task; });
        var vpn = new DelegateVpn(_ => { vpnEntered.TrySetResult(true); return vpnResult.Task; });
        await using var monitoring = new MonitoringService(internet, vpn, TimeSpan.FromMilliseconds(20),
            new ManualNetworkChanges(), TimeSpan.FromMilliseconds(10));
        int snapshots = 0;
        monitoring.SnapshotAvailable += _ => Interlocked.Increment(ref snapshots);
        await monitoring.StartAsync();
        await Task.WhenAll(internetEntered.Task, vpnEntered.Task).WaitAsync(Deadline);
        await monitoring.StopAsync().WaitAsync(Deadline);
        internetResult.TrySetResult(new(InternetState.Online, "late"));
        vpnResult.TrySetResult(new(VpnState.Disconnected, "late", Array.Empty<string>()));
        await Task.Delay(100);
        Equal(0, snapshots, "Late results from noncooperative native operations must not publish after stop.");
        Expect(monitoring.LatestSnapshot is null, "Late native results must not repopulate cleared state.");
    }

    private static MonitoringSnapshot Snapshot(InternetState internet = InternetState.Online,
        VpnState vpn = VpnState.Disconnected) => new(new(internet, "synthetic"),
        new(vpn, "synthetic", Array.Empty<string>()), DateTimeOffset.UtcNow);

    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExpectCanceledAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { _assertions++; return; }
        throw new InvalidOperationException("A canceled operation must throw OperationCanceledException.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Expect(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Expected {expected}; got {actual}.");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private long _utcTicks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            Interlocked.Add(ref _utcTicks, duration.Ticks);
        }
    }

    private sealed class DelegateConnectivity : IConnectivityService
    {
        private readonly Func<CancellationToken, Task<ConnectivityResult>> _check;
        private int _calls;
        public DelegateConnectivity(Func<CancellationToken, Task<ConnectivityResult>> check) => _check = check;
        public int Calls => Volatile.Read(ref _calls);
        public Task<ConnectivityResult> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _check(cancellationToken);
        }
    }

    private sealed class DelegateVpn : IVpnDetectionService
    {
        private readonly Func<CancellationToken, Task<VpnDetectionResult>> _check;
        private int _calls;
        public DelegateVpn(Func<CancellationToken, Task<VpnDetectionResult>> check) => _check = check;
        public int Calls => Volatile.Read(ref _calls);
        public Task<VpnDetectionResult> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _check(cancellationToken);
        }
    }

    private sealed class ManualNetworkChanges : INetworkChangeSource
    {
        private Action? _changed;
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
        public event Action? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }
        public void Raise() => _changed?.Invoke();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _expectedPath;
        public TemporaryDirectory()
        {
            _expectedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"VpnSafeNotify.Checks-{Guid.NewGuid():N}"));
            Path = _expectedPath;
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            string expectedParent = System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(resolved, _expectedPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(System.IO.Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(resolved).StartsWith("VpnSafeNotify.Checks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside this check runner's exact owned temporary path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
