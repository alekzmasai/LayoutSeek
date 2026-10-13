using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

// Local verification of this application's window only. No keyboard input or Search access.
internal static class UiLiveChecks
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    static readonly List<object> checks = new List<object>();
    static void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
    static AutomationElement Find(AutomationElement window, string name)
    {
        return window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
    }
    static bool Until(Func<bool> check)
    {
        var watch = Stopwatch.StartNew();
        do { if (check()) return true; Thread.Sleep(100); } while (watch.ElapsedMilliseconds < 8000);
        return false;
    }
    static void Invoke(AutomationElement window, string name)
    {
        var control = Find(window, name);
        if (control == null) throw new InvalidOperationException("Missing own-app control: " + name);
        ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    [STAThread]
    static int Main(string[] args)
    {
        string report = Path.GetFullPath(args[2]);
        string screenshot = Path.ChangeExtension(report, ".png");
        Process app = null; string error = null; int secondaryPid = 0;
        try
        {
            app = Process.GetProcessById(int.Parse(args[0]));
            string expectedExe = Path.GetFullPath(args[1]);
            Check("Exact target executable", string.Equals(app.MainModule.FileName, expectedExe, StringComparison.OrdinalIgnoreCase));
            Check("Window available", Until(() => { app.Refresh(); return app.MainWindowHandle != IntPtr.Zero; }));
            IntPtr handle = app.MainWindowHandle;
            var window = AutomationElement.FromHandle(handle);
            Check("Fresh launch reaches OFF", Until(() => { var b = Find(window, "Включить"); return b != null && b.Current.IsEnabled; }));
            Invoke(window, "Включить");
            Check("Enable updates real UI", Until(() => Find(window, "Выключить") != null));
            Invoke(window, "Выключить");
            Check("Disable updates real UI", Until(() => Find(window, "Включить") != null));
            Invoke(window, "В трей");
            Check("Explicit tray hides original window", Until(() => !IsWindowVisible(handle)));
            using (var secondary = Process.Start(new ProcessStartInfo(expectedExe) { UseShellExecute = false, CreateNoWindow = true }))
            {
                secondaryPid = secondary.Id;
                Check("Second launch exits", secondary.WaitForExit(8000));
            }
            Check("Second launch restores original handle", Until(() => IsWindowVisible(handle)));
            Check("Only original process remains", Process.GetProcessesByName("SearchLayout").Where(p => string.Equals(p.MainModule.FileName, expectedExe, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).SequenceEqual(new[] { app.Id }));
            Check("Restored window preserves OFF", Find(window, "Включить") != null);
            Invoke(window, "Включить");
            Check("Previous user's enabled mode restored", Until(() => Find(window, "Выключить") != null));
            Thread.Sleep(300);
            Rect rect; Check("Own window bounds", GetWindowRect(handle, out rect));
            using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc(); bool captured;
                try { captured = PrintWindow(handle, hdc, 0); } finally { graphics.ReleaseHdc(hdc); }
                Check("Own window screenshot", captured);
                bitmap.Save(screenshot, ImageFormat.Png);
            }
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        var result = new { timestamp = DateTimeOffset.Now.ToString("o"), layer = "Real portable application UI, no native Search input", pid = app == null ? 0 : app.Id, startTime = app == null ? null : app.StartTime.ToString("o"), secondaryPid, checks, error, screenshot, nativeSearchRetested = false, physicalDpiChangeTested = false };
        string json = new JavaScriptSerializer().Serialize(result);
        File.WriteAllText(report, json, new System.Text.UTF8Encoding(false)); Console.WriteLine(json);
        return error == null ? 0 : 1;
    }
}
