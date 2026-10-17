# VPN Safe Notify — implementation plan

## Phase 6: Windows x64 portable packaging

1. Inspect SDK/project/dependencies and rerun Release service/UI checks before publishing.
2. Embed the app icon and explicit version metadata; retain GUI/asInvoker behavior and disable WPF trimming.
3. Publish self-contained Windows x64 as a single executable, with native extraction configured; resolve sign-in registration from the actual renamed EXE path.
4. Publish to a fresh directory, add the short portable README and original .NET redistribution notices, then create a ZIP.
5. Verify the published PE/resources/manifest/signature and every ZIP file hash. Smoke-test that exact published image with an isolated user profile so the existing app/preferences/Run entry stay untouched.

Phase 6 completed: Release build passed with zero warnings/errors; 20 service groups/902 assertions and 277 UI assertions passed. Self-contained `win-x64` single-file publish succeeded with trimming disabled, embedded icon/version 0.1.0, GUI subsystem, and `asInvoker`. The published renamed EXE was actually launched on Windows 10 and passed 28 smoke checks: bundled runtime/native WPF extraction, previews/focus/styles, real tray menu callbacks, settings and quoted startup registration in isolated HKCU, restart-to-tray, singleton handoff, and clean shutdown during an active preview. Existing user app, preferences, and production startup registration were preserved. The smoke harness uses native tray callbacks/UI Automation, not physical shell-icon mouse clicks.

Portable directory: `output/releases/20261017-100319/VPN-Safe-Notify-0.1.0-win-x64-portable/`; executable: `VPN Safe Notify.exe` (161,873,387 bytes). ZIP: `output/releases/20261017-100319/VPN-Safe-Notify-0.1.0-win-x64-portable.zip` (67,800,510 bytes). All five ZIP files match the portable files by SHA256, including README and original runtime notices. EXE SHA256: `ED0D3FF99437A75535134599CC276017FDC5574306883D2177D88C103E503FBB`; ZIP SHA256: `0E724C408CBF8AE9CE04CCE06FB025DAD3B006C43815CA7C0BBEE4047DB3E2DD`. Actual Authenticode status is NotSigned. Runtime extraction instructions/removal/detection limits are in the portable README. Actual sign-in, Windows 11, physical mixed-DPI/hotplug and exclusive fullscreen/secure desktop remain unvalidated.

## Phase 5: settings and daily use

1. Preserve detection, warning classification, overlay pass-through, single-instance behavior, and data limits.
2. Add bounded duration/repeat/thickness/opacity, all/primary monitor scope, optional sound/notifications, minimized launch, and staged Restore defaults.
3. Add a compact dark Settings window, live application, serialized atomic settings writes, safe invalid-file recovery, and inline errors.
4. Add optional HKCU Run sign-in registration with a correctly quoted apphost path, disabled by default; read at launch and write only on explicit Apply.
5. Add a session-only 15-minute pause with cancellation, obvious deadline status, concise colored tray presentation, and Open Settings/Resume actions.
6. Build and verify persistence, startup registration in an isolated HKCU key, live timings, sound, tray callbacks, hidden-owner settings, native overlays, and clean disposal.

Phase 5 completed: Release solution build passed with zero warnings/errors; 19 service groups/881 assertions and 277 WPF UI assertions passed. Updated executable: `C:\PROJECTs\VPN safe\output\daily-build\VpnSafeNotify.exe`. Timings default to 6 seconds / 60 seconds and normalize to 5–7 / 15–300. Border defaults to 96 DIP / 27%, bounded to 24–180 DIP / 5–65%. All/primary scope applies live. Sound and sign-in launch default off. Settings/Diagnostics remain fixed in the compact window footer. Tests cover settings drafts/defaults/failures, full persistence and legacy/invalid recovery, correct HKCU path quoting/enable/disable, timed pause/canceled callbacks, menu callbacks/tooltips, hidden-owner settings opening, gray/amber/red/green semantics, and native cursor/focus/disposal.

No production user preferences, Run entry, VPN, adapter, route, proxy, or display settings were changed by checks. Actual Windows sign-in was not performed; Windows policy may suppress/delay startup. The 15-minute deadline was verified with an injected scheduler. Actual mixed-DPI/hotplug and Windows 11 remain hardware validation. Restore defaults resets exposed daily-use preferences while preserving monitoring and advanced detection values.

## Phase 4: optional warnings for Unverified

1. Preserve the existing detector, monitor-edge renderer, single alert controller/timer, six-second red pulse, 60-second repeat, and one-second reconnect green.
2. Add the saved `WarnWhenVpnUnverified` preference, default ON for new and legacy settings, with the exact UI label “Also warn when VPN status is unverified”.
3. Apply changes immediately to the current settled state, preserve amber Unverified status and detection reasons, and retain one warning episode through eligible Unverified/Disconnected switches.
4. Verify setting migration/restart persistence, ON/OFF transitions, suppression, cancellation, repeat cadence, and the real checkbox bindings, then build to `output/unverified-build` without disturbing the existing running copy.

Phase 4 completed: isolated Release solution build passed with zero warnings/errors; all 17 service groups/694 assertions and 137 WPF UI assertions passed. The updated executable is `C:\PROJECTs\VPN safe\output\unverified-build\VpnSafeNotify.exe`. Fresh settings-service instances verify persisted false/true and legacy default ON. Immediate ON/OFF, eligible Unverified/Disconnected cadence, suppression, green reconnection, preview/stale-frame ownership, and unchanged status/explanations are covered. UI checks confirm the exact checkbox label, both callbacks, default ON, amber status, and fitting at 620/530 DIP. The working renderer passed its native focus, bounds, and edge/center cursor pass-through checks. The existing running app, user preferences, detectors, and network/display configuration were preserved.

