param(
    [int]$ExistingProcessId = 0,
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\VpnSafeNotify\bin\Release\net8.0-windows\VpnSafeNotify.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output'),
    [switch]$LifecycleOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Accessibility
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiSmokeNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct TitleBarInfoEx {
        public uint Size; public Rect TitleBar;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] States;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public Rect[] Rectangles;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput {
        public int X; public int Y; public uint Data; public uint Flags; public uint Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern IntPtr GetTitleBarInfo(IntPtr window, uint message, IntPtr parameter, ref TitleBarInfoEx info);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    public static IntPtr FindAppWindow() { return FindWindow(null, "VPN Safe Notify"); }
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int length);
    public static IntPtr FindToolbar(IntPtr parent) {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (window, data) => {
            var text = new System.Text.StringBuilder(256);
            GetClassName(window, text, text.Capacity);
            if (text.ToString() != "ToolbarWindow32") return true;
            result = window; return false;
        }, IntPtr.Zero);
        return result;
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    public static void Click(int x, int y, bool right) {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        var inputs = new Input[3];
        inputs[0].Mouse.X = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        inputs[0].Mouse.Y = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        inputs[0].Mouse.Flags = 0xC001; // MOVE | ABSOLUTE | VIRTUALDESK
        inputs[1].Mouse.Flags = right ? 0x0008u : 0x0002u;
        inputs[2].Mouse.Flags = right ? 0x0010u : 0x0004u;
        if (SendInput(3, inputs, Marshal.SizeOf(typeof(Input))) != 3)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VpnSafeNotify\settings.json'
$script:RootElement = [System.Windows.Automation.AutomationElement]::RootElement
$script:AppProcessId = $ExistingProcessId
$script:Checks = [Collections.Generic.List[object]]::new()
$script:Clicks = [Collections.Generic.List[object]]::new()
$script:RaisedWindows = [Collections.Generic.List[object]]::new()
$cursor = [UiSmokeNative+Point]::new()
[void][UiSmokeNative]::GetCursorPos([ref]$cursor)
$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o')
    Executable = $ExePath
    InitialProcessId = $ExistingProcessId
    Mode = $(if ($LifecycleOnly) { 'Lifecycle only' } else { 'Full' })
    Interaction = 'UI Automation locates app controls; cursor hit-testing and an atomic SendInput move/down/up perform real clicks.'
    Screenshot = (Join-Path $OutputDirectory 'app.png')
    InitialStatus = $null
    Checks = $script:Checks
    Clicks = $script:Clicks
    Success = $false
    Error = $null
    FinalSettings = $null
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Add-Check([string]$Name, [bool]$Passed, $Evidence) {
    $script:Checks.Add([ordered]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence })
    if (!$Passed) { throw "Check failed: $Name" }
    Write-Host "PASS: $Name"
}

function Get-AppWindow {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppProcessId),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'VPN Safe Notify'),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))
    return $script:RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Get-Control([string]$AutomationId) {
    $window = Get-AppWindow
    if ($null -eq $window) { return $null }
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
}

function Get-Status([string]$AutomationId) {
    $control = Get-Control $AutomationId
    if ($null -eq $control) { return '' }
    return $control.Current.Name
}

function Click-Element($Element, [switch]$Right, [switch]$Checkbox) {
    if ($null -eq $Element) { throw 'The requested control was not found.' }
    $rect = $Element.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or $rect.IsEmpty -or $rect.X -lt 0 -or $rect.Y -lt 0) {
        throw "Control is not visibly clickable: $($Element.Current.Name), $rect"
    }
    $clickX = if ($Checkbox) { [int]($rect.Left + 8) } else { [int]($rect.Left + $rect.Width / 2) }
    $clickY = [int]($rect.Top + $rect.Height / 2)
    Click-Point $clickX $clickY $Element.Current.ProcessId $Element.Current.Name -Right:$Right
}

