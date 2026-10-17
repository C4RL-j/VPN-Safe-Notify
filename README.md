# VPN Safe Notify

A C#/WPF/.NET 8 desktop app that warns when your PC has internet without a detected local VPN tunnel.

[Download the Windows x64 portable ZIP](https://github.com/C4RL-j/VPN-Safe-Notify/releases/download/v0.1.0/VPN-Safe-Notify-0.1.0-win-x64-portable.zip) · [v0.1.0 release](https://github.com/C4RL-j/VPN-Safe-Notify/releases/tag/v0.1.0)

## Run

Extract the ZIP and open **VPN Safe Notify.exe**. Supports Windows 10/11 x64, includes .NET, and needs no installation or administrator access. It has no console window. **The executable is unsigned, not code-signed.**

Closing the window hides it to the tray; double-click its icon to reopen it. The menu includes **Open Settings**, **Pause Monitoring**, **Pause for 15 minutes**, **Resume**, and **Exit**. Exit cancels checks and alerts and shuts down. Only one instance runs per user.

## Status and warnings

| Status | Meaning and automatic effect |
| --- | --- |
| Green / Connected | A local VPN tunnel was detected. A transition from Disconnected or Unverified shows green for one second; starting already Connected does not animate. |
| Amber / Unverified | Detection is uncertain. While internet is Online, red warnings are enabled by default through **Also warn when VPN status is unverified**. Turning it off preserves amber status without red pulses. |
| Red / Online + Disconnected | No local VPN tunnel was detected. A soft red screen-edge glow lasts six seconds and repeats every 60 seconds by default. |
| Gray / Offline or paused | Automatic red warnings are suppressed. Unknown internet also suppresses red. |

Warnings have a transparent center, eased fades, gentle pulsing, and click-through windows that preserve keyboard focus. One controller prevents stacking. Network changes settle for about two seconds. Pause hides effects immediately; Resume evaluates fresh detection. Switching between eligible Unverified and Disconnected states preserves cadence.

**Preview Red** and **Preview Green** test the visuals without changing your network or detection status. Appearance over exclusive fullscreen games or the Windows secure desktop is not guaranteed.

## Settings

Open **Settings** and select **Apply** to save. Most changes apply immediately; **Start minimized to tray** applies next launch. **Restore defaults** stages defaults until Apply.

| Preference | Default / range |
| --- | --- |
| Red duration | 6 seconds / 5–7 seconds |
| Repeat interval | 60 seconds / 15–300 seconds, measured from the previous warning's start |
| Border thickness | 96 DIP / 24–180 DIP, capped on small displays |
| Border opacity | 27% / 5–65% |
| Monitor scope | All monitors; optionally primary only |
| Warn for Unverified | On |
| Warning sound | Off |
| Windows notifications | On; separate from screen-edge effects |
| Launch at sign-in | Off |
| Start minimized | Off |

Settings use per-user atomic saves at `%LOCALAPPDATA%\VpnSafeNotify\settings.json`. Missing or invalid settings recover safely; values are bounded and save failures appear inline.

Sign-in launch uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value **VPN Safe Notify**, with a quoted executable path. Keep a stable folder; after moving, reopen and apply sign-in preferences again. Windows policy can disable or delay launch.

## Detection and privacy

Internet detection uses Windows connectivity information, rather than treating a connected adapter as proof of internet access. An inconclusive result may use a direct HTTPS request to `https://www.gstatic.com/generate_204`: at most once every 120 seconds, with a four-second deadline, a 4 KiB response-header limit, and no response-body reading, retries, redirects, or external-IP queries. DNS/TLS overhead means this is not a total wire-byte quota. Failed or unexpected responses can remain **Unknown**. Checks react to network changes, use a slow fallback, and cancel on Pause/Exit.

VPN detection considers Windows VPN sessions and recognized third-party adapters, usable addresses, operational state, and routes. A process, installed/enabled adapter, or “VPN” name alone proves no connection.

**NetMod** can be detected when it exposes a suitable active local tunnel. Proxy-only, private SOCKS, or ambiguous configurations may remain **Unverified**. Disconnecting inside NetMod can therefore leave amber status while exiting it removes that ambiguity. This app cannot reliably confirm every NetMod mode. A phone hotspot does not establish that the PC uses the phone's VPN.

Green never guarantees encryption or that all applications, DNS, IPv4, or IPv6 traffic use the tunnel. Diagnostics explains detection without saving browsing history or exposing actual addresses, proxy URLs, or credentials. There is no telemetry, traffic blocking, or VPN connection management.

## Build and verify

Use Windows with the .NET 8 SDK:

```powershell
dotnet build VpnSafeNotify.sln -c Release
dotnet run --project tests/VpnSafeNotify.Checks -c Release
dotnet run --project tests/VpnSafeNotify.UiChecks -c Release
.\tools\Publish-Portable.ps1
```

Service checks use fake network sources. UI checks require an idle desktop and briefly move and restore the pointer. The `PortableWinX64` profile produces Release, self-contained `win-x64`, single-file output with **WPF trimming disabled**. Native WPF/runtime libraries extract under `%TEMP%\.net`; keep it writable, or select a private writable cache using `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Runtime license notices accompany the portable package in `Licenses`.

Use `tools/Verify-Portable.ps1 -PortableDirectory <folder> -ZipPath <zip>` to inspect the package and `tests/PortableSmoke.ps1 -ExePath '<folder>\VPN Safe Notify.exe'` to launch the published image in isolated test mode without live probes or changing production settings.

Version 0.1.0 passed **902 service assertions, 277 UI assertions, and 28 published-EXE smoke checks on Windows 10**, with zero build warnings/errors. Windows 11, actual sign-in, additional NetMod modes, and physical mixed-DPI/hotplug changes remain untested.

## Remove

Disable sign-in launch and select Apply, then choose tray **Exit** and delete the portable folder. Optionally delete `%LOCALAPPDATA%\VpnSafeNotify` and only this app's directory in the extraction cache to remove remaining settings and cached runtime files.
