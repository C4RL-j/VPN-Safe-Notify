VPN Safe Notify __VERSION__ — Windows x64 portable

Launch
Extract the whole ZIP to a folder you own, then open VPN Safe Notify.exe.
Windows 10/11 x64 is required. The .NET 8 desktop runtime is included;
no separate .NET installation or administrator access is required.
This package is unsigned. It is not code-signed.

Daily use
Closing the main window hides it to the tray. Double-click the tray icon
to open it. The tray menu offers Settings, Pause, Pause for 15 minutes,
Resume, and Exit. Exit fully shuts down the app and its pending alerts.
Use Preview Red / Preview Green to check effects without changing the network.

Settings and sign-in launch
Settings are saved per user at %LOCALAPPDATA%\VpnSafeNotify\settings.json.
Use Settings > Apply to save changes, or Restore defaults then Apply.
Sound and Launch at Windows sign-in are disabled by default.
Sign-in launch uses only the current user's HKCU Run entry "VPN Safe Notify"
and quotes this EXE's full path. Keep the folder in a stable location;
after moving it, open this copy and apply the sign-in preference again.
Windows Startup Apps policy can disable or delay sign-in launch.
Start minimized to tray takes effect at the next launch.

Runtime extraction
This is a self-contained single-file app; WPF and .NET native libraries
must be extracted before use. By default .NET caches them under %TEMP%\.net.
That location must be writable. DOTNET_BUNDLE_EXTRACT_BASE_DIR can select
another private writable cache folder. The EXE still belongs in its portable
folder; extraction does not install the app or register a Windows service.

Detection limits
Green means a local VPN tunnel was detected, not that every application,
IPv4/IPv6 path, DNS request, or internet byte is protected. NetMod proxy-only
or ambiguous modes can remain Unverified (amber). By default Unverified
also triggers red while internet is Online; this is configurable.
A phone hotspot does not prove the PC uses the phone's VPN.
Internet checks are event-driven and bounded; failures can remain Unknown.
The app never blocks traffic, manages the VPN, or sends telemetry.
Edge overlays are not guaranteed over exclusive fullscreen games or the
Windows secure desktop. Additional VPN/NetMod modes and physical mixed-DPI
monitor changes still require hardware validation.

Removal
Turn off Launch at Windows sign-in and select Apply, then use tray Exit.
Delete the portable folder. Optionally delete %LOCALAPPDATA%\VpnSafeNotify
to remove saved settings, and only this app's cached directory under
%TEMP%\.net (or the private extraction cache you selected).