## Phase 3: screen-edge warnings

1. Inspect the existing detection, alert, window/tray lifecycle, SDK, and tests; preserve detection behavior and low-data limits.
2. Replace the alert service with one controller and one reusable timer for eased effects, repeat deadlines, notifications, previews, and cancellation.
3. Add one passive, click-through vector overlay per monitor, with physical-pixel bounds, transparent centers, per-monitor DPI awareness, and display-change refresh.
4. Wire immediate Pause/Exit cancellation and settled detection transitions; add clearly labeled Preview Red / Preview Green actions and amber uncertainty status.
5. Build and verify the state matrix, timing, stale callbacks, preview replacement, real native styles/focus/pass-through, rendering, and synthetic mixed-DPI layouts.

Phase 3 completed: clean isolated Release build; 17 groups/584 service assertions and 106 WPF UI assertions passed. The updated executable is `output/edge-build/VpnSafeNotify.exe`. Red lasts six seconds and repeats every 60 seconds while Online/Disconnected; reconnect green lasts one second. Initial Connected does not animate; Unverified stays amber with an optional desktop notification. Pause immediately hides/cancels, and Resume evaluates a fresh settled observation. Previews use the same controller and do not change the network.

Native geometry, no-activation styles, unchanged focus, real edge/center cursor pass-through, reuse/hide/dispose, and rendered colors/transparency passed on one 1600×900 display at 96 DPI. Mixed 96/144/192 DPI, negative origins, monitor removal/repositioning, and resolution/scaling changes were checked with synthetic layouts. Actual multiple-monitor hotplug/scaling changes remain hardware validation. Exclusive fullscreen and secure desktop coverage are not guaranteed. Detection and privacy boundaries were preserved; phase 3 tests made no external connectivity requests or network/display configuration changes.

## Phase 2: real detection

1. Preserve the existing window/tray, single-instance behavior, alert cooldown, and settings recovery.
2. Share passive adapter and IPv4/IPv6 routing snapshots; combine Windows internet flags with a bounded, rate-limited HTTPS fallback and conservative Unknown classification.
3. Require active tunnel, addressing, and preferred forwarding-route evidence; expose Connected, Disconnected, and Unverified without claiming full traffic protection.
4. Trigger checks from address/availability/route events after a two-second debounce, with a slow fallback, serialized work, and explicit cancellation of obsolete cycles.
5. Add a current-evidence Diagnostics view without address/proxy/history/credential logging.
6. Build and run deterministic state, route, HTTP, event/debounce, cancellation, privacy, and UI checks; document NetMod/proxy/hotspot limits.

Phase 2 completed: clean isolated Release build; 17 groups/452 service assertions and 49 UI assertions passed. A live read reported Online/Connected, with native network-change subscriptions available. The existing running app was preserved; the updated executable is `output/phase2-build/VpnSafeNotify.exe`. NetMod-specific mode validation and Windows 11 testing remain open. Full traffic protection is never asserted.

## Phase 1 history

1. Inspect the workspace, installed SDKs, and available Windows detection APIs.
2. Create a .NET 8 WPF solution with separate connectivity, VPN detection, alert, and settings services.
3. Implement cancellable monitoring, explicit unknown states, evidence-based VPN detection, and throttled desktop warnings.
4. Add the dark status window, Start/Pause controls, tray lifecycle, saved preferences, and single-instance activation.
5. Build in Release, check detection and warning transitions, and exercise the actual window/tray lifecycle.

## Boundaries

- Windows 10/11, standard user, no third-party runtime libraries.
- No traffic blocking, VPN management, telemetry, or administrator elevation. Sign-in registration is optional, per user, and disabled by default.
- Internet status comes from Windows Network List Manager; disabled/blocked Windows connectivity checks can affect it.
- VPN detection uses connected Windows VPN sessions and active recognized VPN adapters. Running a VPN process alone is not proof of a connection. NetMod proxy-only modes may require a later provider-specific integration.
- Detection reports evidence; it cannot guarantee encryption, full-tunnel routing, or leak prevention.

## Later phases

- Validate physical mixed-DPI displays, monitor hotplug, scaling changes, and ordinary/borderless fullscreen games; overlays cannot guarantee exclusive fullscreen or secure desktop coverage.
- Validate NetMod's actual modes and other VPN clients on a test matrix; add specific detectors where their state is observable.
- Installer/signing and Windows 11 validation remain future work; phase 5 adds the settings editor and optional per-user startup.

## Phase 1 verification

- Completed all five implementation steps on Windows 10 build 19045 with SDK 8.0.425.
- Release solution build: zero warnings and zero errors. Service checks: 11 groups, 150 assertions, all passed.
- Actual desktop checks passed for Start/Pause, notification preference persistence, close-to-tray, duplicate activation, all tray actions, and restart with a saved paused preference. Exit removed the tray icon and fully stopped the process.
- Live detection found one active VPN adapter. Windows did not report internet access, so a real internet-without-VPN warning was not exercised; warning transitions were verified with injected detector results.
- At the end of phase 1, Windows 11, NetMod mode-specific validation, and a reachability fallback were deferred. Phase 2 above implements the fallback and stronger tunnel detection.
