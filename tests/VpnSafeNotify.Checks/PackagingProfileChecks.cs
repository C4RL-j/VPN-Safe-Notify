using System;
using System.IO;
using System.Threading.Tasks;
using VpnSafeNotify.Infrastructure;

namespace VpnSafeNotify.Checks;

internal static class PackagingProfileChecks
{
    public static int AssertionCount { get; private set; }

    public static Task RunAsync()
    {
        AssertionCount = 0;
        Expect(PortableSmokeProfile.FromArguments(Array.Empty<string>(), _ => throw new Exception("Must not read smoke environment")) is null,
            "Ordinary launches ignore the internal smoke environment entirely.");
        ExpectInvalid(() => PortableSmokeProfile.FromArguments(new[] { "--portable-smoke" }, _ => null));
        string token = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "vpn-safe-smoke-" + token);
        Directory.CreateDirectory(directory);
        try
        {
            string? EnvironmentForTest(string key) => key.EndsWith("TOKEN", StringComparison.Ordinal) ? token : directory;
            var profile = PortableSmokeProfile.FromArguments(new[] { "--portable-smoke" }, EnvironmentForTest)!;
            Expect(profile.Token == token && profile.SettingsPath == Path.Combine(directory, "settings.json"),
                "The real published image can use a dedicated isolated settings file.");
            Expect(profile.StartupRegistryPath == @"Software\VpnSafeNotify\PortableSmoke\" + token,
                "Smoke startup writes cannot target the production Run key.");
            ExpectInvalid(() => PortableSmokeProfile.FromArguments(new[] { "--portable-smoke" },
                key => key.EndsWith("TOKEN", StringComparison.Ordinal) ? token : Path.GetTempPath()));
            ExpectInvalid(() => PortableSmokeProfile.FromArguments(new[] { "--portable-smoke" },
                key => key.EndsWith("TOKEN", StringComparison.Ordinal) ? "invalid" : directory));
            ExpectInvalid(() => new SingleInstanceService("unsafe-name"));
        }
        finally
        {
            // Only the just-created empty GUID directory is removed.
            Directory.Delete(directory, recursive: false);
        }
        return Task.CompletedTask;
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (ArgumentException) { AssertionCount++; return; }
        throw new InvalidOperationException("Invalid isolated profile was accepted.");
    }

    private static void Expect(bool condition, string message)
    {
        AssertionCount++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
