using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class DailySettingsChecks
{
    public static int AssertionCount { get; private set; }

    public static async Task RunAsync()
    {
        using var temporary = new OwnedTemporaryDirectory();
        string settingsPath = Path.Combine(temporary.Path, "settings.json");
        var settings = new SettingsService(settingsPath);
        var defaults = await settings.LoadAsync();
        CheckDefaults(defaults, "A missing settings file");
        Expect(!File.Exists(settingsPath), "Reading first-launch defaults must not create settings.");

        const string legacy = "{\"MonitoringEnabled\":false,\"NotificationsEnabled\":false,\"WarnWhenVpnUnverified\":false,\"PollIntervalSeconds\":90,\"AlertCooldownSeconds\":120,\"AdditionalVpnAdapterKeywords\":[\" Vendor \x20\"]}";
        await File.WriteAllTextAsync(settingsPath, legacy);
        var migrated = await new SettingsService(settingsPath).LoadAsync();
        CheckDailyDefaults(migrated, "A pre-daily-settings document");
        Expect(!migrated.MonitoringEnabled && !migrated.NotificationsEnabled && !migrated.WarnWhenVpnUnverified,
            "Adding daily settings must preserve all existing disabled boolean preferences.");
        Equal(90, migrated.PollIntervalSeconds, "Legacy fallback interval must be preserved.");
        Equal(120, migrated.AlertCooldownSeconds, "Legacy notification cooldown must be preserved.");
        Expect(migrated.AdditionalVpnAdapterKeywords.SequenceEqual(new[] { "Vendor" }),
            "Legacy detection keywords must still normalize.");
        Equal(legacy, await File.ReadAllTextAsync(settingsPath), "Reading legacy settings must not overwrite their file.");

        CheckBounds();
        Expect(await settings.SaveAsync(new AppSettings { BorderOpacity = double.NaN }),
            "An invalid in-memory opacity must normalize before JSON serialization.");
        Equal(0.27, (await new SettingsService(settingsPath).LoadAsync()).BorderOpacity,
            "A nonfinite in-memory opacity must persist as the finite default.");

        var custom = new AppSettings
        {
            MonitoringEnabled = false,
            NotificationsEnabled = false,
            WarnWhenVpnUnverified = false,
            RedAlertDurationSeconds = 7,
            RepeatIntervalSeconds = 15,
            BorderThickness = 153,
            BorderOpacity = 0.58,
            AllMonitors = false,
            WarningSoundEnabled = true,
            LaunchAtSignIn = true,
            StartMinimized = true,
            PollIntervalSeconds = 90,
            AlertCooldownSeconds = 120,
            AdditionalVpnAdapterKeywords = new[] { "Vendor One", "Vendor Two" }
        };
        Expect(await settings.SaveAsync(custom), "Saving all custom daily settings must succeed.");
        var restarted = await new SettingsService(settingsPath).LoadAsync();
        Equal(JsonSerializer.Serialize(custom), JsonSerializer.Serialize(restarted),
            "Every setting must survive a newly constructed service, including startup and all-monitor choices.");
        Expect(settings.LastError is null, "A successful settings save must leave no error.");

        Expect(await new SettingsService(settingsPath).SaveAsync(new AppSettings()),
            "Persisting restored defaults must replace custom daily preferences.");
        var restored = await new SettingsService(settingsPath).LoadAsync();
        CheckDefaults(restored, "Restored defaults after restart");
        Equal(JsonSerializer.Serialize(new AppSettings()), JsonSerializer.Serialize(restored),
            "Restore defaults must clear all custom settings, including detection keyword additions.");

        await File.WriteAllTextAsync(settingsPath, "{\"BorderOpacity\":\"invalid\",\"LaunchAtSignIn\":true}");
        var invalidService = new SettingsService(settingsPath);
        CheckDefaults(await invalidService.LoadAsync(), "An invalid settings value");
        Expect(!string.IsNullOrWhiteSpace(invalidService.LastError), "Invalid types must produce a recoverable settings explanation.");
        Equal("{\"BorderOpacity\":\"invalid\",\"LaunchAtSignIn\":true}", await File.ReadAllTextAsync(settingsPath),
            "Invalid settings must be preserved for recovery, never silently rewritten on load.");

        CheckStartupResolution(temporary.Path);
        CheckStartup(temporary.Path);
    }

    private static void CheckBounds()
    {
        var low = SettingsService.Normalize(new AppSettings
        {
            RedAlertDurationSeconds = int.MinValue,
            RepeatIntervalSeconds = int.MinValue,
            BorderThickness = int.MinValue,
            BorderOpacity = -100,
            AllMonitors = false,
            WarningSoundEnabled = true,
            LaunchAtSignIn = true,
            StartMinimized = true
        });
        Equal(5, low.RedAlertDurationSeconds, "Red duration must clamp at five seconds.");
        Equal(15, low.RepeatIntervalSeconds, "Repeat interval must clamp at fifteen seconds.");
        Equal(24, low.BorderThickness, "Border thickness must clamp at twenty-four DIPs.");
        Equal(0.05, low.BorderOpacity, "Border opacity must retain a faint visible minimum.");
        Expect(!low.AllMonitors && low.WarningSoundEnabled && low.LaunchAtSignIn && low.StartMinimized,
            "Normalization must preserve valid non-default boolean choices.");
        var high = SettingsService.Normalize(new AppSettings
        {
            RedAlertDurationSeconds = int.MaxValue,
            RepeatIntervalSeconds = int.MaxValue,
            BorderThickness = int.MaxValue,
            BorderOpacity = double.MaxValue
        });
        Equal(7, high.RedAlertDurationSeconds, "Red duration must clamp at seven seconds.");
        Equal(300, high.RepeatIntervalSeconds, "Repeat interval must clamp at five minutes.");
        Equal(180, high.BorderThickness, "Border thickness must stay bounded.");
        Equal(0.65, high.BorderOpacity, "Border opacity must stay translucent.");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Equal(0.27, SettingsService.Normalize(new AppSettings { BorderOpacity = invalid }).BorderOpacity,
                "Nonfinite opacity must recover to the default before JSON serialization or rendering.");

        foreach (int duration in new[] { 5, 6, 7 })
            Equal(duration, SettingsService.Normalize(new AppSettings { RedAlertDurationSeconds = duration }).RedAlertDurationSeconds,
                "Every allowed alert duration must remain exact.");
        foreach (int interval in new[] { 15, 60, 300 })
            Equal(interval, SettingsService.Normalize(new AppSettings { RepeatIntervalSeconds = interval }).RepeatIntervalSeconds,
                "Allowed repeat interval boundaries must remain exact.");
    }

    private static void CheckStartupResolution(string temporaryPath)
    {
        string baseDirectory = Path.Combine(temporaryPath, "Development output");
        string fallback = Path.Combine(baseDirectory, "VpnSafeNotify.exe");
        string portable = Path.Combine(temporaryPath, "Portable folder \u6d4b\u8bd5", "VPN Safe Notify.exe");
        Equal(portable, StartupService.ResolveExecutablePath(portable, baseDirectory, "VpnSafeNotify"),
            "A renamed single-file app must use its actual process path, even when the base directory differs.");
        Equal(portable.ToUpperInvariant(), StartupService.ResolveExecutablePath(portable.ToUpperInvariant(),
            baseDirectory, "VpnSafeNotify"), "An uppercase EXE extension must remain a valid apphost.");
        Equal(fallback, StartupService.ResolveExecutablePath(Path.Combine(temporaryPath, "dotnet.exe"),
            baseDirectory, "VpnSafeNotify"), "Running the app DLL through dotnet must resolve the adjacent apphost.");
        Equal(fallback, StartupService.ResolveExecutablePath(Path.Combine(temporaryPath, "DOTNET.EXE"),
            baseDirectory, "VpnSafeNotify"), "The dotnet host exclusion must ignore filename case.");
        Equal(fallback, StartupService.ResolveExecutablePath(Path.Combine(temporaryPath, "VpnSafeNotify.Checks.exe"),
            baseDirectory, "VpnSafeNotify.Checks"), "A check runner must never register its own process path.");
        Equal(fallback, StartupService.ResolveExecutablePath(Path.Combine(temporaryPath, "testhost.exe"),
            baseDirectory, "TestPlatform"), "An external test host must never become the startup executable.");
        Equal(fallback, StartupService.ResolveExecutablePath(null, baseDirectory, "VpnSafeNotify"),
            "A missing process path must retain the development apphost fallback.");
        Equal(fallback, StartupService.ResolveExecutablePath(" ", baseDirectory, "VpnSafeNotify"),
            "An empty process path must retain the apphost fallback.");
        Equal(fallback, StartupService.ResolveExecutablePath(portable, baseDirectory, null),
            "An unknown entry assembly must not register an unrelated process.");
        Equal(fallback, StartupService.ResolveExecutablePath(Path.ChangeExtension(portable, ".dll"),
            baseDirectory, "VpnSafeNotify"), "A managed DLL cannot be a direct startup apphost.");
    }

    private static void CheckStartup(string temporaryPath)
    {
        // Never write the production Run value. This entire test owns one randomly
        // named subtree under the current user's non-startup test key.
        string testRoot = @"Software\VpnSafeNotify.Tests\" + Guid.NewGuid().ToString("N");
        string executable = Path.Combine(temporaryPath, "Application folder \u6d4b\u8bd5", "VpnSafeNotify.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, Array.Empty<byte>()); // Not executed.
        const string valueName = "VPN Safe Notify checks";
        var startup = new StartupService(executable, testRoot, valueName);
        try
        {
            Equal(false, startup.IsEnabled(), "A missing per-user registration must be disabled.");
            Expect(startup.LastError is null, "An absent startup value is not an error.");
            Expect(startup.TrySetEnabled(false), "Disabling a missing registration must be harmless.");
            using (var absent = Registry.CurrentUser.OpenSubKey(testRoot))
                Expect(absent is null, "Disabling absent startup must not create a registry key.");

            using (var key = Registry.CurrentUser.CreateSubKey(testRoot, writable: true))
                key.SetValue("Adjacent application", "preserve this value", RegistryValueKind.String);
            Expect(startup.TrySetEnabled(true), "An existing Unicode apphost must register without elevation.");
            Equal(true, startup.IsEnabled(), "Registered command must be reported as enabled.");
            using (var key = Registry.CurrentUser.OpenSubKey(testRoot))
            {
                Equal($"\"{Path.GetFullPath(executable)}\"", key!.GetValue(valueName) as string,
                    "The complete executable path must be exactly quoted once, preserving spaces and Unicode.");
                Equal(RegistryValueKind.String, key.GetValueKind(valueName), "Startup must use a literal REG_SZ command.");
                Equal("preserve this value", key.GetValue("Adjacent application") as string,
                    "Enabling startup must preserve neighboring values.");
            }
            Equal(true, new StartupService(executable, testRoot, valueName).IsEnabled(),
                "A new startup service must read the persisted per-user command.");
            Expect(startup.TrySetEnabled(true), "Enabling startup twice must be idempotent.");
            Expect(startup.TrySetEnabled(false), "Disabling startup must succeed.");
            Equal(false, startup.IsEnabled(), "Disabled startup must stay disabled for a new read.");
            using (var key = Registry.CurrentUser.OpenSubKey(testRoot))
            {
                Expect(key!.GetValue(valueName) is null, "Disable must remove this application's registration.");
                Equal("preserve this value", key.GetValue("Adjacent application") as string,
                    "Disabling startup must preserve neighboring application registrations.");
            }

            var unavailable = new StartupService(Path.Combine(temporaryPath, "missing.exe"), testRoot, valueName);
            Expect(!unavailable.TrySetEnabled(true), "Missing executable must not register a broken startup command.");
            Expect(!string.IsNullOrWhiteSpace(unavailable.LastError), "Missing apphost must expose an actionable startup error.");
            Expect(unavailable.TrySetEnabled(false), "Removing an entry must work even after its executable has disappeared.");
            Expect(unavailable.LastError is null, "Successful disable must clear a prior enable error.");

            foreach (string invalidPath in new[] { executable + "\" --injected", Path.Combine(temporaryPath, "application.dll"), "\r\n.exe" })
            {
                var invalid = new StartupService(invalidPath, testRoot, valueName);
                Expect(!invalid.TrySetEnabled(true), "A malformed or non-executable startup command must be rejected.");
                Expect(invalid.IsEnabled() is null && !string.IsNullOrWhiteSpace(invalid.LastError),
                    "An invalid expected startup command must return an unknown read, with an explanation.");
            }
            var tooLong = new StartupService(Path.Combine(temporaryPath, new string('p', 270), "VpnSafeNotify.exe"), testRoot, valueName);
            Expect(!tooLong.TrySetEnabled(true), "Commands exceeding the Run key's documented limit must be rejected.");
            Expect(tooLong.LastError?.Contains("shorter folder", StringComparison.OrdinalIgnoreCase) == true,
                "An overlong startup command must explain how to recover.");

            // A default production resolution must point beside the app even though
            // this check runner itself is invoked as 'dotnet VpnSafeNotify.Checks.dll'.
            var defaultHost = new StartupService(registrySubKey: testRoot, valueName: valueName);
            Expect(defaultHost.TrySetEnabled(true), "Default startup resolution must use the built apphost beside the DLLs.");
            using (var key = Registry.CurrentUser.OpenSubKey(testRoot))
                Equal($"\"{Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "VpnSafeNotify.exe"))}\"",
                    key!.GetValue(valueName) as string, "Sign-in must launch VpnSafeNotify.exe, never dotnet.exe or the check runner.");
            Expect(defaultHost.TrySetEnabled(false), "Default-host test registration must be removable.");

            string friendlyExecutable = Path.Combine(Path.GetDirectoryName(executable)!, "VPN Safe Notify.exe");
            File.WriteAllBytes(friendlyExecutable, Array.Empty<byte>()); // Never executed.
            string resolvedPortable = StartupService.ResolveExecutablePath(friendlyExecutable,
                Path.Combine(temporaryPath, "Different extraction directory"), "VpnSafeNotify");
            var renamedHost = new StartupService(resolvedPortable, testRoot, valueName);
            Expect(renamedHost.TrySetEnabled(true), "A friendly renamed portable apphost must register without elevation.");
            using (var key = Registry.CurrentUser.OpenSubKey(testRoot))
                Equal($"\"{Path.GetFullPath(friendlyExecutable)}\"", key!.GetValue(valueName) as string,
                    "The sign-in command must quote the actual renamed portable EXE, never a nonexistent adjacent filename.");
            Equal(true, new StartupService(resolvedPortable, testRoot, valueName).IsEnabled(),
                "A renamed portable apphost must recognize its persisted registration after restart.");
            Expect(renamedHost.TrySetEnabled(false), "The renamed portable apphost's registration must be removable.");

            using (var key = Registry.CurrentUser.OpenSubKey(testRoot, writable: true))
                key!.SetValue(valueName, executable, RegistryValueKind.String);
            Equal(false, startup.IsEnabled(), "An ambiguous unquoted command must not be considered correctly configured.");
            Expect(startup.TrySetEnabled(false), "A malformed existing command must still be removable.");

            var unreadable = new StartupService(executable, testRoot + "\\" + new string('x', 256), valueName);
            Expect(unreadable.IsEnabled() is null, "An invalid registry location must recover as unknown, not crash.");
            Expect(!string.IsNullOrWhiteSpace(unreadable.LastError), "A registry read failure must expose its reason.");
            Expect(!unreadable.TrySetEnabled(true), "Invalid registry write paths must fail safely.");
        }
        finally
        {
            // Only this exact GUID-named test subtree is ever deleted.
            if (!testRoot.StartsWith(@"Software\VpnSafeNotify.Tests\", StringComparison.Ordinal) ||
                !Guid.TryParseExact(testRoot[(@"Software\VpnSafeNotify.Tests\").Length..], "N", out _))
                throw new InvalidOperationException("Refusing to remove an unowned registry test path.");
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
        using var cleaned = Registry.CurrentUser.OpenSubKey(testRoot);
        Expect(cleaned is null, "The complete owned startup test subtree must be removed after checks.");
    }

    private static void CheckDefaults(AppSettings settings, string source)
    {
        CheckDailyDefaults(settings, source);
        Expect(settings.MonitoringEnabled && settings.NotificationsEnabled && settings.WarnWhenVpnUnverified,
            $"{source} must preserve the established monitoring and notification defaults.");
        Equal(60, settings.PollIntervalSeconds, $"{source} must keep the low-frequency detection fallback.");
        Equal(60, settings.AlertCooldownSeconds, $"{source} must keep the notification cooldown.");
        Equal(0, settings.AdditionalVpnAdapterKeywords.Length, $"{source} must have no custom detector keywords.");
    }

    private static void CheckDailyDefaults(AppSettings settings, string source)
    {
        Equal(6, settings.RedAlertDurationSeconds, $"{source} must default to a six-second red effect.");
        Equal(60, settings.RepeatIntervalSeconds, $"{source} must default to a sixty-second repeat.");
        Equal(96, settings.BorderThickness, $"{source} must default to a moderate edge thickness.");
        Equal(0.27, settings.BorderOpacity, $"{source} must default to a soft translucent edge.");
        Expect(settings.AllMonitors, $"{source} must default to all monitors.");
        Expect(!settings.WarningSoundEnabled, $"{source} must default warning sound off.");
        Expect(!settings.LaunchAtSignIn, $"{source} must default sign-in launch off.");
        Expect(!settings.StartMinimized, $"{source} must default to a visible first launch.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        AssertionCount++;
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Expect(Equals(expected, actual), $"{message} Expected {expected}; got {actual}.");

    private sealed class OwnedTemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "VpnSafeNotify.DailyChecks-" + Guid.NewGuid().ToString("N")));
        public OwnedTemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string parent = System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(Path), parent, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(Path).StartsWith("VpnSafeNotify.DailyChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unowned temporary directory.");
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
