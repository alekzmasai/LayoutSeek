using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

// Exercises the application's startup checkbox; restores its original state.
internal static class StartupLiveChecks
{
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    static void Click(AutomationElement checkbox, TogglePattern toggle, bool nativeClick) {
        if (!nativeClick) { toggle.Toggle(); return; }
        IntPtr result;
        if (SendMessageTimeout(new IntPtr(checkbox.Current.NativeWindowHandle), 0x00F5, IntPtr.Zero, IntPtr.Zero, 2, 3000, out result) == IntPtr.Zero) throw new InvalidOperationException("Own checkbox BM_CLICK failed");
    }
    static readonly List<object> checks = new List<object>();
    static void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
    static bool Until(Func<bool> check) { var watch = Stopwatch.StartNew(); do { if (check()) return true; Thread.Sleep(100); } while (watch.ElapsedMilliseconds < 10000); return false; }
    [STAThread] static int Main(string[] args)
    {
        string error = null; TogglePattern toggle = null; ToggleState original = ToggleState.Indeterminate;
        try
        {
            var app = Process.GetProcessById(int.Parse(args[0]));
            bool portable = args.Length > 3 && args[3] == "portable";
            Check("Exact application executable", string.Equals(app.MainModule.FileName, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase));
            var window = AutomationElement.FromHandle(app.MainWindowHandle);
            var checkbox = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartupOption"));
            Check("Startup checkbox available", checkbox != null && Until(() => checkbox.Current.IsEnabled));
            checkbox.SetFocus();
            Thread.Sleep(500); // Activation starts an asynchronous read of the Windows StartupTask state.
            Check("Activated startup checkbox ready", Until(() => checkbox.Current.IsEnabled));
            toggle = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
            original = toggle.Current.ToggleState;
            if (args.Length > 3 && args[3] == "inspect") {
                foreach (AutomationElement item in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    checks.Add(new { name = item.Current.Name, id = item.Current.AutomationId, enabled = item.Current.IsEnabled, type = item.Current.ControlType.ProgrammaticName });
                throw new InvalidOperationException("Read-only inspection");
            }
            Check("Initial startup state is disabled", original == ToggleState.Off);
            bool nativeClick = args.Length > 3 && args[3] == "click";
            Click(checkbox, toggle, nativeClick);
            bool enabled = Until(() => checkbox.Current.IsEnabled && toggle.Current.ToggleState == ToggleState.On);
            checks.Add(new { name = "Observed checkbox after Toggle", state = toggle.Current.ToggleState.ToString(), enabled = checkbox.Current.IsEnabled });
            foreach (AutomationElement item in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartupStatus")))
                checks.Add(new { name = "Observed startup status", text = item.Current.Name });
            Check("Startup registration enable reflected in checkbox", enabled);
            var status = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartupStatus"));
            Check("Enabled startup status", status != null && (portable ? status.Current.Name.StartsWith("Ярлык автозапуска добавлен.") : status.Current.Name == "При входе запустится в трее с включённым исправлением."));
            if (portable) Check("Own Startup folder shortcut exists", File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LayoutSeek.lnk")));
            var fresh = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartupOption"));
            checks.Add(new { name = "Fresh checkbox before disable", oldHandle = checkbox.Current.NativeWindowHandle, handle = fresh.Current.NativeWindowHandle, state = ((TogglePattern)fresh.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState.ToString() });
            checkbox = fresh; toggle = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
            Click(checkbox, toggle, nativeClick);
            Check("Startup registration disable reflected in checkbox", Until(() => checkbox.Current.IsEnabled && toggle.Current.ToggleState == ToggleState.Off));
            Check("Disabled startup status", status.Current.Name == (portable ? "Ярлык автозапуска не добавлен." : "При входе в Windows программа не запускается."));
            if (portable) Check("Own Startup folder shortcut removed", !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LayoutSeek.lnk")));
        }
        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
        finally
        {
            if (toggle != null && original == ToggleState.Off && toggle.Current.ToggleState == ToggleState.On)
            {
                try { toggle.Toggle(); Until(() => toggle.Current.ToggleState == ToggleState.Off); } catch (Exception e) { error = (error ?? "") + " Restore: " + e.Message; }
            }
        }
        var result = new { time = DateTimeOffset.Now.ToString("o"), layer = "Real application UI and startup registration, no logoff/login", pid = int.Parse(args[0]), checks, error, logonTested = false };
        string json = new JavaScriptSerializer().Serialize(result);
        File.WriteAllText(args[2], json, new System.Text.UTF8Encoding(false)); Console.WriteLine(json);
        return error == null ? 0 : 1;
    }
}
