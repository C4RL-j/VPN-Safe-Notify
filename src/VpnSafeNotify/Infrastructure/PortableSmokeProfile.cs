using System;
using System.IO;

namespace VpnSafeNotify.Infrastructure;

/// <summary>
/// Explicit test-only profile for exercising the real published image without
/// acquiring the user's application lock or writing their preferences/Run value.
/// Ordinary launches never read these environment variables.
/// </summary>
public sealed record PortableSmokeProfile(string DirectoryPath, string Token)
{
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string StartupRegistryPath => @"Software\VpnSafeNotify\PortableSmoke\" + Token;

    public static PortableSmokeProfile? FromArguments(string[] arguments,
        Func<string, string?>? environment = null)
    {
        if (Array.IndexOf(arguments, "--portable-smoke") < 0) return null;
        environment ??= Environment.GetEnvironmentVariable;
        string? token = environment("VPN_SAFE_NOTIFY_SMOKE_TOKEN");
        string? directory = environment("VPN_SAFE_NOTIFY_SMOKE_DIRECTORY");
        if (!Guid.TryParseExact(token, "N", out _) || string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Portable smoke testing requires an explicit isolated profile and GUID token.");
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetFileName(fullPath), "vpn-safe-smoke-" + token, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullPath))
            throw new ArgumentException("The portable smoke profile must be an existing dedicated GUID directory.");
        return new(fullPath, token!);
    }
}
