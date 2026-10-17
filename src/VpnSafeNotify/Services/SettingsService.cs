using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VpnSafeNotify", "settings.json");
    }

    public string SettingsPath { get; }
    public string? LastError { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastError = null;
            await using var stream = new FileStream(SettingsPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream,
                JsonOptions, cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                LastError = "Settings are empty or invalid. Defaults are in use; the existing file was preserved.";
                return new AppSettings();
            }

            return Normalize(settings);
        }
        catch (FileNotFoundException)
        {
            return new AppSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings();
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            LastError = $"Could not load settings. Defaults are in use; the existing file was preserved. {exception.Message}";
            return new AppSettings();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<bool> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            LastError = null;
            var fullPath = Path.GetFullPath(SettingsPath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A rename in the same directory replaces the complete file atomically.
            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            LastError = $"Could not save settings. {exception.Message}";
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (IsSettingsError(exception)) { }
            }

            _fileLock.Release();
        }
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            RedAlertDurationSeconds = Math.Clamp(settings.RedAlertDurationSeconds, 5, 7),
            RepeatIntervalSeconds = Math.Clamp(settings.RepeatIntervalSeconds, 15, 300),
            BorderThickness = Math.Clamp(settings.BorderThickness, 24, 180),
            BorderOpacity = double.IsFinite(settings.BorderOpacity)
                ? Math.Clamp(settings.BorderOpacity, 0.05, 0.65) : 0.27,
            PollIntervalSeconds = Math.Clamp(settings.PollIntervalSeconds, 30, 600),
            AlertCooldownSeconds = Math.Clamp(settings.AlertCooldownSeconds, 30, 3600),
            AdditionalVpnAdapterKeywords = (settings.AdditionalVpnAdapterKeywords ?? Array.Empty<string>())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => keyword.Trim())
                .Select(keyword => keyword.Length > 64 ? keyword[..64] : keyword)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray()
        };
    }

    private static bool IsSettingsError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or JsonException or NotSupportedException
        or ArgumentException or SecurityException;
}