function Click-Point([int]$ClickX, [int]$ClickY, [int]$ExpectedProcessId, [string]$Name, [switch]$Right) {
    $actualPoint = [UiSmokeNative+Point]::new()
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        [void][UiSmokeNative]::SetCursorPos($clickX, $clickY)
        Start-Sleep -Milliseconds 30
        [void][UiSmokeNative]::GetCursorPos([ref]$actualPoint)
        $hit = [System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($actualPoint.X, $actualPoint.Y))
        if ($actualPoint.X -eq $clickX -and $actualPoint.Y -eq $clickY -and $hit.Current.ProcessId -eq $ExpectedProcessId) { break }
    }
    $script:Clicks.Add([ordered]@{ Name = $Name; X = $clickX; Y = $clickY; ActualX = $actualPoint.X; ActualY = $actualPoint.Y; Right = [bool]$Right; ExpectedProcessId = $ExpectedProcessId; HitProcessId = $hit.Current.ProcessId })
    if ($actualPoint.X -ne $clickX -or $actualPoint.Y -ne $clickY -or $hit.Current.ProcessId -ne $ExpectedProcessId) {
        throw "Cursor target is obscured; refusing to click another process (expected $ExpectedProcessId, found $($hit.Current.ProcessId))."
    }
    [UiSmokeNative]::Click($clickX, $clickY, [bool]$Right)
    Start-Sleep -Milliseconds 180
}

function Close-AppWindow {
    $close = Get-Control 'Close'
    if ($null -ne $close) { Click-Element $close; return }
    # Native caption controls are not consistently exposed through WPF's UIA
    # subtree. Read the native close rectangle and still click with the mouse.
    $window = Get-AppWindow
    $info = [UiSmokeNative+TitleBarInfoEx]::new()
    $info.States = [uint32[]]::new(6)
    $info.Rectangles = [UiSmokeNative+Rect[]]::new(6)
    $info.Size = [Runtime.InteropServices.Marshal]::SizeOf($info)
    [void][UiSmokeNative]::GetTitleBarInfo([IntPtr]$window.Current.NativeWindowHandle, 0x033F, [IntPtr]::Zero, [ref]$info)
    $rect = $info.Rectangles[5]
    if ($rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { throw 'Native close button bounds are unavailable.' }
    Click-Point ([int](($rect.Left + $rect.Right) / 2)) ([int](($rect.Top + $rect.Bottom) / 2)) $script:AppProcessId 'Close'
}

function Get-Settings {
    if (Test-Path -LiteralPath $settingsPath) {
        return (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json)
    }
    return $null
}

function Wait-Settings([string]$Name, [bool]$Expected) {
    Wait-Until { $settings = Get-Settings; $null -ne $settings -and $settings.$Name -eq $Expected } "settings $Name = $Expected"
}

function Find-TrayIcon {
    $top = $script:RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $top) {
        if ($element.Current.ClassName -notmatch '^(Shell_TrayWnd|NotifyIconOverflowWindow|TopLevelWindowForOverflowXamlIsland)$') { continue }
        $buttons = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($button in $buttons) {
            if ($button.Current.Name.StartsWith('VPN Safe Notify') -and !$button.Current.IsOffscreen) { return $button }
        }
    }
    # Some customized Windows taskbars expose their classic toolbar as an empty
    # UIA Pane. Resolve only this app's icon through the toolbar's MSAA names.
    $overflow = [UiSmokeNative]::FindWindow('NotifyIconOverflowWindow', $null)
    if ($overflow -ne [IntPtr]::Zero -and [UiSmokeNative]::IsWindowVisible($overflow)) {
        $toolbar = [UiSmokeNative]::FindToolbar($overflow)
        if ($toolbar -ne [IntPtr]::Zero) {
            $guid = [Guid]'618736e0-3c3d-11cf-810c-00aa00389b71'
            $accessible = $null
            if ([UiSmokeNative]::AccessibleObjectFromWindow($toolbar, [uint32]4294967292, [ref]$guid, [ref]$accessible) -eq 0) {
                for ($child = 1; $child -le $accessible.accChildCount; $child++) {
                    try {
                        $name = $accessible.accName($child)
                        if ($null -eq $name -or !$name.StartsWith('VPN Safe Notify')) { continue }
                        $left = 0; $top = 0; $width = 0; $height = 0
                        $accessible.accLocation([ref]$left, [ref]$top, [ref]$width, [ref]$height, $child)
                        $element = [System.Windows.Automation.AutomationElement]::FromHandle($toolbar)
                        return [pscustomobject]@{ Current = [pscustomobject]@{
                            Name = $name; ProcessId = $element.Current.ProcessId; IsOffscreen = $false
                            BoundingRectangle = [System.Windows.Rect]::new($left, $top, $width, $height)
                        } }
                    } catch { continue }
                }
            }
        }
    }
    return $null
}

