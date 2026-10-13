using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SearchLayout
{
    internal static class UiTests
    {
        sealed class FakeController : ISearchController
        {
            public bool Enabled { get; set; }
            public int Count { get; set; }
            public volatile bool Disposed;
            public event Action<string> Faulted;
            public void Fail() { var handler = Faulted; if (handler != null) handler("Исправление не подтверждено. Режим выключен."); }
            public void Dispose() { Enabled = false; Disposed = true; }
        }
        sealed class FakeTray : ITraySurface
        {
            public bool Visible { get; set; }
            public bool CanToggle, Enabled, Disposed;
            public string Status;
            public event Action OpenRequested;
            public event Action ToggleRequested;
            public event Action ExitRequested;
            public void Update(bool canToggle, bool enabled, string status) { CanToggle = canToggle; Enabled = enabled; Status = status; }
            public void Open() { if (OpenRequested != null) OpenRequested(); }
            public void Toggle() { if (ToggleRequested != null) ToggleRequested(); }
            public void Exit() { if (ExitRequested != null) ExitRequested(); }
            public void Dispose() { Visible = false; Disposed = true; }
        }
        sealed class FakeStartup : IStartupService
        {
            public StartupResult Current;
            public Task<StartupResult> ReadOverride, SetOverride;
            public int ReadCount, SetCount;
            public FakeStartup(StartupResult initial) { Current = initial; }
            public Task<StartupResult> ReadAsync() { ReadCount++; return ReadOverride ?? Task.FromResult(Current); }
            public Task<StartupResult> SetEnabledAsync(bool enabled)
            {
                SetCount++;
                if (SetOverride != null) return SetOverride;
                if (Current.CanChange) Current = new StartupResult(0, (int)(enabled ? StartupState.Enabled : StartupState.Disabled), Current.Portable);
                return Task.FromResult(Current);
            }
        }
        static readonly List<object> checks = new List<object>();
        static readonly List<object> images = new List<object>();
        static int failures;
        static string output;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        static void Check(string name, bool passed, string detail = null)
        {
            checks.Add(new { name, passed, detail });
            if (!passed) failures++;
        }
        static bool Until(Func<bool> condition, Action poll = null, int milliseconds = 2500)
        {
            var clock = Stopwatch.StartNew();
            do { if (poll != null) poll(); if (condition()) return true; Thread.Sleep(10); } while (clock.ElapsedMilliseconds < milliseconds);
            return false;
        }
        static MainForm NewForm(Func<ISearchController> factory, FakeTray tray, UiPalette palette = null, FakeStartup startup = null, bool startupLaunch = false)
        {
            var form = new MainForm(factory, tray, true, palette, startup ?? new FakeStartup(new StartupResult(0, (int)StartupState.Disabled, true)), startupLaunch);
            // These fixtures emulate scale factors without changing the OS DPI.
            form.AutoScaleMode = AutoScaleMode.None;
            var handle = form.Handle;
            return form;
        }
        static MainForm Ready(FakeController controller, FakeTray tray, UiPalette palette = null)
        {
            var form = NewForm(() => controller, tray, palette);
            form.BeginInitialization();
            if (!Until(() => !form.Loading, form.Pulse)) throw new InvalidOperationException("UI initialization timed out");
            return form;
        }
        static IEnumerable<Control> Children(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Children(child)) yield return nested; }
        }
        static Control Find(MainForm form, string name) { return Children(form).Single(c => c.Name == name); }

        static void StateChecks()
        {
            using (var gate = new ManualResetEventSlim(false))
            using (var entered = new ManualResetEventSlim(false))
            {
                var controller = new FakeController(); var tray = new FakeTray();
                ApartmentState apartment = ApartmentState.Unknown;
                using (var form = NewForm(() => { apartment = Thread.CurrentThread.GetApartmentState(); entered.Set(); gate.Wait(); return controller; }, tray))
                {
                    var clock = Stopwatch.StartNew(); form.BeginInitialization(); clock.Stop();
                    Check("Initialization does not wait for a blocked catalog", clock.ElapsedMilliseconds < 250 && Until(() => entered.IsSet), clock.ElapsedMilliseconds + " ms");
                    Check("Loading disables primary action", form.Loading && !form.PrimaryEnabled && !tray.CanToggle);
                    Check("Catalog factory runs in MTA", apartment == ApartmentState.MTA);
                    gate.Set(); Check("Initialization completes", Until(() => !form.Loading, form.Pulse));
                    Check("Manual launch correction starts OFF", !controller.Enabled && form.StatusText.StartsWith("Выключено") && form.PrimaryText == "Включить");
                    form.PerformPrimaryAction(); Check("Primary enables", controller.Enabled && tray.Enabled && form.StatusText.StartsWith("Включено"));
                    form.PerformPrimaryAction(); Check("Primary disables", !controller.Enabled && !tray.Enabled);
                    form.PerformPrimaryAction(); controller.Fail();
                    Check("Fault fixture does not disable itself", controller.Enabled);
                    form.Pulse(); Check("Pending fault disables controller and offers recovery", !controller.Enabled && form.StatusText.StartsWith("Остановлено") && form.PrimaryText == "Включить снова");
                    form.PerformPrimaryAction(); Check("Recovery enables again", controller.Enabled && form.StatusText.StartsWith("Включено"));
                    int stableWidth = form.Width;
                    for (int i = 0; i < 25; i++) { controller.Fail(); form.Pulse(); form.PerformPrimaryAction(); form.Pulse(); }
                    Check("Repeated states and Pulse preserve window width", form.Width == stableWidth, stableWidth + " -> " + form.Width);
                    form.MoveToTray(); Check("Explicit tray action retains selected mode", form.InTray && tray.Visible && controller.Enabled && !form.Visible);
                    tray.Toggle(); Check("Tray action pauses", !controller.Enabled && form.InTray);
                    tray.Open(); Check("Open restores window state without changing mode", !form.InTray && !tray.Visible && !controller.Enabled);
                    form.ToggleDetails(); Check("Details expand", form.DetailsOpen); form.ToggleDetails(); Check("Details collapse", !form.DetailsOpen);
                    Check("Native keyboard controls have ordered tab stops", Find(form, "PrimaryAction") is Button && Find(form, "StartupOption") is CheckBox && Find(form, "DetailsLink") is LinkLabel && Find(form, "TrayAction") is Button && Find(form, "PrimaryAction").TabIndex == 0 && Find(form, "StartupOption").TabIndex == 1 && Find(form, "DetailsLink").TabIndex == 3 && Find(form, "TrayAction").TabIndex == 4);
                    Check("Primary action exposes accessible state", !string.IsNullOrEmpty(Find(form, "PrimaryAction").AccessibleName) && !string.IsNullOrEmpty(Find(form, "PrimaryAction").AccessibleDescription));
                    tray.Exit(); Check("Tray exit disposes UI and controller", form.Stopped && controller.Disposed && tray.Disposed && !tray.Visible);
                }
            }
            int attempts = 0;
            var retryController = new FakeController(); var retryTray = new FakeTray();
            using (var form = NewForm(() => { if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Synthetic catalog failure"); return retryController; }, retryTray))
            {
                form.BeginInitialization(); Check("Catalog error stays inside window", Until(() => !form.Loading, form.Pulse) && form.PrimaryText == "Повторить" && !form.Stopped);
                form.PerformPrimaryAction(); Check("Retry loads catalog and remains OFF", Until(() => !form.Loading, form.Pulse) && attempts == 2 && !retryController.Enabled && form.StatusText.StartsWith("Выключено"));
                form.Close(); form.Dispose(); Check("Window close exits instead of hiding", form.Stopped && retryController.Disposed && !retryTray.Visible);
            }
            using (var gate = new ManualResetEventSlim(false))
            using (var entered = new ManualResetEventSlim(false))
            {
                var late = new FakeController(); var tray = new FakeTray();
                var form = NewForm(() => { entered.Set(); gate.Wait(); return late; }, tray);
                form.BeginInitialization(); Until(() => entered.IsSet); form.Dispose(); gate.Set();
                Check("Closing while loading disposes the late result", Until(() => late.Disposed) && tray.Disposed);
            }
            var palette = UiPalette.FromSystem();
            Check("Body text contrast", UiPalette.Contrast(palette.Text, palette.Surface) >= 4.5);
            Check("Secondary text contrast", UiPalette.Contrast(palette.Secondary, palette.Surface) >= 4.5);
            Check("Primary button contrast", UiPalette.Contrast(palette.AccentText, palette.Accent) >= 4.5);
        }
        static void StartupChecks()
        {
            var startup = new FakeStartup(new StartupResult(0, (int)StartupState.Disabled));
            var controller = new FakeController(); var tray = new FakeTray();
            using (var form = NewForm(() => controller, tray, null, startup))
            {
                form.BeginInitialization(); Until(() => !form.Loading, form.Pulse);
                form.BeginStartupRead(); form.Pulse();
                Check("Disabled startup is available and unchecked", form.StartupCanChange && !form.StartupChecked && !form.StartupUnknown);
                var pending = new TaskCompletionSource<StartupResult>(); startup.SetOverride = pending.Task;
                var clock = Stopwatch.StartNew(); form.ChangeStartup(); clock.Stop();
                Check("Startup request returns without blocking", clock.ElapsedMilliseconds < 100 && form.StartupBusy);
                Check("Pending enable keeps actual unchecked state", !form.StartupChecked && !form.StartupCanChange && !controller.Enabled);
                startup.Current = new StartupResult(0, (int)StartupState.Enabled); pending.SetResult(startup.Current); form.Pulse();
                Check("Native result controls the checkbox", form.StartupChecked && form.StartupCanChange && !controller.Enabled);
                startup.SetOverride = null; form.ChangeStartup(); form.Pulse();
                Check("Disable request reflects returned disabled state", !form.StartupChecked && form.StartupCanChange);
                int previousRequests = startup.SetCount;
                ((CheckBox)Find(form, "StartupOption")).CheckState = CheckState.Checked; form.Pulse();
                Check("Accessibility state change enables startup exactly once", form.StartupChecked && startup.SetCount == previousRequests + 1);
                ((CheckBox)Find(form, "StartupOption")).CheckState = CheckState.Unchecked; form.Pulse();
                Check("Accessibility state change disables startup exactly once", !form.StartupChecked && startup.SetCount == previousRequests + 2);
                Find(form, "StartupOption").AccessibilityObject.DoDefaultAction(); form.Pulse();
                Check("Accessible default action enables startup once", form.StartupChecked && startup.SetCount == previousRequests + 3);
                Find(form, "StartupOption").AccessibilityObject.DoDefaultAction(); form.Pulse();
                Check("Accessible default action disables startup once", !form.StartupChecked && startup.SetCount == previousRequests + 4);
                foreach (StartupState state in new[] { StartupState.DisabledByUser, StartupState.DisabledByPolicy, StartupState.EnabledByPolicy })
                {
                    startup.Current = new StartupResult(0, (int)state); form.BeginStartupRead(); form.Pulse();
                    int requests = startup.SetCount; form.ChangeStartup();
                    Check("Blocked state " + state + " cannot be overridden", !form.StartupCanChange && startup.SetCount == requests && form.StartupChecked == (state == StartupState.EnabledByPolicy));
                }
                startup.Current = new StartupResult(0, (int)StartupState.Disabled, true); form.BeginStartupRead(); form.Pulse();
                Check("Portable startup is available and unchecked", !form.StartupChecked && !form.StartupUnknown && form.StartupCanChange);
                Find(form, "StartupOption").AccessibilityObject.DoDefaultAction(); form.Pulse();
                Check("Portable startup checkbox enables once without enabling correction", form.StartupChecked && startup.Current.Portable && !controller.Enabled);
                var activationRead = new TaskCompletionSource<StartupResult>();
                startup.ReadOverride = activationRead.Task; form.BeginStartupRead();
                Check("Refreshing known startup state keeps its action available", form.StartupCanChange);
                int activationRequests = startup.SetCount;
                Find(form, "StartupOption").AccessibilityObject.DoDefaultAction(); form.Pulse();
                Check("Activation read does not swallow the disable click", !form.StartupChecked && startup.SetCount == activationRequests + 1);
                activationRead.SetResult(new StartupResult(0, (int)StartupState.Enabled, true)); form.Pulse();
                Check("Superseded read cannot restore a stale check state", !form.StartupChecked);
                startup.ReadOverride = null;
                startup.Current = StartupResult.Failed(); form.BeginStartupRead(); form.Pulse();
                Check("Startup error is unknown rather than falsely OFF", form.StartupUnknown && !form.StartupCanChange && ((LinkLabel)Find(form, "StartupStatus")).Links.Count == 1);
                form.PerformPrimaryAction(); Check("Startup failure does not block correction control", controller.Enabled);
                startup.Current = new StartupResult(0, (int)StartupState.Disabled); form.BeginStartupRead(); form.Pulse();
                Check("Startup read can recover", !form.StartupUnknown && form.StartupCanChange);
                var failed = new TaskCompletionSource<StartupResult>(); failed.SetException(new InvalidOperationException("Synthetic startup failure")); startup.ReadOverride = failed.Task;
                form.BeginStartupRead(); form.Pulse(); Check("Faulted startup task stays inside UI", form.StartupUnknown && !form.Stopped);
                var late = new TaskCompletionSource<StartupResult>(); startup.ReadOverride = late.Task;
                form.BeginStartupRead(); form.Dispose(); late.SetResult(new StartupResult(0, (int)StartupState.Enabled)); form.Pulse();
                Check("Closing during startup request prevents late UI updates", form.Stopped && controller.Disposed && tray.Disposed);
            }
            using (var gate = new ManualResetEventSlim(false))
            using (var entered = new ManualResetEventSlim(false))
            {
                var loginController = new FakeController(); var loginTray = new FakeTray();
                using (var form = NewForm(() => { entered.Set(); gate.Wait(); return loginController; }, loginTray, null, new FakeStartup(new StartupResult(0, (int)StartupState.Enabled)), true))
                {
                    form.Show(); Until(() => entered.IsSet);
                    Check("Login launch starts hidden in tray while preparing", !form.Visible && form.InTray && loginTray.Visible && !loginController.Enabled);
                    gate.Set(); Until(() => !form.Loading, form.Pulse);
                    Check("Login launch enables only after preparation", loginController.Enabled && !form.Visible && form.InTray);
                }
            }
            int attempts = 0; var retry = new FakeController(); var retryTray = new FakeTray();
            using (var form = NewForm(() => { if (++attempts == 1) throw new InvalidOperationException("Synthetic catalog failure"); return retry; }, retryTray, null, new FakeStartup(new StartupResult(0, (int)StartupState.Enabled)), true))
            {
                form.Show(); Until(() => !form.Loading, form.Pulse);
                Check("Login initialization failure remains OFF in tray", !retry.Enabled && form.InTray && form.PrimaryText == "Повторить");
                form.PerformPrimaryAction(); Until(() => !form.Loading, form.Pulse);
                Check("Login retry enables after successful initialization", retry.Enabled && attempts == 2);
            }
            Check("Only exact startup argument selects login mode", Program.IsStartupInvocation(new[] { "--startup" }) && !Program.IsStartupInvocation(new[] { "--startup-other" }) && !Program.IsStartupInvocation(new string[0]));
            Check("Invalid native startup enum is rejected", !new StartupResult(0, 99).Available);
        }
        static void ScaleFixture(MainForm form, float factor)
        {
            if (factor == 1F) return;
            var controls = new[] { (Control)form }.Concat(Children(form)).ToArray();
            var fonts = controls.Select(c => new { c.Font.FontFamily.Name, c.Font.Size, c.Font.Style }).ToArray();
            form.Scale(new SizeF(factor, factor));
            for (int i = 0; i < controls.Length; i++) controls[i].Font = new Font(fonts[i].Name, fonts[i].Size * factor, fonts[i].Style);
        }
        static void Capture(string name, float scale, string state, bool details = false, UiPalette palette = null, StartupResult startupState = null)
        {
            var controller = new FakeController { Count = 3 }; var tray = new FakeTray();
            using (var form = NewForm(() => controller, tray, palette, new FakeStartup(startupState ?? new StartupResult(0, (int)StartupState.Disabled, true))))
            {
                form.BeginInitialization(); if (state != "loading") Until(() => !form.Loading, form.Pulse);
                if (state != "loading") { form.BeginStartupRead(); form.Pulse(); }
                if (state == "on" || state == "fault") form.PerformPrimaryAction();
                if (state == "fault") { controller.Fail(); form.Pulse(); }
                ScaleFixture(form, scale);
                if (details) form.ToggleDetails();
                form.StartPosition = FormStartPosition.Manual; form.ShowInTaskbar = false;
                form.Location = new Point(SystemInformation.VirtualScreen.Right + 64, SystemInformation.VirtualScreen.Top);
                form.Show(); if (state != "loading") form.Pulse(); form.FitToContent(); form.PerformLayout(); form.Update();
                Check(name + ": preview stays outside every screen", Screen.AllScreens.All(s => Rectangle.Intersect(s.Bounds, form.Bounds).IsEmpty));
                Check(name + ": preview does not become foreground", GetForegroundWindow() != form.Handle);
                Check(name + ": fits work area height", form.Height <= Screen.FromControl(form).WorkingArea.Height);
                var button = Find(form, "PrimaryAction"); var link = Find(form, "DetailsLink"); var trayButton = Find(form, "TrayAction");
                Check(name + ": action controls fit their parents", button.Left >= 0 && button.Right <= button.Parent.ClientSize.Width && link.Right <= link.Parent.ClientSize.Width && trayButton.Right <= trayButton.Parent.ClientSize.Width);
                string path = Path.Combine(output, name + ".png");
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); }
                var geometry = Children(form).Where(c => c is Label || c is Button).Select(c => new { name = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name, bounds = c.Bounds.ToString(), font = c.Font.Size, preferred = c.GetPreferredSize(c.Size).ToString() }).ToArray();
                images.Add(new { name, path, scale, state, details, width = form.Width, height = form.Height, scrollMaximum = form.VerticalScroll.Maximum, geometry });
                if (details)
                {
                    var body = Find(form, "DetailsText");
                    bool exceeds = form.Controls[0].Height > form.ClientSize.Height;
                    form.AutoScrollPosition = new Point(0, form.Controls[0].Height);
                    bool reachable = !exceeds || (form.AutoScrollPosition.Y < 0 && body.PointToScreen(new Point(0, body.Height)).Y <= form.PointToScreen(new Point(0, form.ClientSize.Height)).Y + 2);
                    Check(name + ": long details reachable by scrolling", reachable, "content=" + form.Controls[0].Height + "; viewport=" + form.ClientSize.Height + "; scroll=" + form.AutoScrollPosition.Y);
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name + "-bottom.png"), ImageFormat.Png); }
                }
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            output = Path.GetFullPath(args.Length == 0 ? "ui-evidence" : args[0]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                StateChecks(); StartupChecks();
                foreach (float scale in new[] { 1F, 1.5F, 2F })
                {
                    string suffix = ((int)(scale * 100)).ToString();
                    Capture("off-" + suffix, scale, "off"); Capture("on-" + suffix, scale, "on"); Capture("fault-" + suffix, scale, "fault");
                }
                Capture("loading-100", 1F, "loading"); Capture("details-200", 2F, "off", true);
                var contrast = new UiPalette { HighContrast = true, Surface = Color.Black, Panel = Color.Black, Text = Color.White, Secondary = Color.White, Accent = Color.Yellow, AccentText = Color.Black, Border = Color.White };
                Capture("high-contrast-150", 1.5F, "on", false, contrast);
                Capture("startup-enabled-100", 1F, "off", false, null, new StartupResult(0, (int)StartupState.Enabled));
                Capture("startup-disabled-by-user-150", 1.5F, "off", false, null, new StartupResult(0, (int)StartupState.DisabledByUser));
                Capture("startup-error-100", 1F, "off", false, null, StartupResult.Failed());
                foreach (float scale in new[] { 1F, 1.5F, 2F })
                    Capture("startup-portable-enabled-" + ((int)(scale * 100)), scale, "off", false, null, new StartupResult(0, (int)StartupState.Enabled, true));
            }
            catch (Exception e) { Check("Harness completed", false, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
            var report = new { timestamp = DateTimeOffset.Now.ToString("o"), layer = "Offscreen nonactivating WinForms rendering and fake controller/tray; no Search input, no real engine, no OS theme/DPI changes", actualMonitorDpiNotTested = true, nativeTrayAndScreenReaderNotTested = true, failures, checks, images };
            string json = new JavaScriptSerializer { MaxJsonLength = 2000000 }.Serialize(report);
            File.WriteAllText(Path.Combine(output, "ui-tests.json"), json, new System.Text.UTF8Encoding(false));
            Console.WriteLine(json);
            return failures == 0 ? 0 : 1;
        }
    }
}
