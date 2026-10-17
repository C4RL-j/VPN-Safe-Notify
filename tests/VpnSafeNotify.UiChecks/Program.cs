using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using VpnSafeNotify.Models;
using VpnSafeNotify.ViewModels;

namespace VpnSafeNotify.UiChecks;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        Application? application = null;
        VpnSafeNotify.MainWindow? window = null;
        VpnSafeNotify.DiagnosticsWindow? diagnostics = null;
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            // The production App constructor queues OnStartup even without Run, so use
            // an independent Application with the exact copied source theme instead.
            // This never enters production startup or signals an existing application.
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppTheme.xaml"));
            var dictionary = new XElement(presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                theme.Root!.Element(presentation + "Application.Resources")!.Elements());
            application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            var model = new MainViewModel { NotificationsEnabled = true, NetworkChangeDetail = "Fixture network changes settle for two seconds." };
            var previewEffects = new List<EdgeEffect>();
            var unverifiedPolicyChanges = new List<bool>();
            int settingsOpens = 0;
            window = new VpnSafeNotify.MainWindow(model, () => Task.CompletedTask, _ => Task.CompletedTask, () =>
            {
                diagnostics = new VpnSafeNotify.DiagnosticsWindow(model) { Owner = window, ShowActivated = false };
                diagnostics.Show();
            }, effect => previewEffects.Add(effect), enabled =>
            {
                unverifiedPolicyChanges.Add(enabled);
                model.WarnWhenVpnUnverified = enabled;
                return Task.CompletedTask;
            }, () => settingsOpens++) { Title = "UI verification — VPN Safe Notify", ShowActivated = false };
            model.SetRunning(true);
            window.Show();
            Pump(window);
            Equal("Active", ById<TextBlock>(window, "MonitoringStatus").Text, "Monitoring binding");
            var unverifiedPolicyToggle = ById<CheckBox>(window, "WarnWhenVpnUnverifiedToggle");
            Equal("Also warn when VPN status is unverified", unverifiedPolicyToggle.Content.ToString(), "Unverified warning setting label");
            Expect(model.WarnWhenVpnUnverified && unverifiedPolicyToggle.IsChecked == true, "Unverified warnings must default to enabled in the model and rendered checkbox.");
            double defaultWindowWidth = window.Width;
            foreach (double width in new[] { defaultWindowWidth, window.MinWidth })
            {
                window.Width = width;
                Pump(window);
                CheckLabelFits(unverifiedPolicyToggle, width);
                CheckVisibleInClient(window, "SettingsOpen", width);
                CheckVisibleInClient(window, "DiagnosticsOpen", width);
            }
            window.Width = defaultWindowWidth;
            Pump(window);

            foreach (InternetState internet in Enum.GetValues<InternetState>())
            foreach (VpnState vpn in Enum.GetValues<VpnState>())
            {
                model.SetSnapshot(Fixture(internet, vpn));
                Pump(window);
                Equal(internet.ToString(), ById<TextBlock>(window, "InternetStatus").Text, "Internet state binding");
                Equal(vpn.ToString(), ById<TextBlock>(window, "VpnStatus").Text, "VPN state binding");
                Color expectedVpnColor = internet == InternetState.Offline ? Color.FromRgb(170, 181, 204) : vpn switch
                {
                    VpnState.Connected => Color.FromRgb(117, 223, 181),
                    VpnState.Unverified => Color.FromRgb(255, 198, 117),
                    _ => internet == InternetState.Online ? Color.FromRgb(255, 141, 145) : Color.FromRgb(170, 181, 204)
                };
                Equal(expectedVpnColor, ((SolidColorBrush)ById<TextBlock>(window, "VpnStatus").Foreground).Color,
                    "VPN status color must represent detected evidence and reserve red for Online + Disconnected");
                string expected = internet switch
                {
                    InternetState.Offline => "No usable network connection",
                    InternetState.Unknown => "Internet access is unconfirmed",
                    _ => vpn switch
                    {
                        VpnState.Connected => "Active VPN tunnel detected",
                        VpnState.Disconnected => "Internet available. VPN disconnected.",
                        _ => "Internet available. VPN is unverified."
                    }
                };
                Equal(expected, BoundText(window, "Summary").Text, "Summary binding");
            }
            model.SetSnapshot(Fixture(InternetState.Online, VpnState.Connected));
            Pump(window);
            Expect(BoundText(window, "SummaryDetail").Text.Contains("Full internet traffic protection remains unverified", StringComparison.Ordinal),
                "A detected tunnel must retain the protection qualification in rendered text.");
            model.SetSnapshot(Fixture(InternetState.Online, VpnState.Unverified));
            Pump(window);
            Equal(Color.FromRgb(255, 198, 117), ((SolidColorBrush)ById<TextBlock>(window, "VpnStatus").Foreground).Color,
                "An unverified VPN must use amber status");
            string unverifiedExplanation = BoundText(window, "VpnDetail").Text;
            string unverifiedDiagnostics = model.DiagnosticsText;
            var togglePeer = (IToggleProvider)new CheckBoxAutomationPeer(unverifiedPolicyToggle).GetPattern(PatternInterface.Toggle);
            foreach (bool enabled in new[] { false, true })
            {
                togglePeer.Toggle();
                // WPF's UIA Toggle pattern changes IsChecked without raising Click.
                // Route the matching click so the real window callback sees that value,
                // without activating the fixture or moving focus from the user's app.
                unverifiedPolicyToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(window);
                Equal(enabled ? 2 : 1, unverifiedPolicyChanges.Count, "Each setting click must invoke the production callback exactly once");
                Equal(enabled, unverifiedPolicyChanges.Last(), "The checkbox callback must receive the actual toggled value");
                Equal(enabled, model.WarnWhenVpnUnverified, "The policy callback must update the model");
                Equal(enabled, unverifiedPolicyToggle.IsChecked == true, "The policy binding must reflect the updated model");
                Equal("Online", ById<TextBlock>(window, "InternetStatus").Text, "Changing warning policy must preserve internet status");
                Equal("Unverified", ById<TextBlock>(window, "VpnStatus").Text, "Changing warning policy must preserve the detected VPN status");
                Equal(Color.FromRgb(255, 198, 117), ((SolidColorBrush)ById<TextBlock>(window, "VpnStatus").Foreground).Color,
                    "An unverified VPN must remain amber with either warning policy");
                Equal("Internet available. VPN is unverified.", BoundText(window, "Summary").Text, "Warning policy must not relabel Unverified as Disconnected");
                Equal(unverifiedExplanation, BoundText(window, "VpnDetail").Text, "Changing warning policy must preserve the VPN detection explanation");
                Equal(unverifiedDiagnostics, model.DiagnosticsText, "Changing warning policy must preserve diagnostic evidence");
                string policyDetail = BoundText(window, "SummaryDetail").Text;
                Expect(policyDetail.Contains("uncertain", StringComparison.OrdinalIgnoreCase), "The policy summary must retain detection uncertainty.");
                Expect(policyDetail.Contains(enabled ? "six seconds" : "are off", StringComparison.OrdinalIgnoreCase),
                    "The policy summary must describe the active red warning policy.");
            }
            Expect(unverifiedPolicyChanges.SequenceEqual(new[] { false, true }), "The policy checkbox must invoke exactly one callback for each direction.");
            foreach ((string id, string label) in new[] { ("PreviewRed", "Preview Red"), ("PreviewGreen", "Preview Green") })
            {
                var preview = ById<Button>(window, id);
                Equal(label, preview.Content.ToString(), "Preview action label");
                ((IInvokeProvider)new ButtonAutomationPeer(preview).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
            }
            Expect(previewEffects.SequenceEqual(new[] { EdgeEffect.Red, EdgeEffect.Green }), "Preview actions must invoke the explicit red and green effects in order.");
            Equal("Online", ById<TextBlock>(window, "InternetStatus").Text, "Preview must preserve internet status");
            Equal("Unverified", ById<TextBlock>(window, "VpnStatus").Text, "Preview must preserve VPN status");
            ((IInvokeProvider)new ButtonAutomationPeer(ById<Button>(window, "SettingsOpen")).GetPattern(PatternInterface.Invoke)).Invoke();
            Pump(window);
            Equal(1, settingsOpens, "Settings button must invoke the settings callback exactly once");
            var open = ById<Button>(window, "DiagnosticsOpen");
            var peer = new ButtonAutomationPeer(open);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Pump(window);
            Expect(diagnostics is { IsVisible: true }, "Diagnostics button invocation must open the real window.");
            Pump(diagnostics!);
            string reasons = ById<TextBlock>(diagnostics!, "DetectionReasons").Text;
            foreach (string expected in new[] { "fixture internet evidence", "fixture VPN evidence", "fixture active tunnel", "Traffic protection: Unverified", "phone hotspot" })
                Expect(reasons.Contains(expected, StringComparison.OrdinalIgnoreCase), "Rendered detection reasons must include " + expected);
            var adapters = ById<ItemsControl>(diagnostics!, "DiagnosticAdapters");
            Equal(2, adapters.Items.Count, "Adapter row count");
            string rows = string.Join("\n", Descendants(adapters).OfType<TextBlock>().Select(Text));
            foreach (string expected in new[] { "Fixture tunnel", "Fixture uplink", "Usable addresses: 1", "Forwarding routes: 2", "fixture route evidence", "fixture ordinary uplink" })
                Expect(rows.Contains(expected, StringComparison.Ordinal), "Rendered adapter rows must include " + expected);

            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            Directory.CreateDirectory(output);
            Save(window, Path.Combine(output, "phase2-ui.png"));
            Save(diagnostics!, Path.Combine(output, "diagnostics.png"));
            CheckSettingsWindow(output);
            CheckSettingsWithHiddenOwner();
            TrayChecks.Run(Expect);
            EdgeOverlayChecks.Run(output, Expect);
            Expect(bindingErrors.Messages.Length == 0, "No WPF binding errors: " + bindingErrors.Messages);
            model.SetRunning(false);
            Pump(window);
            Equal("Paused", ById<TextBlock>(window, "MonitoringStatus").Text, "Paused status");
            Equal("Not checking", ById<TextBlock>(window, "InternetStatus").Text, "Paused internet status");
            Equal("Not checking", ById<TextBlock>(window, "VpnStatus").Text, "Paused VPN status");
            Equal(0, adapters.Items.Count, "Paused diagnostics must clear adapter evidence");
            var resumeAt = DateTimeOffset.Now.AddMinutes(15);
            model.SetPauseUntil(resumeAt);
            Pump(window);
            Equal("Paused until " + resumeAt.ToLocalTime().ToString("HH:mm"), ById<TextBlock>(window, "MonitoringStatus").Text,
                "Timed pause must be obvious in the main monitoring status");
            Expect(BoundText(window, "SummaryDetail").Text.Contains(resumeAt.ToLocalTime().ToString("HH:mm"), StringComparison.Ordinal),
                "Timed pause summary must show when monitoring will resume.");
            Equal(Color.FromRgb(170, 181, 204), ((SolidColorBrush)ById<TextBlock>(window, "MonitoringStatus").Foreground).Color,
                "Paused monitoring must use a neutral gray status");
            window.Close();
            Pump(window);
            Expect(!window.IsVisible, "Closing the main window must hide it to the tray.");
            Expect(!diagnostics!.IsVisible, "Closing the main window must also hide its owned diagnostics.");
            Console.WriteLine($"PASS WPF render and automation invocation: {_assertions} assertions; no binding errors.");
            Console.WriteLine($"Window-only artifacts: {Path.Combine(output, "phase2-ui.png")}; {Path.Combine(output, "diagnostics.png")}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAIL " + exception);
            return 1;
        }
        finally
        {
            diagnostics?.Close();
            if (window is not null) { window.AllowClose = true; window.Close(); }
            application?.Shutdown();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
        }
    }

    private static MonitoringSnapshot Fixture(InternetState internet, VpnState vpn) => new(
        new ConnectivityResult(internet, "Fixture internet status") { Diagnostics = new[] { "fixture internet evidence" } },
        new VpnDetectionResult(vpn, "Fixture VPN status", new[] { "fixture active tunnel" })
        {
            Diagnostics = new[] { "fixture VPN evidence" },
            Adapters = new[]
            {
                new AdapterDiagnostic("Fixture tunnel", "Synthetic tunnel adapter", "Tunnel", "Up", 1, 2, "fixture route evidence"),
                new AdapterDiagnostic("Fixture uplink", "Synthetic Ethernet adapter", "Ethernet", "Up", 1, 1, "fixture ordinary uplink")
            }
        }, DateTimeOffset.Now);

    private static void CheckSettingsWindow(string output)
    {
        var initial = new AppSettings
        {
            MonitoringEnabled = false,
            PollIntervalSeconds = 300,
            AlertCooldownSeconds = 180,
            AdditionalVpnAdapterKeywords = new[] { "fixture custom tunnel" },
            RedAlertDurationSeconds = -10,
            RepeatIntervalSeconds = 999,
            BorderThickness = -4,
            BorderOpacity = double.NaN
        };
        var applied = new List<AppSettings>();
        var previews = new List<EdgeEffect>();
        string? nextError = null;
        bool throwOnApply = false;
        TaskCompletionSource<string?>? pending = null;
        var settings = new VpnSafeNotify.SettingsWindow(initial, value =>
        {
            applied.Add(value);
            if (throwOnApply) throw new InvalidOperationException("fixture apply exception");
            return pending?.Task ?? Task.FromResult(nextError);
        }, previews.Add) { ShowActivated = false, Title = "Settings UI verification — VPN Safe Notify" };
        try
        {
            settings.Show();
            Pump(settings);
            Equal(0, applied.Count, "Opening settings must not apply anything");
            Equal(5d, ById<Slider>(settings, "SettingsRedDuration").Value, "Settings UI must clamp an invalid red duration");
            Equal(300d, ById<Slider>(settings, "SettingsRepeatInterval").Value, "Settings UI must clamp an invalid repeat interval");
            Equal(24d, ById<Slider>(settings, "SettingsBorderThickness").Value, "Settings UI must clamp invalid border thickness");
            Equal(27d, ById<Slider>(settings, "SettingsBorderOpacity").Value, "Settings UI must recover a non-finite opacity");
            foreach (var (id, minimum, maximum) in new[]
            {
                ("SettingsRedDuration", 5d, 7d), ("SettingsRepeatInterval", 15d, 300d),
                ("SettingsBorderThickness", 24d, 180d), ("SettingsBorderOpacity", 5d, 65d)
            })
            {
                var slider = ById<Slider>(settings, id);
                Equal(minimum, slider.Minimum, id + " minimum");
                Equal(maximum, slider.Maximum, id + " maximum");
                Expect(slider.IsSnapToTickEnabled && slider.TickFrequency == 1, id + " must select bounded whole-number values.");
            }
            ById<Slider>(settings, "SettingsRedDuration").Value = 7;
            ById<Slider>(settings, "SettingsRepeatInterval").Value = 15;
            ById<Slider>(settings, "SettingsBorderThickness").Value = 180;
            ById<Slider>(settings, "SettingsBorderOpacity").Value = 65;
            foreach (string id in new[] { "SettingsAllMonitors", "SettingsWarnUnverified", "SettingsNotifications" })
                ById<CheckBox>(settings, id).IsChecked = false;
            foreach (string id in new[] { "SettingsWarningSound", "SettingsLaunchAtSignIn", "SettingsStartMinimized" })
                ById<CheckBox>(settings, id).IsChecked = true;
            Equal(0, applied.Count, "Editing draft values must not apply settings");
            foreach (string id in new[] { "SettingsPreviewRed", "SettingsPreviewGreen" }) InvokeSettings(settings, id);
            Expect(previews.SequenceEqual(new[] { EdgeEffect.Red, EdgeEffect.Green }), "Settings previews must route explicit effects without applying the draft.");
            Equal(0, applied.Count, "Previews must not apply draft settings");
            InvokeSettings(settings, "SettingsApply");
            Equal(1, applied.Count, "Apply must invoke the callback once");
            var saved = applied.Last();
            Equal(7, saved.RedAlertDurationSeconds, "Applied red duration");
            Equal(15, saved.RepeatIntervalSeconds, "Applied repeat interval");
            Equal(180, saved.BorderThickness, "Applied border thickness");
            Equal(.65, saved.BorderOpacity, "Applied percent must be converted to opacity");
            Expect(!saved.AllMonitors && !saved.WarnWhenVpnUnverified && !saved.NotificationsEnabled,
                "Applied settings must retain disabled monitor, uncertainty and notification choices.");
            Expect(saved.WarningSoundEnabled && saved.LaunchAtSignIn && saved.StartMinimized,
                "Applied settings must retain sound and daily-use choices.");
            Expect(!saved.MonitoringEnabled && saved.PollIntervalSeconds == 300 && saved.AlertCooldownSeconds == 180 &&
                saved.AdditionalVpnAdapterKeywords.SequenceEqual(initial.AdditionalVpnAdapterKeywords),
                "Editing visible settings must preserve monitoring and advanced detection settings.");
            Equal("Settings saved and applied.", ById<TextBlock>(settings, "SettingsMessage").Text, "Settings success must be visible");
            Expect(settings.IsVisible, "Apply must keep settings open.");

            InvokeSettings(settings, "SettingsRestoreDefaults");
            Equal(1, applied.Count, "Restore defaults must remain a draft until Apply");
            Equal(6d, ById<Slider>(settings, "SettingsRedDuration").Value, "Default red duration");
            Equal(60d, ById<Slider>(settings, "SettingsRepeatInterval").Value, "Default repeat interval");
            Equal(96d, ById<Slider>(settings, "SettingsBorderThickness").Value, "Default border thickness");
            Equal(27d, ById<Slider>(settings, "SettingsBorderOpacity").Value, "Default opacity percentage");
            foreach (string id in new[] { "SettingsAllMonitors", "SettingsWarnUnverified", "SettingsNotifications" })
                Expect(ById<CheckBox>(settings, id).IsChecked == true, id + " default must be enabled.");
            foreach (string id in new[] { "SettingsWarningSound", "SettingsLaunchAtSignIn", "SettingsStartMinimized" })
                Expect(ById<CheckBox>(settings, id).IsChecked == false, id + " default must be disabled.");
            InvokeSettings(settings, "SettingsApply");
            saved = applied.Last();
            Expect(!saved.MonitoringEnabled && saved.PollIntervalSeconds == 300 && saved.AlertCooldownSeconds == 180 &&
                saved.AdditionalVpnAdapterKeywords.SequenceEqual(initial.AdditionalVpnAdapterKeywords),
                "Restoring visible defaults must preserve monitoring and advanced detection settings.");
            nextError = "fixture save failure; previous settings remain active";
            InvokeSettings(settings, "SettingsApply");
            Equal(nextError, ById<TextBlock>(settings, "SettingsMessage").Text, "An apply failure must stay visible without closing settings");
            Expect(settings.IsVisible && ById<Button>(settings, "SettingsApply").IsEnabled,
                "Apply failure must leave the window usable.");
            throwOnApply = true;
            InvokeSettings(settings, "SettingsApply");
            Expect(ById<TextBlock>(settings, "SettingsMessage").Text.Contains("fixture apply exception", StringComparison.Ordinal),
                "Unexpected callback failure must be contained in the settings window.");
            throwOnApply = false;
            nextError = null;
            pending = new TaskCompletionSource<string?>();
            int beforePending = applied.Count;
            InvokeSettings(settings, "SettingsApply");
            Expect(!ById<Button>(settings, "SettingsApply").IsEnabled, "Apply must be disabled while a save is pending.");
            ById<Button>(settings, "SettingsApply").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Equal(beforePending + 1, applied.Count, "A queued duplicate click must not start another save");
            InvokeSettings(settings, "SettingsClose");
            Expect(!settings.IsVisible, "Close must close the settings window while an apply is pending.");
            pending.SetResult(null);
            Pump(settings);

            var reopened = new VpnSafeNotify.SettingsWindow(saved, _ => Task.FromResult<string?>(null), _ => { })
            { ShowActivated = false, Title = "Settings round trip — VPN Safe Notify" };
            try
            {
                reopened.Show();
                Pump(reopened);
                Equal((double)saved.RedAlertDurationSeconds, ById<Slider>(reopened, "SettingsRedDuration").Value, "Reopening settings must retain applied duration");
                Equal((double)saved.RepeatIntervalSeconds, ById<Slider>(reopened, "SettingsRepeatInterval").Value, "Reopening settings must retain applied interval");
                Equal(saved.BorderOpacity * 100, ById<Slider>(reopened, "SettingsBorderOpacity").Value, "Reopening settings must retain applied opacity");
                Save(reopened, Path.Combine(output, "settings-ui.png"));
                InvokeSettings(reopened, "SettingsClose");
                Expect(!reopened.IsVisible, "Closing settings must fully close its window.");
            }
            finally { reopened.Close(); }
        }
        finally { settings.Close(); }
    }

    private static void InvokeSettings(Window settings, string id)
    {
        var button = ById<Button>(settings, id);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump(settings);
    }

    private static void CheckSettingsWithHiddenOwner()
    {
        var owner = new VpnSafeNotify.MainWindow(new MainViewModel(), () => Task.CompletedTask,
            _ => Task.CompletedTask, () => { }) { ShowActivated = false, Title = "Hidden owner fixture" };
        VpnSafeNotify.SettingsWindow? settings = null;
        try
        {
            new WindowInteropHelper(owner).EnsureHandle();
            Expect(!owner.IsVisible, "A tray-first owner must remain hidden after its native handle is initialized.");
            settings = new VpnSafeNotify.SettingsWindow(new AppSettings(), _ => Task.FromResult<string?>(null), _ => { })
            { Owner = owner, ShowActivated = false, Title = "Settings from hidden tray owner" };
            settings.Show();
            Pump(settings);
            Expect(settings.IsVisible, "Settings must open when its main owner has never been shown.");
            Expect(!owner.IsVisible, "Opening Settings from the tray must keep the never-shown main window hidden.");
            InvokeSettings(settings, "SettingsClose");
            Expect(!settings.IsVisible && !owner.IsVisible, "Closing Settings must leave a tray-first main window hidden.");
        }
        finally
        {
            settings?.Close();
            owner.AllowClose = true;
            owner.Close();
        }
    }

    private static void CheckVisibleInClient(Window window, string id, double windowWidth)
    {
        var button = ById<Button>(window, id);
        var client = (FrameworkElement)window.Content;
        Rect bounds = button.TransformToAncestor(client).TransformBounds(new Rect(button.RenderSize));
        Expect(button.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= client.ActualWidth + 1 && bounds.Bottom <= client.ActualHeight + 1,
            $"{id} must remain fully visible inside the main client without scrolling at width {windowWidth} DIP.");
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T ById<T>(Window window, string id) where T : DependencyObject => Descendants(window).OfType<T>()
        .Single(element => AutomationProperties.GetAutomationId(element) == id);
    private static TextBlock BoundText(Window window, string path) => Descendants(window).OfType<TextBlock>()
        .Single(element => BindingOperations.GetBinding(element, TextBlock.TextProperty)?.Path.Path == path);
    private static string Text(TextBlock block) => new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;

    private static void CheckLabelFits(CheckBox checkBox, double windowWidth)
    {
        var label = Descendants(checkBox).OfType<TextBlock>()
            .Single(block => Text(block).Trim() == checkBox.Content.ToString());
        var formatted = new FormattedText(Text(label).Trim(), CultureInfo.CurrentUICulture, label.FlowDirection,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
            label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
        if (label.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, label.ActualWidth);
        Expect(label.ActualWidth + 1 >= formatted.Width && label.ActualHeight + 1 >= formatted.Height,
            $"The full unverified setting label must fit at window width {windowWidth} DIP.");
        Rect bounds = label.TransformToAncestor(checkBox).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
        Expect(bounds.Left >= -1 && bounds.Right <= checkBox.ActualWidth + 1,
            $"The unverified setting label must remain inside its checkbox at window width {windowWidth} DIP.");
    }

    private static void Save(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Equal<T>(T expected, T actual, string reason) => Expect(EqualityComparer<T>.Default.Equals(expected, actual), $"{reason}: expected {expected}; got {actual}.");
    private static void Expect(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); _assertions++; }
    private sealed class BindingErrors : TraceListener
    {
        public StringBuilder Messages { get; } = new();
        public override void Write(string? message) => Messages.Append(message);
        public override void WriteLine(string? message) => Messages.AppendLine(message);
    }

}
