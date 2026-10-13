using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace SearchLayout
{
    static class TestInput
    {
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        static int ReadLauncher(bool start,out Rect rect,out IntPtr hwnd)
        {
            rect = new Rect(); hwnd = IntPtr.Zero;
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestShellLocator.exe");
            // A separate modern client avoids sharing the legacy managed UIA client's taskbar view.
            using (var child = Process.Start(new ProcessStartInfo(exe, start ? "start" : "search") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                DateTime created = child.StartTime;
                if (!child.WaitForExit(5000))
                {
                    if (!child.HasExited && child.StartTime == created && String.Equals(child.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) { child.Kill(); child.WaitForExit(2000); }
                    throw new TimeoutException("Taskbar fixture timed out; no input sent");
                }
                if (child.ExitCode != 0) return unchecked((int)0x80004005);
                string[] values = child.StandardOutput.ReadToEnd().Trim().Split(' ');
                if (values.Length != 5) throw new InvalidOperationException("Unexpected taskbar geometry");
                hwnd = new IntPtr(Int64.Parse(values[0])); rect = new Rect { Left = Int32.Parse(values[1]), Top = Int32.Parse(values[2]), Right = Int32.Parse(values[3]), Bottom = Int32.Parse(values[4]) }; return 0;
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct Mouse { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct Key { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] struct Union { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Key Key; }
        [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public Union Data; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr data, int length, out int needed);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static string TokenIdentity(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, (uint)pid), token = IntPtr.Zero, buffer = IntPtr.Zero;
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!OpenProcessToken(process, 8, out token)) throw new System.ComponentModel.Win32Exception();
                int length; GetTokenInformation(token, 25, IntPtr.Zero, 0, out length);
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, 25, buffer, length, out length)) throw new System.ComponentModel.Win32Exception();
                IntPtr sid = Marshal.ReadIntPtr(buffer); byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                Marshal.FreeHGlobal(buffer); buffer = Marshal.AllocHGlobal(4);
                if (!GetTokenInformation(token, 20, buffer, 4, out length)) throw new System.ComponentModel.Win32Exception();
                return "RID=" + rid + " / elevated=" + (Marshal.ReadInt32(buffer) != 0);
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
        }
        public static string Foreground()
        {
            uint pid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            using (var process = Process.GetProcessById((int)pid)) return process.ProcessName;
        }
        static void RequireShell()
        {
            string fg = Foreground();
            if (fg != "SearchHost" && fg != "StartMenuExperienceHost") throw new InvalidOperationException("Input cancelled: foreground is not Windows Search/Start");
        }
        static void Send(Input[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length) throw new InvalidOperationException("SendInput failed");
        }
        static Input KeyEvent(ushort vk, ushort scan, uint flags) { return new Input { Type = 1, Data = new Union { Key = new Key { Vk = vk, Scan = scan, Flags = flags } } }; }
        public static void KeyPress(ushort key, bool ctrl = false)
        {
            RequireShell();
            var list = new List<Input>();
            if (ctrl) list.Add(KeyEvent(0x11, 0, 0));
            list.Add(KeyEvent(key, 0, 0)); list.Add(KeyEvent(key, 0, 2));
            if (ctrl) list.Add(KeyEvent(0x11, 0, 2));
            Send(list.ToArray());
        }
        public static void Type(string text, int delay = 15)
        {
            foreach (char c in text)
            {
                RequireShell();
                Send(new[] { KeyEvent(0, c, 4), KeyEvent(0, c, 6) });
                Thread.Sleep(delay);
            }
        }
        public static void CloseShell()
        {
            for (int i = 0; i < 3 && (Foreground() == "SearchHost" || Foreground() == "StartMenuExperienceHost"); i++) { KeyPress(0x1B); Thread.Sleep(300); }
        }
        public static void ClickOwnedControl(Control control)
        {
            var rect = AutomationElement.FromHandle(control.Handle).Current.BoundingRectangle;
            var point = new Point { X = (int)(rect.Left + rect.Width / 2), Y = (int)(rect.Top + rect.Height / 2) };
            IntPtr found = WindowFromPoint(point);
            uint pid; Native.GetWindowThreadProcessId(found, out pid);
            if (pid != Process.GetCurrentProcess().Id || GetAncestor(found, 2) != control.FindForm().Handle) throw new InvalidOperationException("Synthetic test control is not at input point: x=" + point.X + ",y=" + point.Y + ", found-pid=" + pid + ", own-pid=" + Process.GetCurrentProcess().Id + ", root=" + GetAncestor(found, 2) + ", expected=" + control.FindForm().Handle);
            if (!SetCursorPos(point.X, point.Y)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed");
            Send(new[] { new Input { Data = new Union { Mouse = new Mouse { Flags = 2 } } }, new Input { Data = new Union { Mouse = new Mouse { Flags = 4 } } } });
        }
        public static void Open(bool fromStart)
        {
            CloseShell();
            IntPtr hwnd;
            Rect rect;
            int hr = ReadLauncher(fromStart, out rect, out hwnd);
            var launcherClock = Stopwatch.StartNew();
            while (hr < 0 && launcherClock.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(80);
                hr = ReadLauncher(fromStart, out rect, out hwnd);
            }
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var point = new Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            if (GetAncestor(WindowFromPoint(point), 2) != hwnd) throw new InvalidOperationException("Taskbar target changed");
            if (!SetCursorPos(point.X, point.Y)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed");
            Send(new[] { new Input { Data = new Union { Mouse = new Mouse { Flags = 2 } } }, new Input { Data = new Union { Mouse = new Mouse { Flags = 4 } } } });
            var clock = Stopwatch.StartNew();
            while (Foreground() != "SearchHost" && (!fromStart || Foreground() != "StartMenuExperienceHost") && clock.ElapsedMilliseconds < 2500) Thread.Sleep(30);
            if (Foreground() != "SearchHost" && (!fromStart || Foreground() != "StartMenuExperienceHost")) throw new InvalidOperationException("Expected shell did not open; foreground=" + Foreground());
            Thread.Sleep(180);
            if (!fromStart)
            {
                SearchSnapshot field = null;
                var focusClock = Stopwatch.StartNew();
                while (focusClock.ElapsedMilliseconds < 1500 && field == null) { field = SearchTarget.Read(); if (field == null) Thread.Sleep(25); }
                if (field == null) throw new InvalidOperationException("Search field not focused during fixture setup");
                field.Pattern.SetValue("");
                Thread.Sleep(90);
                field = SearchTarget.Read();
                if (field == null) throw new InvalidOperationException("Search fixture lost focus; foreground=" + Foreground());
                // Explicitly select the fixture after Search has processed its reset.
                // This also replaces any provider-inserted placeholder character on the next real input.
                ((TextPattern)field.Element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.Select();
                field = SearchTarget.Read();
                if (field == null || (field.Value.Length > 0 && field.Collapsed)) throw new InvalidOperationException("Search fixture selection unavailable");
            }
        }
        public static void OpenStartWithShortcut()
        {
            Open(false);
            KeyPress(0x1B, true);
            Thread.Sleep(500);
            if (Foreground() != "StartMenuExperienceHost") throw new InvalidOperationException("Ctrl+Esc did not open Start; foreground=" + Foreground());
        }
    }

    public static class Tests
    {
        static readonly List<object> records = new List<object>();
        static int failed;
        static string ProductExecutable { get { string explicitPath = Environment.GetEnvironmentVariable("SEARCHLAYOUT_TEST_PRODUCT_PATH"); return String.IsNullOrEmpty(explicitPath) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SearchLayout.exe") : Path.GetFullPath(explicitPath); } }
        static void Record(string name, bool pass, string expected, string actual)
        {
            if (!pass) failed++;
            var record = new { Time = DateTime.Now.ToString("o"), Test = name, Pass = pass, Expected = expected, Actual = actual };
            records.Add(record);
            Console.WriteLine(new JavaScriptSerializer().Serialize(record));
        }
        static void CheckCorrection(AppCatalog catalog, string input, string expected, string label)
        {
            string output; bool did = catalog.TryCorrect(input, out output);
            Record(label, expected == null ? !did : did && output == expected, expected ?? "unchanged", did ? output : "unchanged");
        }
        static void Units()
        {
            var catalog = new AppCatalog(new[] { "Google Chrome", "Калькулятор", "Блокнот", "Paint", "Visual Studio Code" });
            CheckCorrection(catalog, "сркщьу", "chrome", "RU to EN unique app word");
            CheckCorrection(catalog, ",kjryjn", "блокнот", "EN to RU unique app");
            CheckCorrection(catalog, "пщщпду сркщьу", "google chrome", "Full installed title");
            CheckCorrection(catalog, "chrome", null, "Correct EN unchanged");
            CheckCorrection(catalog, "Калькулятор", null, "Correct RU unchanged");
            CheckCorrection(catalog, "chr", null, "Correct prefix unchanged");
            CheckCorrection(catalog, "срк", "chr", "Partial wrong prefix converted");
            CheckCorrection(catalog, "сhrome", null, "Mixed alphabet unchanged");
            CheckCorrection(catalog, " сркщьу", null, "Leading space unchanged");
            CheckCorrection(catalog, "unknown", null, "Unknown app unchanged");
            CheckCorrection(new AppCatalog(new[] { "Google Chrome", "Chrome Beta" }), "сркщьу", "chrome", "Multiple results remain a valid search");
            CheckCorrection(new AppCatalog(new[] { "Chrome", "Chrome" }), "сркщьу", "chrome", "Duplicate app entries do not make query ambiguous");
            CheckCorrection(new AppCatalog(new[] { "ChatGPT", "ChatGPT (Beta)", "ChatGPT Classic" }), "срфепзе", "chatgpt", "Exact full name wins over shared edition words");
            CheckCorrection(new AppCatalog(new[] { "ChatGPT", "ChatGPT" }), "срфепзе", "chatgpt", "Duplicate titles remain native search results");
            CheckCorrection(new AppCatalog(new[] { "ChatGPT (Beta)", "ChatGPT Classic" }), "срфепзе", "chatgpt", "Shared edition prefix remains a valid search");
            CheckCorrection(new AppCatalog(new[] { "Сркщьу", "Chrome" }), "сркщьу", null, "Valid original wins");
            string en = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?!@#$%^&*()_+";
            string ru = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,!\"№;%:?*()_+";
            Record("Physical keyboard both directions", PhysicalLayout.Convert(en, false) == ru && PhysicalLayout.Convert(ru, true) == en, "all mapped keys", "round-trip checked");
            var a = new SearchSnapshot { Hwnd = (IntPtr)1, Pid = 1, RuntimeId = "one", Value = "сркщьу", Caret = 6, Collapsed = true, InputTick = 10 };
            var b = new SearchSnapshot { Hwnd = (IntPtr)1, Pid = 1, RuntimeId = "one", Value = "сркщьуй", Caret = 7, Collapsed = true, InputTick = 11 };
            Record("Changed text/input invalidates snapshot", !a.SameAs(b), "different", a.SameAs(b).ToString());
        }
        static void SessionStep(QuerySession session, AppCatalog catalog, string input, string expected, string label)
        {
            var plan = session.Plan(input, catalog, true);
            string actual = plan == null ? input : plan.Value;
            Record(label, actual == expected, expected, actual);
            if (plan != null) session.Accept(plan);
        }
        static void PrefixUnits()
        {
            Units();
            var catalog = new AppCatalog(new[] { "ChatGPT", "ChatGPT (Beta)", "Cheat Engine", "Google Chrome", "Средство записи действий", "Калькулятор", "CPU-Z MSI", "Edit CPU-Z MSI Config File", "Uninstall CPU-Z MSI" });
            var session = new QuerySession();
            SessionStep(session, catalog, "ср", "ch", "Two-letter conflict prefers Latin");
            SessionStep(session, catalog, "chф", "cha", "Append original-layout character");
            SessionStep(session, catalog, "chaепзе", "chatgpt", "Continue original-layout suffix");
            SessionStep(session, catalog, "chatgp", "chatgp", "Plain Backspace does not rewrite");
            SessionStep(session, catalog, "chatgpе", "chatgpt", "Continue after deletion");
            SessionStep(session, catalog, "", "", "Clear resets session");
            SessionStep(session, catalog, "ср", "ch", "Same prefix after clear works again");
            SessionStep(session, catalog, "cр", "ch", "Fast tail deletion and replacement");
            SessionStep(session, catalog, "chочно", "срочно", "Restore Russian word after speculative prefix");
            SessionStep(session, catalog, "ср", "ch", "New prefix after rollback");
            SessionStep(session, catalog, "chеда", "среда", "Restore Russian short word");
            session.Reset();
            SessionStep(session, catalog, "сре", "сре", "Three-letter valid original prefix preserved");
            SessionStep(session, catalog, "ch", "ch", "Correct Latin prefix preserved");
            SessionStep(session, catalog, "chф", "chф", "Mixed suffix without owned session untouched");
            SessionStep(session, catalog, "сзг-я", "cpu-z", "CPU-Z prefix across multiple installed entries");
            session.Reset();
            SessionStep(session, catalog, "rf", "ка", "Reverse two-letter prefix");
            SessionStep(session, catalog, "каkmrekznjh", "калькулятор", "Reverse continuation");
            session.Reset();
            SessionStep(session, catalog, "ср", "ch", "Start session before focus change");
            session.Reset();
            SessionStep(session, catalog, "chф", "chф", "Session reset prevents mixed rewrite");
            session.Reset(); SessionStep(session, catalog, "ср", "ch", "Start session before intentional alphabet change");
            SessionStep(session, catalog, "cha", "cha", "User switched to target alphabet");
            SessionStep(session, catalog, "chaе", "chaе", "Intentional alphabet change cancels ownership");
            SessionStep(new QuerySession(), new AppCatalog(new[] { "ср", "Chrome" }), "ср", "ср", "Exact original title wins even at two letters");
            SessionStep(new QuerySession(), catalog, "как открыть настройки", "как открыть настройки", "Ordinary Russian phrase preserved");
            SessionStep(new QuerySession(), catalog, "open settings", "open settings", "Ordinary English phrase preserved");
            string outcome = "not completed"; bool passed = false;
            var loader = new Thread(() => { try { var live = AppCatalog.ReadInstalled(); string mapped; passed = live.Names.Length > 0 && live.TryCorrect("сзг-я", true, out mapped) && mapped == "cpu-z"; outcome = "entries=" + live.Names.Length; } catch (Exception e) { outcome = e.GetType().Name + ": " + e.Message; } }) { IsBackground = true };
            loader.SetApartmentState(ApartmentState.MTA); loader.Start();
            bool completed = loader.Join(10000);
            Record("Installed catalog can refresh from MTA worker", completed && passed, "system catalog and CPU-Z prefix", outcome);
            CatalogRefreshUnits();
        }
        static void CatalogRefreshUnits()
        {
            var initial = new AppCatalog(new[] { "Chrome" });
            var updated = new AppCatalog(new[] { "ChatGPT" });
            long now = 0; int calls = 0; Thread worker = null; ApartmentState apartment = ApartmentState.Unknown;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cache = new CatalogRefresh(initial, () => { worker = Thread.CurrentThread; apartment = worker.GetApartmentState(); Interlocked.Increment(ref calls); entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); return updated; }, () => Interlocked.Read(ref now), 60000))
            {
                Record("Fresh catalog does not refresh", !cache.RequestIfDue(), "no request before 60 seconds", "initial snapshot retained");
                Interlocked.Exchange(ref now, 60000);
                bool requested = cache.RequestIfDue(); bool started = entered.Wait(1000);
                var timer = Stopwatch.StartNew(); bool second = cache.RequestIfDue(); var usable = cache.Current; timer.Stop();
                Record("Slow refresh does not block snapshot or allow a second job", requested && started && !second && Object.ReferenceEquals(usable, initial) && calls == 1 && timer.ElapsedMilliseconds < 250, "old snapshot immediately; one MTA job", "elapsed=" + timer.ElapsedMilliseconds + " ms / calls=" + calls + " / apartment=" + apartment);
                release.Set(); bool joined = worker != null && worker.Join(1000);
                Record("Completed catalog is published atomically", joined && Object.ReferenceEquals(cache.Current, updated) && apartment == ApartmentState.MTA, "new snapshot after success", "joined=" + joined);
            }
            now = 0; calls = 0; worker = null;
            using (var entered = new AutoResetEvent(false))
            using (var cache = new CatalogRefresh(initial, () => { worker = Thread.CurrentThread; int attempt = Interlocked.Increment(ref calls); entered.Set(); if (attempt == 1) throw new InvalidOperationException("synthetic enumeration failure"); return updated; }, () => Interlocked.Read(ref now), 60000))
            {
                Interlocked.Exchange(ref now, 60000); cache.RequestIfDue(); entered.WaitOne(1000); bool joined = worker != null && worker.Join(1000);
                Record("Failed refresh preserves old snapshot", joined && Object.ReferenceEquals(cache.Current, initial), "old snapshot", "calls=" + calls);
                Interlocked.Exchange(ref now, 119999);
                Record("Failed refresh is not retried early", !cache.RequestIfDue() && calls == 1, "wait 60 seconds after failure", "calls=" + calls);
                Interlocked.Exchange(ref now, 120000); bool retry = cache.RequestIfDue(); entered.WaitOne(1000); joined = worker != null && worker.Join(1000);
                Record("Failed refresh retries after interval", retry && joined && calls == 2 && Object.ReferenceEquals(cache.Current, updated), "retry and publish", "calls=" + calls);
            }
            now = 0; worker = null;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cache = new CatalogRefresh(initial, () => { worker = Thread.CurrentThread; entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); return updated; }, () => Interlocked.Read(ref now), 60000))
            {
                Interlocked.Exchange(ref now, 60000); cache.RequestIfDue(); entered.Wait(1000); cache.Dispose(); release.Set(); bool joined = worker != null && worker.Join(1000);
                Record("Disposed catalog suppresses late publication and new work", joined && Object.ReferenceEquals(cache.Current, initial) && !cache.RequestIfDue(), "old snapshot; no new job", "joined=" + joined);
            }
        }
        static SearchSnapshot ReadSearch()
        {
            var s = SearchTarget.Read();
            if (s == null) throw new InvalidOperationException("Actual focused SearchTextBox unavailable; foreground=" + TestInput.Foreground() + "; hwnd=" + Native.GetForegroundWindow() + "; last-input=" + Native.LastInputTick());
            return s;
        }
        static string Value() { return ReadSearch().Value; }
        static void Wait(int ms)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(15); }
        }
        static void InputCase(CorrectionEngine engine, bool start, string input, string expected, string label, int delay = 25)
        {
            TestInput.Open(start);
            TestInput.Type(input, delay);
            Wait(1500);
            var s = ReadSearch();
            Record(label, s.Value == expected && s.Caret == expected.Length && engine.Enabled, expected + " / caret=" + expected.Length, s.Value + " / caret=" + s.Caret + " / enabled=" + engine.Enabled);
        }
        static void ScreenshotRegion(string path, int x, int y, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap)) { graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
        static void ProofImages(string name, int resultHeight = 240)
        {
            var field = ReadSearch();
            var root = AutomationElement.FromHandle(field.Hwnd).Current.BoundingRectangle;
            var input = field.Element.Current.BoundingRectangle;
            string folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "evidence"));
            ScreenshotRegion(Path.Combine(folder, name + "-result.png"), (int)root.Left + 30, (int)root.Top + 25, 440, resultHeight);
            ScreenshotRegion(Path.Combine(folder, name + "-query.png"), (int)input.Left, (int)input.Top, (int)input.Width, (int)input.Height);
        }
        static void InspectShell()
        {
            string fg = TestInput.Foreground();
            var hwnd = Native.GetForegroundWindow();
            Record("Passive shell foreground", true, "no input", fg + " / HWND=" + hwnd);
            if (fg == "SearchHost" || fg == "StartMenuExperienceHost")
            {
                var rect = AutomationElement.FromHandle(hwnd).Current.BoundingRectangle;
                Record("Foreground shell geometry", true, "visible window", rect.ToString());
                if (rect.Width > 0 && rect.Height > 0 && rect.Width < 2000 && rect.Height < 2000)
                    ScreenshotRegion(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "evidence", "user-opened-shell.png")), (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
            }
            foreach (var process in Process.GetProcessesByName("StartMenuExperienceHost"))
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                foreach (AutomationElement window in windows)
                    Record("Start window geometry", true, "passive observation", window.Current.NativeWindowHandle + " / offscreen=" + window.Current.IsOffscreen + " / " + window.Current.BoundingRectangle);
                process.Dispose();
            }
        }
        static void GuiSmoke(bool showOnly)
        {
            string exe = ProductExecutable;
            Process app = Process.GetProcessesByName("SearchLayout").FirstOrDefault(p => String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase));
            bool created = app == null;
            if (created) app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
            Wait(1200); app.Refresh();
            Record("GUI process identity", true, "own prototype", "PID=" + app.Id + " / start=" + app.StartTime.ToString("o") + " / created=" + created + " / path=" + app.MainModule.FileName);
            string token = TestInput.TokenIdentity(app.Id);
            Record("GUI medium integrity", token == "RID=8192 / elevated=False", "RID=8192 / elevated=False", token);
            TestInput.ShowWindow(app.MainWindowHandle, 9);
            TestInput.SetForegroundWindow(app.MainWindowHandle); Wait(300);
            var window = AutomationElement.FromHandle(app.MainWindowHandle);
            var button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            Record("GUI default off", button != null && button.Current.Name == "Включить", "Включить", button == null ? "missing" : button.Current.Name);
            if (button == null || button.Current.Name != "Включить") return;
            if (!showOnly)
            {
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Wait(350);
                Record("GUI on button", button.Current.Name == "Выключить", "Выключить", button.Current.Name);
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Wait(350);
                Record("GUI returned off", button.Current.Name == "Включить", "Включить", button.Current.Name);
            }
            if (Native.GetForegroundWindow() == app.MainWindowHandle)
            {
                var rect = window.Current.BoundingRectangle;
                ScreenshotRegion(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "evidence", "prototype-ui-off.png")), (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
            }
            // The user-facing, disabled window intentionally remains open.
            app.Dispose();
        }
        static void UserCheck()
        {
            var catalog = AppCatalog.ReadInstalled();
            string result; bool correct = catalog.TryCorrect("срфепзе", out result);
            Record("Installed ChatGPT candidates", true, "read only", String.Join(" | ", catalog.Names.Where(n => n.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0)));
            Record("Current matching for user query", correct, "chatgpt", result ?? "unchanged");
            foreach (var app in Process.GetProcessesByName("SearchLayout"))
            {
                if (!String.Equals(app.MainModule.FileName, ProductExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                var button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                Record("Actual GUI state read only", true, "current button", "PID=" + app.Id + " / button=" + (button == null ? "missing" : button.Current.Name) + " / offscreen=" + window.Current.IsOffscreen + " / rectangle=" + window.Current.BoundingRectangle);
                app.Dispose();
            }
            var search = SearchTarget.Read();
            Record("Actual Search state read only", search != null, "focused SearchTextBox", search == null ? "not focused" : "value=" + search.Value + " / caret=" + search.Caret + " / hwnd=" + search.Hwnd);
        }
        static string ProductStatus(AutomationElement window)
        {
            return String.Join(" | ", window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.StartsWith("Включено") || n.StartsWith("Выключено") || n.StartsWith("Не удалось")));
        }
        static void ProductValue(AutomationElement window, string expected, string label, int caret = -1)
        {
            var field = ReadSearch(); string status = ProductStatus(window);
            bool caretOk = expected.Length == 0 || field.Caret == (caret < 0 ? expected.Length : caret);
            bool pass = field.Value == expected && caretOk && status.StartsWith("Включено");
            Record(label, pass, expected, field.Value + " / caret=" + field.Caret + " / " + status);
            if (!pass) throw new InvalidOperationException("Product verification failed; remaining input cancelled");
        }
        static void ProductClear() { TestInput.KeyPress(0x41, true); TestInput.KeyPress(0x08); Wait(850); }
        static void WaitProductValue(AutomationElement window, string original, string expected, string label)
        {
            var elapsed = Stopwatch.StartNew();
            var initial = ReadSearch();
            SearchSnapshot current = initial;
            while (elapsed.ElapsedMilliseconds < 5000)
            {
                current = ReadSearch();
                if (current.Hwnd != initial.Hwnd || current.RuntimeId != initial.RuntimeId || (current.Value != original && current.Value != expected))
                    throw new InvalidOperationException("Observed target or query changed during read-only wait");
                if (!ProductStatus(window).StartsWith("Включено")) throw new InvalidOperationException("Product disabled during read-only wait");
                if (current.Value == expected && current.Collapsed && current.Caret == expected.Length)
                {
                    ProductValue(window, expected, label);
                    Record(label + " observed latency", true, "measurement after final synthetic input; not a UX threshold", elapsed.ElapsedMilliseconds + " ms / input-idle=" + unchecked((uint)Environment.TickCount - current.InputTick) + " ms");
                    return;
                }
                Wait(50);
            }
            Record(label + " timeout", false, expected + " within observation window", current.Value + " / caret=" + current.Caret + " / collapsed=" + current.Collapsed + " / elapsed=" + elapsed.ElapsedMilliseconds + " ms");
            throw new InvalidOperationException("Observed correction did not complete within 5 seconds");
        }
        static void ActualProduct(bool prefixMode)
        {
            string exe = ProductExecutable;
            var app = Process.GetProcessesByName("SearchLayout").FirstOrDefault(p => String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase));
            if (app == null) app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
            var ready = Stopwatch.StartNew();
            do { Wait(100); app.Refresh(); } while (app.MainWindowHandle == IntPtr.Zero && ready.ElapsedMilliseconds < 5000 && !app.HasExited);
            Record("Actual product process", true, "no test CorrectionEngine", "PID=" + app.Id + " / start=" + app.StartTime.ToString("o") + " / path=" + app.MainModule.FileName + " / " + TestInput.TokenIdentity(app.Id));
            var window = AutomationElement.FromHandle(app.MainWindowHandle);
            var button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            ready.Restart();
            while ((button == null || button.Current.Name != "Включить" || !button.Current.IsEnabled) && ready.ElapsedMilliseconds < 7000)
            {
                Wait(100);
                button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Включить"));
            }
            if (button == null || button.Current.Name != "Включить" || !button.Current.IsEnabled) throw new InvalidOperationException("Expected initialized disabled product");
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Wait(250);
            Record("Actual product enabled", button.Current.Name == "Выключить", "Выключить", button.Current.Name);
            string[] inputs = prefixMode ? new[] { "ср", "сзг-я", "rf", "сре", "ch" } : new[] { "срфепзе", "сркщьу" };
            string[] expected = prefixMode ? new[] { "ch", "cpu-z", "ка", "сре", "ch" } : new[] { "chatgpt", "chrome" };
            for (int i = 0; i < inputs.Length; i++)
            {
                TestInput.Open(false); TestInput.Type(inputs[i], 35);
                if (prefixMode && i == 0)
                {
                    var initial = ReadSearch();
                    var text = (TextPattern)initial.Element.GetCurrentPattern(TextPattern.Pattern);
                    var selected = text.GetSelection()[0];
                    var end = text.DocumentRange.Clone();
                    end.MoveEndpointByRange(TextPatternRangeEndpoint.End, selected, TextPatternRangeEndpoint.End);
                    Record("Initial synthetic prefix selection", true, "read-only observation before correction", "value=" + initial.Value + " / document=" + text.DocumentRange.GetText(-1).Replace("\r", "\\r").Replace("\n", "\\n") + " / start=" + initial.Caret + " / end=" + end.GetText(-1).Length + " / collapsed=" + initial.Collapsed + " / inputTick=" + initial.InputTick + " / idleMs=" + unchecked((uint)Environment.TickCount - initial.InputTick));
                }
                Wait(1700);
                ProductValue(window, expected[i], "Actual product " + inputs[i]);
                if (!prefixMode) ProofImages(i == 0 ? "product-chatgpt" : "product-chrome");
                else if (i == 0)
                {
                    ProofImages("prefix-ch", 420);
                    TestInput.Type("ф"); Wait(1200); ProductValue(window, "cha", "Continue source layout to cha");
                    TestInput.Type("епзе"); Wait(1200); ProductValue(window, "chatgpt", "Continue source layout to ChatGPT");
                    TestInput.KeyPress(0x08); Wait(950); ProductValue(window, "chatgp", "Delete corrected tail");
                    TestInput.Type("е"); Wait(1200); ProductValue(window, "chatgpt", "Retype source-layout tail");
                    ProductClear(); ProductValue(window, "", "Clear owned session");
                    TestInput.Type("ср"); Wait(1200); ProductValue(window, "ch", "Repeat prefix after clear");
                    TestInput.Type("очно"); Wait(1200); ProductValue(window, "срочно", "Restore ordinary Russian word");
                    ProductClear(); TestInput.Type("ср"); Wait(1200); TestInput.Type("еда"); Wait(1200); ProductValue(window, "среда", "Restore ordinary Russian short word");
                }
                else if (i == 1) ProofImages("prefix-cpuz");
                else if (i == 2)
                {
                    TestInput.Type("kmrekznjh"); Wait(1200); ProductValue(window, "калькулятор", "Reverse-layout continuation"); ProofImages("prefix-calculator");
                }
                else if (i == 4) { TestInput.Type("ф"); Wait(1200); ProductValue(window, "chф", "Unowned mixed query stays untouched"); }
            }
            if (prefixMode)
            {
                TestInput.Open(false); TestInput.Type("ср"); Wait(1200); TestInput.KeyPress(0x25); TestInput.Type("ф"); Wait(1200); ProductValue(window, "cфh", "Caret editing cancels owned session", 2);
                ProductFocusSwitch(window);
                TestInput.Open(true); TestInput.Type("ср"); Wait(1200); ProductValue(window, "ch", "Prefix after taskbar Start button");
            }
            TestInput.CloseShell();
            // The enabled user-facing product remains running; this driver never creates an engine.
            app.Dispose();
        }
        static void ProductFocusSwitch(AutomationElement window)
        {
            TestInput.CloseShell();
            Form form = null; TextBox box = null;
            using (var ready = new ManualResetEvent(false))
            {
                var ui = new Thread(() => {
                    using (var owned = new Form { Text = "SearchLayout synthetic non-target", TopMost = true, Size = new Size(460, 180), StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(20, 20) })
                    {
                        form = owned; box = new TextBox { Text = "ср", Dock = DockStyle.Top }; owned.Controls.Add(box);
                        owned.Shown += (s,e) => ready.Set(); Application.Run(owned);
                    }
                }) { IsBackground = true };
                ui.SetApartmentState(ApartmentState.STA); ui.Start();
                try {
                    if (!ready.WaitOne(5000)) throw new TimeoutException("Fixture message loop did not start");
                    TestInput.Open(false); string before = ProductStatus(window); TestInput.Type("ср", 1);
                    var switching = Stopwatch.StartNew(); bool activated = false;
                    form.Invoke((Action)(() => { TestInput.ShowWindow(form.Handle,9); activated=TestInput.SetWindowPos(form.Handle,new IntPtr(-1),20,20,460,180,0x0040); }));
                    TestInput.ClickOwnedControl(box);
                    bool focused = (bool)form.Invoke((Func<bool>)(() => Native.GetForegroundWindow()==form.Handle && box.Focused));
                    Record("Switch fixture is focused before correction delay", focused && switching.ElapsedMilliseconds < CorrectionEngine.IdleMilliseconds, "foreground fixture before idle threshold", "elapsed=" + switching.ElapsedMilliseconds + " ms / positioned=" + activated + " / foreground=" + TestInput.Foreground());
                    if(!focused) throw new InvalidOperationException("Own test fixture did not gain real foreground focus");
                    Wait(1100);
                    string unchanged = (string)form.Invoke((Func<string>)(() => box.Text));
                    Record("Actual product focus switch cancels pending prefix", unchanged == "ср" && ProductStatus(window) == before && Native.GetForegroundWindow() == form.Handle, "own fixture unchanged / counter unchanged", unchanged + " / " + ProductStatus(window));
                } finally { if(form!=null && form.IsHandleCreated) form.BeginInvoke((Action)(() => form.Close())); if(!ui.Join(5000)) throw new TimeoutException("Fixture message loop did not close"); }
            }
        }
        static void ActualFocus(int expectedPid)
        {
            string exe = ProductExecutable;
            using (var app = Process.GetProcessById(expectedPid))
            {
                if (!String.Equals(app.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase) || app.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Expected running product identity unavailable");
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                if (!ProductStatus(window).StartsWith("Включено")) throw new InvalidOperationException("Expected product already enabled");
                for(int i=0;i<5;i++) ProductFocusSwitch(window);
            }
        }
        static void ActualReverse(int expectedPid)
        {
            string exe = ProductExecutable;
            using (var app = Process.GetProcessById(expectedPid))
            {
                if (!String.Equals(app.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase) || app.MainWindowHandle == IntPtr.Zero)
                    throw new InvalidOperationException("Expected running product identity unavailable");
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                var button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                if (button == null || button.Current.Name != "Выключить" || !ProductStatus(window).StartsWith("Включено"))
                    throw new InvalidOperationException("Expected product already enabled; no state was changed");
                Record("Existing enabled product", true, "same product; no second engine", "PID=" + app.Id + " / start=" + app.StartTime.ToString("o") + " / " + TestInput.TokenIdentity(app.Id));
                TestInput.Open(false); TestInput.Type("rf", 35);
                WaitProductValue(window, "rf", "ка", "Reverse prefix on actual product");
                TestInput.Type("kmrekznjh", 35);
                WaitProductValue(window, "каkmrekznjh", "калькулятор", "Reverse continuation on actual product");
                // Value/selection and native result rendering complete independently.
                // This delay is only a capture opportunity, not proof that the results are ready.
                Wait(1000);
                ProductValue(window, "калькулятор", "Stable query before native result capture");
                ProofImages("prefix-calculator-native-final");
                Record("Native result image captured", true, "requires visual review", "prefix-calculator-native-final-result.png / prefix-calculator-native-final-query.png");
                TestInput.CloseShell();
            }
        }
        static Dictionary<string, string> ReadStringClipboard()
        {
            var data = Clipboard.GetDataObject(); var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data == null) return result;
            foreach (string format in data.GetFormats(false))
            {
                var value = data.GetData(format, false) as string;
                if (value == null) return null;
                result.Add(format, value);
            }
            return result;
        }
        static void ProductPaste(AutomationElement window)
        {
            uint initialSequence = TestInput.GetClipboardSequenceNumber();
            var original = ReadStringClipboard();
            if (original == null || TestInput.GetClipboardSequenceNumber() != initialSequence)
            {
                records.Add(new { Time = DateTime.Now.ToString("o"), Test = "Clipboard paste", Skipped = true, Reason = "Unsupported clipboard format or concurrent change; clipboard untouched" });
                return;
            }
            TestInput.Open(false);
            if (TestInput.GetClipboardSequenceNumber() != initialSequence) throw new InvalidOperationException("Clipboard changed before synthetic paste; no overwrite");
            uint ownedSequence = 0;
            try
            {
                Clipboard.SetText("сркщьу", TextDataFormat.UnicodeText); ownedSequence = TestInput.GetClipboardSequenceNumber();
                TestInput.KeyPress(0x56, true);
                WaitProductValue(window, "сркщьу", "chrome", "Clipboard paste corrected on actual product");
            }
            finally
            {
                if (ownedSequence != 0 && TestInput.GetClipboardSequenceNumber() == ownedSequence)
                {
                    if (original.Count == 0) Clipboard.Clear();
                    else { var data = new DataObject(); foreach (var entry in original) data.SetData(entry.Key, false, entry.Value); Clipboard.SetDataObject(data, true); }
                    var restored = ReadStringClipboard();
                    bool exact = restored != null && restored.Count == original.Count && original.All(e => restored.ContainsKey(e.Key) && restored[e.Key] == e.Value);
                    Record("Clipboard formats and values restored", exact, "exact original strings; contents never logged", "format-count=" + original.Count);
                }
                else Record("Clipboard restore cancelled after external change", false, "preserve newer clipboard contents", "no restore write performed");
            }
        }
        static void ActualExtra(int expectedPid)
        {
            string exe = ProductExecutable;
            using (var app = Process.GetProcessById(expectedPid))
            {
                if (!String.Equals(app.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase) || app.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Expected running product identity unavailable");
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                if (!ProductStatus(window).StartsWith("Включено")) throw new InvalidOperationException("Expected product already enabled");
                Record("Existing enabled product for extra scenarios", true, "no second engine", "PID=" + app.Id + " / start=" + app.StartTime.ToString("o"));
                TestInput.Open(false); TestInput.Type("сркщьу", 1); WaitProductValue(window, "сркщьу", "chrome", "Fast synthetic typing corrected");
                foreach (string title in new[] { "Google Chrome", "Калькулятор" })
                {
                    TestInput.Open(false); TestInput.Type(title, 5); Wait(1100); ProductValue(window, title, "Correct full title preserved: " + title);
                }
                ProductPaste(window);
                TestInput.Open(false); string before = ProductStatus(window); TestInput.Type("ср", 1); Wait(100); TestInput.CloseShell(); Wait(1100);
                string foreground = TestInput.Foreground(); string after = ProductStatus(window);
                Record("Closing Search cancels pending correction", before == after && foreground != "SearchHost" && foreground != "StartMenuExperienceHost", "counter unchanged after Search closes", after + " / foreground=" + foreground);
            }
        }
        static void GuiCapture()
        {
            string exe = ProductExecutable;
            using (var app = Process.GetProcessesByName("SearchLayout").First(p => String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)))
            {
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                var rect = window.Current.BoundingRectangle;
                if (rect.Width < 50 || rect.Height < 50 || rect.Width > 1800 || rect.Height > 1600) throw new InvalidOperationException("Unexpected own window geometry");
                using (var bitmap = new Bitmap((int)rect.Width, (int)rect.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc(); bool copied;
                    try { copied = TestInput.PrintWindow(app.MainWindowHandle, hdc, 0); }
                    finally { graphics.ReleaseHdc(hdc); }
                    Record("Own GUI PrintWindow", copied, "window pixels only; no focus change", "PID=" + app.Id + " / HWND=" + app.MainWindowHandle + " / " + rect);
                    if (copied) bitmap.Save(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "evidence", "prototype-ui-on.png")), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
        static void SwitchCase(CorrectionEngine engine)
        {
            TestInput.Open(false); TestInput.Type("сркщьу"); Wait(100);
            int beforeSwitch = engine.Count;
            using (var form = new Form { Text = "SearchLayout synthetic non-target", TopMost = true, Size = new Size(460, 180), StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(20, 20) })
            {
                var box = new TextBox { Text = "сркщьу", Dock = DockStyle.Top };
                form.Controls.Add(box); form.Show(); TestInput.ShowWindow(form.Handle, 5); Application.DoEvents(); TestInput.ClickOwnedControl(box); Wait(1100);
                bool ownForeground = TestInput.Foreground() == Process.GetCurrentProcess().ProcessName;
                Record("Verified switch to another window", ownForeground && box.Text == "сркщьу" && engine.Count == beforeSwitch && engine.Enabled, "synthetic text unchanged / no correction / enabled", box.Text + " / delta=" + (engine.Count - beforeSwitch) + " / foreground=" + ownForeground + " / enabled=" + engine.Enabled);
                form.Close();
            }
        }
        static void Live(string mode)
        {
            var catalog = AppCatalog.ReadInstalled();
            CheckCorrection(catalog, "сркщьу", "chrome", "Live installed Chrome identity");
            string wrongRussian = PhysicalLayout.Convert("калькулятор", true);
            CheckCorrection(catalog, wrongRussian, "калькулятор", "Live installed Russian app identity");
            if (failed != 0) return;
            using (var engine = new CorrectionEngine(catalog))
            {
                engine.Faulted += message => Console.WriteLine("ENGINE: " + message);
                engine.Corrected += e => Console.WriteLine("CORRECTION: " + e.Before + " -> " + e.After + "; verified=" + e.Verified);
                engine.Enabled = true;
                bool manualStarted = false;
                try
                {
                    if (mode == "diagprefix")
                    {
                        foreach (var app in Process.GetProcessesByName("SearchLayout"))
                        {
                            if (!String.Equals(app.MainModule.FileName, ProductExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                            var node = AutomationElement.FromHandle(app.MainWindowHandle).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                            if (node == null || node.Current.Name != "Включить") throw new InvalidOperationException("Actual product must be disabled before isolated diagnostic");
                            app.Dispose();
                        }
                        SearchTarget.Diagnostic = message => Console.WriteLine("UIA-DIAG " + DateTime.Now.ToString("o") + " " + message);
                        InputCase(engine, false, "ср", "ch", "Diagnostic prefix");
                        var beforeTail = ReadSearch(); var beforeText = (TextPattern)beforeTail.Element.GetCurrentPattern(TextPattern.Pattern);
                        Console.WriteLine("UIA-DIAG before continuation: value=" + beforeTail.Value + "; caret=" + beforeTail.Caret + "; collapsed=" + beforeTail.Collapsed + "; idle=" + unchecked((uint)Environment.TickCount - beforeTail.InputTick) + "; doc=" + beforeText.DocumentRange.GetText(-1).Replace("\r", "\\r") + "; selection=" + String.Join("|", beforeText.GetSelection().Select(r => r.GetText(-1).Replace("\r", "\\r"))));
                        TestInput.Type("ф"); Wait(1300);
                        var s = ReadSearch(); Record("Diagnostic continuation", s.Value == "cha" && s.Caret == 3 && engine.Enabled, "cha / caret3 / enabled", s.Value + " / caret=" + s.Caret + " / enabled=" + engine.Enabled);
                        return;
                    }
                    if (mode == "openstart") { TestInput.Open(true); InspectShell(); return; }
                    if (mode == "currentstart")
                    {
                        var initial = ReadSearch();
                        if (initial.Value.Length != 0) throw new InvalidOperationException("Current Start query is not empty; no input sent");
                        Record("User-opened visual Start precondition", true, "empty focused native field", "HWND=" + initial.Hwnd + " / PID=" + initial.Pid + " / value length=" + initial.Value.Length);
                        TestInput.Type("сркщьу", 35); Wait(1600);
                        var actual = ReadSearch();
                        Record("Typing in user-opened visual Start", actual.Value == "chrome" && actual.Caret == 6 && engine.Enabled, "chrome / caret=6", actual.Value + " / caret=" + actual.Caret);
                        ProofImages("manual-start-chrome");
                        return;
                    }
                    if (mode == "currentstartru")
                    {
                        var initial = ReadSearch();
                        if (initial.Value.Length != 0) throw new InvalidOperationException("Current Start query is not empty; no input sent");
                        TestInput.Type(wrongRussian, 35); Wait(1600);
                        var actual = ReadSearch();
                        Record("Typing Russian app in visual Start", actual.Value == "калькулятор" && actual.Caret == 11 && engine.Enabled, "калькулятор / caret=11", actual.Value + " / caret=" + actual.Caret);
                        ProofImages("start-calculator"); return;
                    }
                    if (mode == "manualstart")
                    {
                        var timer = Stopwatch.StartNew();
                        Console.WriteLine("OBSERVING: waiting up to 60 seconds for actual StartMenuExperienceHost; no input before that.");
                        while (TestInput.Foreground() != "StartMenuExperienceHost" && timer.ElapsedMilliseconds < 60000) Thread.Sleep(50);
                        if (TestInput.Foreground() != "StartMenuExperienceHost") { Record("Manual Start observation", false, "StartMenuExperienceHost", "timeout; no input sent"); return; }
                        manualStarted = true;
                        Record("Actual user-opened Start", true, "StartMenuExperienceHost", TestInput.Foreground() + " / HWND=" + Native.GetForegroundWindow());
                        TestInput.Type("сркщьу", 35); Wait(1600);
                        var actual = ReadSearch();
                        Record("User-opened Start automatic correction", actual.Value == "chrome" && actual.Caret == 6 && engine.Enabled, "chrome / caret=6", actual.Value + " / caret=" + actual.Caret);
                        ProofImages("manual-start-chrome");
                        return;
                    }
                    if (mode == "switch") { SwitchCase(engine); return; }
                    if (mode == "startkey")
                    {
                        string[] queries = { "сркщьу", wrongRussian };
                        string[] expected = { "chrome", "калькулятор" };
                        for (int i = 0; i < queries.Length; i++)
                        {
                            TestInput.OpenStartWithShortcut(); TestInput.Type(queries[i], 35); Wait(1600);
                            var s = ReadSearch();
                            Record("Typing after Ctrl+Esc Start " + i, s.Value == expected[i] && s.Caret == expected[i].Length && engine.Enabled, expected[i], s.Value + " / caret=" + s.Caret);
                            ProofImages(i == 0 ? "start-chrome" : "start-calculator");
                        }
                        return;
                    }
                    if (mode == "visual") { InputCase(engine, false, wrongRussian, "калькулятор", "Calculator visual proof fixture"); ProofImages("calculator"); InputCase(engine, false, "сркщьу", "chrome", "Chrome focused visual proof fixture"); ProofImages("chrome"); return; }
                    if (mode == "additional")
                    {
                        InputCase(engine, false, "как открыть настройки", "как открыть настройки", "Ordinary Russian query preserved");
                        InputCase(engine, false, "open settings", "open settings", "Ordinary English query preserved");
                        SwitchCase(engine);
                        InputCase(engine, false, wrongRussian, "калькулятор", "Calculator visual proof fixture"); ProofImages("calculator");
                        InputCase(engine, false, "сркщьу", "chrome", "Chrome focused visual proof fixture"); ProofImages("chrome");
                        InputCase(engine, true, "сркщьу", "chrome", "Start mouse + typing RU to EN"); ProofImages("start-chrome");
                        InputCase(engine, true, wrongRussian, "калькулятор", "Start mouse + typing EN to RU");
                        return;
                    }
                    if (mode == "start") { InputCase(engine, true, "сркщьу", "chrome", "Start typing automatic RU to EN"); InputCase(engine, true, wrongRussian, "калькулятор", "Start typing automatic EN to RU"); return; }
                    InputCase(engine, false, "сркщьу", "chrome", "Taskbar automatic RU to EN");
                    if (mode == "smoke" || failed != 0) return;
                    if (mode != "taskbar") InputCase(engine, true, "сркщьу", "chrome", "Start typing automatic RU to EN");
                    InputCase(engine, false, wrongRussian, "калькулятор", "Taskbar automatic EN to RU");
                    if (mode != "taskbar") InputCase(engine, true, wrongRussian, "калькулятор", "Start typing automatic EN to RU");
                    InputCase(engine, false, "Google Chrome", "Google Chrome", "Correct full English name preserved");
                    InputCase(engine, false, "Калькулятор", "Калькулятор", "Correct Russian name preserved");
                    TestInput.Open(false); TestInput.Type("сркщьу", 8); Wait(180);
                    Record("Fast typing before idle", Value() == "сркщьу", "сркщьу", Value());
                    Wait(1400);
                    Record("Fast typing after idle", Value() == "chrome", "chrome", Value());
                    TestInput.Open(false); TestInput.Type("сркщьу"); Wait(200); TestInput.KeyPress(0x08); Wait(1200);
                    Record("Backspace preserves shortened valid prefix", Value() == "chrom" && engine.Enabled, "chrom / enabled", Value() + " / " + engine.Enabled);
                    TestInput.KeyPress(0x41, true); TestInput.KeyPress(0x08); Wait(1100);
                    Record("Clear search stays empty", Value() == "", "empty", Value());
                    TestInput.Open(false); TestInput.Type("сркщьу"); Wait(180); TestInput.KeyPress(0x25); Wait(1100);
                    Record("Caret editing skips correction", Value() == "сркщьу", "сркщьу", Value());
                    TestInput.Open(false); TestInput.Type("сркщьу"); Wait(180); TestInput.KeyPress(0x41, true); Wait(1100);
                    Record("Selected text skips correction", Value() == "сркщьу", "сркщьу", Value());
                    TestInput.Open(false);
                    var saved = new DataObject();
                    IDataObject original = Clipboard.GetDataObject();
                    if (original != null) foreach (string format in original.GetFormats(false)) saved.SetData(format, false, original.GetData(format, false));
                    try { Clipboard.SetText("сркщьу"); TestInput.KeyPress(0x56, true); Wait(1500); Record("Real clipboard paste", Value() == "chrome", "chrome", Value()); }
                    finally { if (original == null) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); }
                    int beforeClose = engine.Count;
                    TestInput.Open(false); TestInput.Type("сркщьу"); Wait(180); TestInput.KeyPress(0x1B); Wait(1100);
                    Record("Closing Search cancels pending work", engine.Count == beforeClose && engine.Enabled, "no correction / enabled", (engine.Count - beforeClose) + " / " + engine.Enabled);
                    SwitchCase(engine);
                    engine.Enabled = false;
                    TestInput.Open(false); TestInput.Type("сркщьу"); Wait(1200);
                    Record("Off switch prevents correction", Value() == "сркщьу", "сркщьу", Value());
                    engine.Enabled = true; Wait(1300);
                    Record("On switch resumes correction", Value() == "chrome", "chrome", Value());
                    int stableCount = engine.Count; Wait(1600);
                    Record("No correction loop", engine.Count == stableCount && Value() == "chrome", "stable count and chrome", (engine.Count - stableCount) + " / " + Value());
                    int[] offsets = { 620, 650, 680, 700, 720, 740, 760, 780, 800, 820, 850, 900 };
                    foreach (int offset in offsets)
                    {
                        if (!engine.Enabled) break;
                        TestInput.Open(false); TestInput.Type("сркщьу", 8); Wait(offset); TestInput.Type("й", 0); Wait(1250);
                        var s = ReadSearch();
                        bool pass = (s.Value == "chromeй" || s.Value == "сркщьуй") && s.Caret == 7;
                        Record("Tail near debounce " + offset + "ms", pass, "chromeй or сркщьуй / caret=7", s.Value + " / caret=" + s.Caret + " / enabled=" + engine.Enabled);
                    }
                }
                finally { engine.Enabled = false; if (mode != "openstart" && (mode != "manualstart" || manualStarted)) TestInput.CloseShell(); }
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            TestInput.SetProcessDpiAwarenessContext(new IntPtr(-4));
            string mode = args.Length > 0 ? args[0] : "unit";
            string output = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests.json");
            try { if (mode == "unit") Units(); else if (mode == "prefixunit") PrefixUnits(); else if (mode == "inspect") InspectShell(); else if (mode == "usercheck") UserCheck(); else if (mode == "product" || mode == "prefixproduct") ActualProduct(mode == "prefixproduct"); else if (mode == "reverseproduct") ActualReverse(Int32.Parse(args[2])); else if (mode == "focusproduct") ActualFocus(Int32.Parse(args[2])); else if (mode == "extraproduct") ActualExtra(Int32.Parse(args[2])); else if (mode == "guicapture") GuiCapture(); else if (mode == "gui" || mode == "showgui") GuiSmoke(mode == "showgui"); else Live(mode); }
            catch (Exception e) { Record("Harness exception", false, "completed", e.GetType().Name + ": " + e.Message); }
            finally { File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { Time = DateTime.Now.ToString("o"), Mode = mode, Failed = failed, Records = records }), new UTF8Encoding(false)); }
            return failed == 0 ? 0 : 1;
        }
    }
}
