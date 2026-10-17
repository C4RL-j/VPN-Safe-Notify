param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output\portable-smoke')
)

# Runs the actual published app. The two-factor internal smoke mode keeps its
# singleton, preferences and startup registration separate from the user's app.
# Monitoring remains paused throughout; these checks do not probe the network.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PortableSmokeNative
{
    public sealed class WindowInfo {
        public long Handle; public string ClassName; public string Title;
        public bool Visible; public long ExtendedStyle;
    }
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static WindowInfo[] Windows(int processId) {
        var windows = new List<WindowInfo>();
        EnumWindows((window, parameter) => {
            uint actual; GetWindowThreadProcessId(window, out actual);
            if (actual != processId) return true;
            var title = new StringBuilder(512); var name = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity); GetClassName(window, name, name.Capacity);
            windows.Add(new WindowInfo { Handle = window.ToInt64(), Title = title.ToString(),
                ClassName = name.ToString(), Visible = IsWindowVisible(window),
                ExtendedStyle = GetWindowLongPtr(window, -20).ToInt64() });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if ([IO.Path]::GetFileName($ExePath) -ne 'VPN Safe Notify.exe') {
    throw 'Smoke the distribution executable named VPN Safe Notify.exe.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$token = [Guid]::NewGuid().ToString('N')
# Keep extraction below MAX_PATH even when release evidence lives deep in a
# workspace. Native runtime loaders can still have path limits on Windows 10.
$smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('vpn-safe-smoke-' + $token)
$extractDirectory = Join-Path $smokeDirectory 'extraction'
$emptyRuntimeDirectory = Join-Path $smokeDirectory 'empty-dotnet-root'
foreach ($directory in @($smokeDirectory, $extractDirectory, $emptyRuntimeDirectory)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
$settingsPath = Join-Path $smokeDirectory 'settings.json'
$startupSubKey = 'Software\VpnSafeNotify\PortableSmoke\' + $token
$startupValueName = 'VPN Safe Notify'
$normalSettingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VpnSafeNotify\settings.json'
$script:RootElement = [System.Windows.Automation.AutomationElement]::RootElement
$script:SettingsTitle = 'Settings ' + [char]0x2014 + ' VPN Safe Notify'
$script:AppProcess = $null
$script:OwnedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:Checks = [Collections.Generic.List[object]]::new()
$script:TrayHandle = [IntPtr]::Zero
$productionProcesses = @(Get-Process -Name VpnSafeNotify,'VPN Safe Notify' -ErrorAction SilentlyContinue |
    Select-Object Id, Path, StartTime)

function Get-NormalSettingsHash {
    if (Test-Path -LiteralPath $normalSettingsPath) { return (Get-FileHash -LiteralPath $normalSettingsPath -Algorithm SHA256).Hash }
    return '<absent>'
}
function Get-NormalStartupValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try {
        if ($null -eq $key) { return '<absent>' }
        $value = $key.GetValue($startupValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) { return '<absent>' }
        return [string]$value
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
function Get-SmokeStartupValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($startupSubKey)
    try {
        if ($null -eq $key) { return $null }
        return $key.GetValue($startupValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}
function Add-Check([string]$Name, [bool]$Passed, $Evidence) {
    $script:Checks.Add([ordered]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence })
    if (!$Passed) { throw "Check failed: $Name" }
    Write-Host "PASS: $Name"
}
function Start-OwnedApp {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ExePath
    $start.Arguments = '--portable-smoke'
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($ExePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['VPN_SAFE_NOTIFY_SMOKE_TOKEN'] = $token
    $start.EnvironmentVariables['VPN_SAFE_NOTIFY_SMOKE_DIRECTORY'] = $smokeDirectory
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractDirectory
    $start.EnvironmentVariables['DOTNET_ROOT'] = $emptyRuntimeDirectory
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = $emptyRuntimeDirectory
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.EnvironmentVariables['COREHOST_TRACE'] = '1'
    $start.EnvironmentVariables['COREHOST_TRACEFILE'] = (Join-Path $OutputDirectory 'corehost-trace.txt')
    $process = [Diagnostics.Process]::Start($start)
    $script:OwnedProcesses.Add($process)
    return $process
}
function Get-AppWindow([string]$Title = 'VPN Safe Notify') {
    if ($null -eq $script:AppProcess) { return $null }
    # WPF places owned Settings windows under a visible owner's automation peer;
    # they are not necessarily direct Desktop children. Resolve the exact visible
    # native HWND in our PID, then obtain its own UIA peer without desktop scans.
    $native = @([PortableSmokeNative]::Windows($script:AppProcess.Id) | Where-Object { $_.Title -eq $Title -and $_.Visible })
    if ($native.Count -eq 0) { return $null }
    if ($native.Count -ne 1) { throw "More than one owned window matches $Title." }
    $element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$native[0].Handle)
    if ($element.Current.ProcessId -ne $script:AppProcess.Id) { throw 'Native HWND ownership changed during UIA lookup.' }
    return $element
}
function Get-Control([string]$AutomationId, [string]$Title = 'VPN Safe Notify') {
    $window = Get-AppWindow $Title
    if ($null -eq $window) { return $null }
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
}
function Invoke-Control([string]$AutomationId, [string]$Title = 'VPN Safe Notify') {
    $control = Get-Control $AutomationId $Title
    if ($null -eq $control -or $control.Current.ProcessId -ne $script:AppProcess.Id) { throw "Own control not found: $AutomationId" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Set-Checkbox([string]$AutomationId, [bool]$Enabled) {
    $control = Get-Control $AutomationId $script:SettingsTitle
    if ($null -eq $control -or $control.Current.ProcessId -ne $script:AppProcess.Id) { throw "Own setting not found: $AutomationId" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $desired = if ($Enabled) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    if ($pattern.Current.ToggleState -ne $desired) { $pattern.Toggle() }
}
function Set-Range([string]$AutomationId, [double]$Value) {
    $control = Get-Control $AutomationId $script:SettingsTitle
    if ($null -eq $control -or $control.Current.ProcessId -ne $script:AppProcess.Id) { throw "Own range setting not found: $AutomationId" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $pattern.SetValue($Value)
}
function Get-TrayMenuItem([string]$Name) {
    $windows = $script:RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppProcess.Id))
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
    foreach ($window in $windows) {
        $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $item -and !$item.Current.IsOffscreen) { return $item }
    }
    return $null
}
function Show-TrayMenu {
    if ($script:TrayHandle -ne [IntPtr]::Zero) {
        [void][PortableSmokeNative]::PostMessage($script:TrayHandle, 0x800, [IntPtr]1, [IntPtr]0x205)
        Wait-Until { $null -ne (Get-TrayMenuItem 'Exit') } 'own tray menu'
        return
    }
    # .NET 8 NotifyIcon's actual native callback is WM_USER + 1024, with
    # WM_RBUTTONUP in lParam. Post only to this owned process's hidden WinForms
    # HWNDs. UIA then invokes its real menu; no global mouse or shell-icon search.
    # Source: https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/NotifyIcon.cs
    $candidates = @([PortableSmokeNative]::Windows($script:AppProcess.Id) | Where-Object { !$_.Visible -and $_.ClassName.StartsWith('WindowsForms10.Window.') })
    foreach ($candidate in $candidates) {
        [void][PortableSmokeNative]::PostMessage([IntPtr]$candidate.Handle, 0x800, [IntPtr]1, [IntPtr]0x205)
        Start-Sleep -Milliseconds 180
        if ($null -ne (Get-TrayMenuItem 'Exit')) { $script:TrayHandle = [IntPtr]$candidate.Handle; return }
    }
    throw 'The owned process did not expose a working NotifyIcon native callback.'
}
function Invoke-TrayAction([string]$Name) {
    Show-TrayMenu
    $item = Get-TrayMenuItem $Name
    if ($null -eq $item -or $item.Current.ProcessId -ne $script:AppProcess.Id) { throw "Own tray action not found: $Name" }
    if (!$item.Current.IsEnabled) { throw "Own tray action is disabled: $Name" }
    $pattern = $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Get-VisibleOverlays {
    return @([PortableSmokeNative]::Windows($script:AppProcess.Id) |
        Where-Object { $_.Title -eq 'VPN Safe Notify edge warning' -and $_.Visible })
}
function Test-Preview([string]$AutomationId, [string]$Name, [int]$DurationLimit) {
    $foreground = [PortableSmokeNative]::GetForegroundWindow()
    Invoke-Control $AutomationId
    Wait-Until { (Get-VisibleOverlays).Count -gt 0 } "$Name published preview appears" 4
    $overlays = Get-VisibleOverlays
    $required = 0x080800A0 # NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT
    foreach ($overlay in $overlays) {
        Add-Check "$Name passive overlay styles" (($overlay.ExtendedStyle -band $required) -eq $required -and ($overlay.ExtendedStyle -band 0x40000) -eq 0) $overlay
    }
    Add-Check "$Name preview preserves foreground" ([PortableSmokeNative]::GetForegroundWindow() -eq $foreground) $foreground.ToInt64()
    Wait-Until { (Get-VisibleOverlays).Count -eq 0 } "$Name published preview finishes" $DurationLimit
    Add-Check "$Name preview hides cleanly" $true $overlays.Count
}
function Exit-OwnedApp {
    Invoke-TrayAction 'Exit'
    Wait-Until { $script:AppProcess.Refresh(); $script:AppProcess.HasExited } 'owned published app exits' 15
    Add-Check 'Tray Exit fully shuts down published app' ($script:AppProcess.ExitCode -eq 0) @{ ProcessId = $script:AppProcess.Id; ExitCode = $script:AppProcess.ExitCode }
    $script:TrayHandle = [IntPtr]::Zero
}

$normalSettingsBefore = Get-NormalSettingsHash
$normalStartupBefore = Get-NormalStartupValue
$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o')
    Executable = $ExePath
    ExecutableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    Machine = [Environment]::OSVersion.VersionString
    TestDirectory = $smokeDirectory
    SettingsPath = $settingsPath
    TestStartupSubKey = $startupSubKey
    Interaction = 'Actual published WinExe; isolated settings and singleton; native NotifyIcon callbacks and own-process UI Automation. No physical tray click, network probes, or desktop captures.'
    ExistingProcesses = $productionProcesses
    ProcessIds = @()
    RuntimeModules = @()
    Checks = $script:Checks
    Success = $false
    Error = $null
    CleanupError = $null
}

try {
    @{ MonitoringEnabled = $false; StartMinimized = $true; NotificationsEnabled = $false;
        WarningSoundEnabled = $false; LaunchAtSignIn = $false; RedAlertDurationSeconds = 6;
        RepeatIntervalSeconds = 60; AllMonitors = $false } |
        ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
    $script:AppProcess = Start-OwnedApp
    Wait-Until { $script:AppProcess.Refresh(); !$script:AppProcess.HasExited -and @([PortableSmokeNative]::Windows($script:AppProcess.Id)).Count -gt 0 } 'published app creates native windows' 30
    Start-Sleep -Milliseconds 700
    Add-Check 'Actual published EXE remains running' (!$script:AppProcess.HasExited -and $script:AppProcess.Path -eq $ExePath) $script:AppProcess.Id
    $mainWindows = @([PortableSmokeNative]::Windows($script:AppProcess.Id) | Where-Object { $_.Title -eq 'VPN Safe Notify' -and $_.Visible })
    Add-Check 'Start minimized keeps main window hidden' ($mainWindows.Count -eq 0) $mainWindows.Count
    Wait-Until {
        $script:AppProcess.Refresh()
        if ($script:AppProcess.HasExited) { throw "Published process exited before runtime validation, exit code $($script:AppProcess.ExitCode). See corehost-trace.txt." }
        @((Get-Process -Id $script:AppProcess.Id).Modules | Where-Object { $_.ModuleName -ieq 'PresentationNative_cor3.dll' }).Count -eq 1
    } 'published native WPF runtime loaded'
    $modules = @((Get-Process -Id $script:AppProcess.Id).Modules | Where-Object { $_.ModuleName -match '^(coreclr|clrjit|PresentationNative_cor3|wpfgfx_cor3|D3DCompiler_47_cor3)\.dll$' } |
        Select-Object ModuleName, FileName)
    $report.RuntimeModules = $modules
    # The Windows .NET 8 single-file host embeds CoreCLR and hostpolicy inside
    # its PE image; a separate coreclr.dll need not appear in Process.Modules.
    # Use the actual host's trace plus extracted WPF native modules as evidence.
    $hostTrace = Get-Content -LiteralPath (Join-Path $OutputDirectory 'corehost-trace.txt') -Raw
    Add-Check 'Actual host executes a self-contained single-file runtime' ($hostTrace.Contains('Detected Single-File app bundle') -and $hostTrace.Contains('Executing as a self-contained app') -and $hostTrace.Contains('Using internal hostpolicy')) 'COREHOST_TRACE identifies a self-contained bundle with embedded hostpolicy; both DOTNET_ROOT variants point to an empty private directory.'
    $extractPrefix = $extractDirectory.TrimEnd('\') + '\'
    $wpfModules = @($modules | Where-Object { $_.ModuleName -match '^(PresentationNative_cor3|wpfgfx_cor3)\.dll$' })
    $outsideCache = @($wpfModules | Where-Object { !$_.FileName.StartsWith($extractPrefix, [StringComparison]::OrdinalIgnoreCase) })
    Add-Check 'Native WPF dependencies load from this bundle extraction cache' ($wpfModules.Count -eq 2 -and $outsideCache.Count -eq 0) $wpfModules
    Add-Check 'No adjacent runtime DLL is needed' (@(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($ExePath)) -Filter '*.dll' -File).Count -eq 0) ([IO.Path]::GetDirectoryName($ExePath))

    Invoke-TrayAction 'Open'
    Wait-Until { $null -ne (Get-AppWindow) -and !(Get-AppWindow).Current.IsOffscreen } 'tray Open shows main window'
    Wait-Until { $null -ne (Get-Control 'MonitoringStatus') -and (Get-Control 'MonitoringStatus').Current.Name -eq 'Paused' } 'paused published status'
    Add-Check 'Tray Open shows an explicitly paused main window' $true (Get-Control 'MonitoringStatus').Current.Name
    Show-TrayMenu
    Add-Check 'Pause for 15 minutes disabled while paused' (!(Get-TrayMenuItem 'Pause for 15 minutes').Current.IsEnabled) 'Monitoring intentionally remains paused; Resume is not invoked.'
    Add-Check 'Resume available while paused' ((Get-TrayMenuItem 'Resume').Current.IsEnabled) $true
    $menuOpen = Get-TrayMenuItem 'Open'
    $menuOpen.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    Test-Preview 'PreviewRed' 'Red' 9
    Test-Preview 'PreviewGreen' 'Green' 4
    $mainWindow = Get-AppWindow
    $mainHandle = [IntPtr]$mainWindow.Current.NativeWindowHandle
    [void][PortableSmokeNative]::PostMessage($mainHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { ![PortableSmokeNative]::IsWindowVisible($mainHandle) } 'close hides main window'
    Add-Check 'Close hides to tray and leaves process alive' (!$script:AppProcess.HasExited) $script:AppProcess.Id

    Invoke-TrayAction 'Open Settings'
    Wait-Until { $null -ne (Get-AppWindow $script:SettingsTitle) } 'tray Open Settings'
    Add-Check 'Tray Open Settings opens published settings window' $true (Get-AppWindow $script:SettingsTitle).Current.Name
    Set-Range 'SettingsRedDuration' 7
    Set-Range 'SettingsRepeatInterval' 90
    Set-Checkbox 'SettingsLaunchAtSignIn' $true
    Invoke-Control 'SettingsApply' $script:SettingsTitle
    Wait-Until {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $settings.RedAlertDurationSeconds -eq 7 -and $settings.RepeatIntervalSeconds -eq 90 -and $settings.LaunchAtSignIn
    } 'published settings persisted'
    $expectedStartup = '"' + $ExePath + '"'
    Wait-Until { (Get-SmokeStartupValue) -eq $expectedStartup } 'isolated startup entry'
    Add-Check 'Published executable startup command is correctly quoted in isolated HKCU key' ((Get-SmokeStartupValue) -eq $expectedStartup) (Get-SmokeStartupValue)
    Wait-Until { (Get-Control 'SettingsApply' $script:SettingsTitle).Current.IsEnabled } 'settings Apply finishes'
    Set-Checkbox 'SettingsLaunchAtSignIn' $false
    Invoke-Control 'SettingsApply' $script:SettingsTitle
    Wait-Until { $null -eq (Get-SmokeStartupValue) } 'startup disable removes isolated value'
    Add-Check 'Startup disable removes only test registration' $true $startupSubKey
    Invoke-Control 'SettingsClose' $script:SettingsTitle
    Invoke-TrayAction 'Open'
    Wait-Until { $null -ne (Get-Control 'PreviewRed') } 'main preview control before shutdown'
    Invoke-Control 'PreviewRed'
    Wait-Until { (Get-VisibleOverlays).Count -gt 0 } 'active preview before shutdown' 4
    $exitingId = $script:AppProcess.Id
    Exit-OwnedApp
    Add-Check 'Exit during active preview removes all owned windows' (@([PortableSmokeNative]::Windows($exitingId)).Count -eq 0) $exitingId

    $script:AppProcess = Start-OwnedApp
    Wait-Until { $script:AppProcess.Refresh(); !$script:AppProcess.HasExited -and @([PortableSmokeNative]::Windows($script:AppProcess.Id)).Count -gt 0 } 'published restart starts' 30
    Start-Sleep -Milliseconds 500
    $restartVisible = @([PortableSmokeNative]::Windows($script:AppProcess.Id) | Where-Object { $_.Title -eq 'VPN Safe Notify' -and $_.Visible })
    Add-Check 'Restart honors persisted start-to-tray' ($restartVisible.Count -eq 0) $restartVisible.Count
    $originalId = $script:AppProcess.Id
    $duplicate = Start-OwnedApp
    Wait-Until { $duplicate.Refresh(); $duplicate.HasExited } 'duplicate process exits'
    Wait-Until { $null -ne (Get-AppWindow) -and !(Get-AppWindow).Current.IsOffscreen } 'duplicate activates own original window'
    Add-Check 'Single-instance handoff retains the owned original process' (!$script:AppProcess.HasExited -and $script:AppProcess.Id -eq $originalId -and $duplicate.ExitCode -eq 0) @{ Original = $originalId; Duplicate = $duplicate.Id; DuplicateExitCode = $duplicate.ExitCode }
    # The tray Settings route was verified above. Exercise the main-window route
    # after the duplicate activation dispatcher action has completed as well.
    Wait-Until { $null -ne (Get-Control 'SettingsOpen') -and (Get-Control 'SettingsOpen').Current.IsEnabled } 'restarted main Settings action'
    Start-Sleep -Milliseconds 200
    Invoke-Control 'SettingsOpen'
    Wait-Until { $null -ne (Get-Control 'SettingsRedDuration' $script:SettingsTitle) } 'restart settings controls'
    $duration = (Get-Control 'SettingsRedDuration' $script:SettingsTitle).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    $repeat = (Get-Control 'SettingsRepeatInterval' $script:SettingsTitle).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    Add-Check 'Published restart restores saved settings' ($duration -eq 7 -and $repeat -eq 90) @{ RedDurationSeconds = $duration; RepeatSeconds = $repeat }
    Invoke-Control 'SettingsClose' $script:SettingsTitle
    Wait-Until { $null -eq (Get-AppWindow $script:SettingsTitle) } 'main-route Settings window closes'
    Invoke-TrayAction 'Open Settings'
    Wait-Until { $null -ne (Get-AppWindow $script:SettingsTitle) -and $null -ne (Get-Control 'SettingsRedDuration' $script:SettingsTitle) } 'restarted tray Settings root and controls'
    Add-Check 'Tray Settings works again after restart and singleton activation' $true (Get-AppWindow $script:SettingsTitle).Current.NativeWindowHandle
    Invoke-Control 'SettingsClose' $script:SettingsTitle
    Exit-OwnedApp
    Add-Check 'Production preferences remain unchanged' ((Get-NormalSettingsHash) -eq $normalSettingsBefore) $normalSettingsBefore
    Add-Check 'Production sign-in registration remains unchanged' ((Get-NormalStartupValue) -eq $normalStartupBefore) (Get-NormalStartupValue)
    foreach ($existing in $productionProcesses) {
        $current = Get-Process -Id $existing.Id -ErrorAction SilentlyContinue
        Add-Check 'Existing user app process was preserved' ($null -ne $current -and $current.Path -eq $existing.Path -and $current.StartTime -eq $existing.StartTime) $existing
    }
    $report.Success = $true
} catch {
    $report.Error = $_.Exception.Message
    if ($null -ne $script:AppProcess -and !$script:AppProcess.HasExited) {
        $report.FaultWindows = @([PortableSmokeNative]::Windows($script:AppProcess.Id))
        try {
            $ownRoots = $script:RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppProcess.Id))
            $report.FaultAutomation = @($ownRoots | ForEach-Object {
                $root = $_
                [ordered]@{ Name = $root.Current.Name; Handle = $root.Current.NativeWindowHandle;
                    Controls = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                        Select-Object -First 100 | ForEach-Object { [ordered]@{ Name = $_.Current.Name; Id = $_.Current.AutomationId; Offscreen = $_.Current.IsOffscreen } }) }
            })
        } catch { $report.FaultAutomationError = $_.Exception.Message }
    }
    $script:Checks.Add([ordered]@{ Name = 'Published smoke run'; Passed = $false; Evidence = $_.ToString() })
    Write-Host "FAIL: $($report.Error)"
} finally {
    # A failed check is never converted to a pass by forcibly terminating the app.
    # Try its ordinary Exit action, and report any owned process left alive.
    try {
        if ($null -ne $script:AppProcess) {
            $script:AppProcess.Refresh()
            if (!$script:AppProcess.HasExited) { Invoke-TrayAction 'Exit'; [void]$script:AppProcess.WaitForExit(5000) }
        }
    } catch { $report.CleanupError = $_.Exception.Message }
    $alive = @()
    foreach ($owned in $script:OwnedProcesses) {
        $owned.Refresh()
        if (!$owned.HasExited) { $alive += $owned.Id }
    }
    $report.ProcessIds = @($script:OwnedProcesses | ForEach-Object { $_.Id })
    $report.SurvivingOwnedProcessIds = $alive
    if ($alive.Count -eq 0) {
        # Exact GUID-isolated test subtree, never the production Run key.
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($startupSubKey, $false)
    }
    $report.FinalSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $report.FinishedAt = [DateTimeOffset]::Now.ToString('o')
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'portable-smoke.json') -Encoding utf8
}
if (!$report.Success) { exit 1 }
