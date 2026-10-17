using System;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace VpnSafeNotify.Services;

/// <summary>Manages this application's sign-in entry for the current Windows user.</summary>
public sealed class StartupService
{
    private const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "VPN Safe Notify";
    private readonly string _executablePath;
    private readonly string _registrySubKey;
    private readonly string _valueName;

    // Optional locations let checks use their own HKCU subtree and temporary apphost;
    // production callers use the current user's Run value and the running apphost.
    public StartupService(string? executablePath = null, string? registrySubKey = null,
        string? valueName = null)
    {
        _executablePath = executablePath ?? ResolveExecutablePath(Environment.ProcessPath,
            AppContext.BaseDirectory, Assembly.GetEntryAssembly()?.GetName().Name);
        _registrySubKey = registrySubKey ?? DefaultSubKey;
        _valueName = valueName ?? DefaultValueName;
    }

    public string? LastError { get; private set; }

    /// <summary>
    /// A single-file distribution may have a friendly, renamed executable. Its actual
    /// process path is the durable startup target, not a DLL extraction directory.
    /// Development DLL hosts and check runners must never register themselves.
    /// </summary>
    public static string ResolveExecutablePath(string? processPath, string baseDirectory,
        string? entryAssemblyName)
    {
        if (string.Equals(entryAssemblyName, "VpnSafeNotify", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        return Path.Combine(baseDirectory, "VpnSafeNotify.exe");
    }

    /// <summary>
    /// Whether this app's correctly quoted command is registered. This does not override
    /// Windows Startup Apps policy or promise when Windows will launch the command.
    /// </summary>
    public bool? IsEnabled()
    {
        LastError = null;
        try
        {
            string command = GetCommand();
            using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: false);
            return key?.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is string existing && string.Equals(existing, command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsStartupError(exception))
        {
            LastError = $"Could not read the sign-in setting. {exception.Message}";
            return null;
        }
    }

    public bool TrySetEnabled(bool enabled)
    {
        LastError = null;
        try
        {
            if (enabled)
            {
                string command = GetCommand();
                if (!File.Exists(Path.GetFullPath(_executablePath)))
                {
                    LastError = "Could not enable sign-in launch because the application executable was not found. Keep the portable app in a permanent folder before enabling this setting.";
                    return false;
                }

                using var key = Registry.CurrentUser.CreateSubKey(_registrySubKey, writable: true);
                if (key is null)
                {
                    LastError = "Could not open the current user's sign-in settings.";
                    return false;
                }
                key.SetValue(_valueName, command, RegistryValueKind.String);
            }
            else
            {
                // Do not create an absent key or change adjacent applications' values.
                using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: true);
                key?.DeleteValue(_valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception) when (IsStartupError(exception))
        {
            LastError = $"Could not {(enabled ? "enable" : "disable")} sign-in launch. {exception.Message}";
            return false;
        }
    }

    private string GetCommand()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) ||
            _executablePath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("The executable path is invalid.");
        string path = Path.GetFullPath(_executablePath);
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The sign-in command must point to the VpnSafeNotify executable.");

        string command = $"\"{path}\"";
        // The Run key's documented command-line limit includes these quotation marks.
        if (command.Length > 260)
            throw new ArgumentException("The executable path is too long for Windows sign-in launch. Move the app to a shorter folder path.");
        return command;
    }

    private static bool IsStartupError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or ArgumentException
        or NotSupportedException;
}