function Get-TrayIcon {
    $icon = Find-TrayIcon
    if ($null -ne $icon) { return $icon }
    $taskbar = $script:RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd'))
    if ($null -eq $taskbar) { throw 'The Windows taskbar is not accessible.' }
    $chevron = $taskbar.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1502'))
    Click-Element $chevron
    $overflow = [UiSmokeNative]::FindWindow('NotifyIconOverflowWindow', $null)
    if ($overflow -ne [IntPtr]::Zero -and ![UiSmokeNative]::IsWindowVisible($overflow)) {
        [void][UiSmokeNative]::SendMessage([IntPtr]$chevron.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Wait-Until { $null -ne (Find-TrayIcon) } 'VPN Safe Notify icon in notification overflow'
    return Find-TrayIcon
}

function Get-TrayMenuItem([string]$Name) {
    $windows = $script:RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppProcessId))
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
    foreach ($window in $windows) {
        $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $item -and !$item.Current.IsOffscreen) { return $item }
    }
    return $null
}

function Click-TrayAction([string]$Name) {
    Click-Element (Get-TrayIcon) -Right
    Wait-Until { $null -ne (Get-TrayMenuItem $Name) } "tray menu action $Name"
    Click-Element (Get-TrayMenuItem $Name)
}

function Wait-Monitoring([string]$Expected) {
    Wait-Until { (Get-Status 'MonitoringStatus') -eq $Expected } "monitoring status $Expected"
}

function Save-AppScreenshot {
    $window = Get-AppWindow
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][UiSmokeNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 250
    $rect = [UiSmokeNative+Rect]::new()
    if ([UiSmokeNative]::DwmGetWindowAttribute($handle, 9, [ref]$rect, 16) -ne 0) { throw 'App frame bounds could not be read for a tightly cropped screenshot.' }
    if ($window.Current.IsOffscreen -or $rect.Left -lt 0 -or $rect.Top -lt 0) { throw 'App is not visible for its cropped screenshot.' }
    foreach ($point in @(@(($rect.Left + 12), ($rect.Top + 12)), @(($rect.Right - 12), ($rect.Bottom - 12)), @([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2)))) {
        $hit = [System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($point[0], $point[1]))
        if ($hit.Current.ProcessId -ne $script:AppProcessId) { throw 'The app screenshot would contain an obscuring window.' }
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $bitmap.Save($report.Screenshot, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose() }
    } finally { $bitmap.Dispose() }
}

function Raise-AppForSmoke {
    $window = Get-AppWindow
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][UiSmokeNative]::ShowWindow($handle, 9)
    # Temporarily raise only this app above unrelated video overlays. Restore
    # ordinary z-order in finally; no other window or desktop setting changes.
    [void][UiSmokeNative]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0003)
    [void][UiSmokeNative]::SetForegroundWindow($handle)
    $script:RaisedWindows.Add(@{ ProcessId = $script:AppProcessId; Handle = $handle })
    Start-Sleep -Milliseconds 250
}

try {
    if ($script:AppProcessId -eq 0) {
        $existing = @(Get-Process -Name VpnSafeNotify -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $ExePath })
        if ($existing.Count -gt 1) { throw 'More than one existing app process was found.' }
        if ($existing.Count -eq 1) { $script:AppProcessId = $existing[0].Id }
        else { $script:AppProcessId = (Start-Process -FilePath $ExePath -PassThru).Id }
        $ExistingProcessId = $script:AppProcessId
        $report.InitialProcessId = $ExistingProcessId
    }
    $process = Get-Process -Id $script:AppProcessId
    if ($process.Path -ne $ExePath) { throw 'The existing process does not match the requested app executable.' }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        $activator = Start-Process -FilePath $ExePath -PassThru
        Wait-Until { $activator.Refresh(); $activator.HasExited } 'initial duplicate activation exits'
    }
    Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero -or [UiSmokeNative]::FindAppWindow() -ne [IntPtr]::Zero } 'initial app native window'
    $originalHandle = $process.MainWindowHandle
    if ($originalHandle -eq [IntPtr]::Zero) { $originalHandle = [UiSmokeNative]::FindAppWindow() }
    if ([System.Windows.Automation.AutomationElement]::FromHandle($originalHandle).Current.ProcessId -ne $script:AppProcessId) { throw 'The app window belongs to an unexpected process.' }
    [void][UiSmokeNative]::ShowWindow($originalHandle, 9)
    [void][UiSmokeNative]::SetForegroundWindow($originalHandle)
    Wait-Until { $null -ne (Get-AppWindow) -and !(Get-AppWindow).Current.IsOffscreen } 'initial visible app window'
    Raise-AppForSmoke
    if ((Get-Status 'MonitoringStatus') -eq 'Paused') { Click-Element (Get-Control 'MonitoringToggle') }
    Wait-Monitoring 'Active'
    $report.InitialStatus = [ordered]@{
        Internet = (Get-Status 'InternetStatus')
        Vpn = (Get-Status 'VpnStatus')
        Monitoring = (Get-Status 'MonitoringStatus')
    }
    foreach ($id in @('InternetStatus', 'VpnStatus', 'MonitoringStatus')) {
        $control = Get-Control $id
        Add-Check "Visible $id" ($null -ne $control -and !$control.Current.IsOffscreen -and ![string]::IsNullOrWhiteSpace($control.Current.Name)) $control.Current.Name
    }
    if (!$LifecycleOnly) {
    try {
        Save-AppScreenshot
        Add-Check 'Cropped app screenshot' (Test-Path -LiteralPath $report.Screenshot) $report.Screenshot
    } catch { $report.ScreenshotError = $_.Exception.Message; Write-Host "Screenshot skipped: $($_.Exception.Message)" }
    Click-Element (Get-Control 'MonitoringToggle')
    Wait-Monitoring 'Paused'
    Wait-Settings 'MonitoringEnabled' $false
    Add-Check 'Window Pause and persisted preference' $true (Get-Settings).MonitoringEnabled
    Start-Sleep -Milliseconds 700
    Add-Check 'Paused UI ignores stale checks' ((Get-Status 'MonitoringStatus') -eq 'Paused' -and (Get-Status 'InternetStatus') -eq 'Not checking' -and (Get-Status 'VpnStatus') -eq 'Not checking') (Get-Status 'MonitoringStatus')

    Click-Element (Get-Control 'MonitoringToggle')
    Wait-Monitoring 'Active'
    Wait-Settings 'MonitoringEnabled' $true
    Add-Check 'Window Start and persisted preference' $true (Get-Settings).MonitoringEnabled

    $notification = Get-Control 'NotificationsToggle'
    $toggle = $notification.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { Click-Element $notification -Checkbox }
    Click-Element (Get-Control 'NotificationsToggle') -Checkbox
    Wait-Settings 'NotificationsEnabled' $false
    Add-Check 'Desktop warnings off persisted' $true (Get-Settings).NotificationsEnabled
    Click-Element (Get-Control 'NotificationsToggle') -Checkbox
    Wait-Settings 'NotificationsEnabled' $true
    Add-Check 'Desktop warnings on persisted' $true (Get-Settings).NotificationsEnabled
    }

    Close-AppWindow
    Wait-Until { ![UiSmokeNative]::IsWindowVisible($originalHandle) } 'close hides window'
    Add-Check 'Close hides window and keeps process alive' ($null -ne (Get-Process -Id $script:AppProcessId -ErrorAction SilentlyContinue)) $script:AppProcessId

    $duplicate = Start-Process -FilePath $ExePath -PassThru
    Wait-Until { $duplicate.Refresh(); $duplicate.HasExited } 'duplicate instance exits'
    Wait-Until { [UiSmokeNative]::IsWindowVisible($originalHandle) } 'duplicate launch reopens existing window'
    $matching = @(Get-Process -Name VpnSafeNotify | Where-Object { $_.Path -eq $ExePath })
    Add-Check 'Duplicate launch exits and activates original process' ($matching.Count -eq 1 -and $matching[0].Id -eq $ExistingProcessId) @{ Existing = $ExistingProcessId; Duplicate = $duplicate.Id; Remaining = @($matching.Id) }

    Close-AppWindow
    Wait-Until { ![UiSmokeNative]::IsWindowVisible($originalHandle) } 'window hides before tray Open'
    Click-TrayAction 'Open'
    Wait-Until { [UiSmokeNative]::IsWindowVisible($originalHandle) } 'tray Open restores window'
    Add-Check 'Real tray Open restores window' $true $script:AppProcessId

    Click-TrayAction 'Pause Monitoring'
    Wait-Monitoring 'Paused'
    Wait-Settings 'MonitoringEnabled' $false
    Add-Check 'Real tray Pause' $true (Get-Status 'MonitoringStatus')
    Click-TrayAction 'Resume'
    Wait-Monitoring 'Active'
    Wait-Settings 'MonitoringEnabled' $true
    Add-Check 'Real tray Resume' $true (Get-Status 'MonitoringStatus')
    Click-TrayAction 'Pause Monitoring'
    Wait-Monitoring 'Paused'
    Wait-Settings 'MonitoringEnabled' $false
    Click-TrayAction 'Exit'
    Wait-Until { $null -eq (Get-Process -Id $script:AppProcessId -ErrorAction SilentlyContinue) } 'tray Exit fully stops first process'
    Add-Check 'Real tray Exit fully stops process' $true $script:AppProcessId

    $restarted = Start-Process -FilePath $ExePath -PassThru
    $script:AppProcessId = $restarted.Id
    Wait-Until { $null -ne (Get-AppWindow) } 'restarted app window'
    Raise-AppForSmoke
    Wait-Monitoring 'Paused'
    Add-Check 'Restart restores saved Paused preference' $true @{ ProcessId = $script:AppProcessId; Status = (Get-Status 'MonitoringStatus'); Saved = (Get-Settings).MonitoringEnabled }
    Click-Element (Get-Control 'MonitoringToggle')
    Wait-Monitoring 'Active'
    Wait-Settings 'MonitoringEnabled' $true
    Wait-Settings 'NotificationsEnabled' $true
    Add-Check 'Restart Start restores active/default preferences' $true (Get-Settings)
    Click-TrayAction 'Exit'
    Wait-Until { $null -eq (Get-Process -Id $script:AppProcessId -ErrorAction SilentlyContinue) } 'final tray Exit fully stops restarted process'
    Add-Check 'Final tray Exit fully stops process' $true $script:AppProcessId
    $report.Success = $true
} catch {
    $report.Error = $_.Exception.Message
    $script:Checks.Add([ordered]@{ Name = 'Smoke run'; Passed = $false; Evidence = $_.ToString() })
    Write-Host "FAIL: $($report.Error)"
} finally {
    foreach ($raised in $script:RaisedWindows) {
        if ($null -ne (Get-Process -Id $raised.ProcessId -ErrorAction SilentlyContinue)) {
            [void][UiSmokeNative]::SetWindowPos($raised.Handle, [IntPtr](-2), 0, 0, 0, 0, 0x0003)
        }
    }
    [void][UiSmokeNative]::SetCursorPos($cursor.X, $cursor.Y)
    $report.FinalSettings = Get-Settings
    $report.FinishedAt = [DateTimeOffset]::Now.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ui-smoke.json') -Encoding utf8
}

if (!$report.Success) { exit 1 }
