using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace SearchLayout
{
    public sealed class AppCatalog
    {
        readonly HashSet<string> fullNames;
        readonly HashSet<string> prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly string[] Names;
        public AppCatalog(IEnumerable<string> names)
        {
            Names = names.Where(n => !String.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            fullNames = new HashSet<string>(Names, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Names.Length; i++)
            {
                string name = Names[i];
                prefixes.Add(name);
                foreach (Match token in Regex.Matches(name, @"[\p{L}\p{Nd}][\p{L}\p{Nd}+#._-]*"))
                    if (token.Value.Length >= 2) prefixes.Add(token.Value);
            }
        }
        public bool HasPrefix(string query) { return !String.IsNullOrEmpty(query) && prefixes.Any(a => a.StartsWith(query, StringComparison.OrdinalIgnoreCase)); }
        public bool IsFullName(string query) { return fullNames.Contains(query); }
        static readonly Lazy<KeyboardLayoutCatalog> defaultLayouts = new Lazy<KeyboardLayoutCatalog>(KeyboardLayoutCatalog.ReadAvailable);
        public static AppCatalog ReadInstalled()
        {
            dynamic shell = null, folder = null, items = null;
            var names = new List<string>();
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                folder = shell.NameSpace("shell:AppsFolder");
                if (folder == null) throw new InvalidOperationException("AppsFolder is unavailable");
                items = folder.Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = null;
                    try { item = items.Item(i); names.Add((string)item.Name); }
                    finally { if (item != null) Marshal.FinalReleaseComObject(item); }
                }
            }
            finally
            {
                if (items != null) Marshal.FinalReleaseComObject(items);
                if (folder != null) Marshal.FinalReleaseComObject(folder);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
            return new AppCatalog(names);
        }
        public bool TryCorrect(string query, out string corrected) { return TryCorrect(query, false, out corrected); }
        public bool TryCorrect(string query, bool preferEnglishShortPrefix, out string corrected)
        {
            corrected = null;
            if (query == null || query.Length < 2 || query.Length > 80 || query != query.Trim()) return false;
            KeyboardLayoutCandidate candidate; LayoutMappingFailure failure;
            if (IsFullName(query) || !defaultLayouts.Value.TryUniqueCandidate(query, HasPrefix, out candidate, out failure)) return false;
            if (HasPrefix(query) && !(preferEnglishShortPrefix && candidate.Pair.Target.IsEnglish && query.Length == 2)) return false;
            corrected = candidate.Value;
            return true;
        }
    }

    internal sealed class CatalogRefresh : IDisposable
    {
        readonly object gate = new object();
        readonly Func<AppCatalog> load;
        readonly Func<long> clock;
        readonly long interval;
        AppCatalog current;
        long completedAt;
        bool running, disposed;
        public CatalogRefresh(AppCatalog initial, Func<AppCatalog> load)
            : this(initial, load, () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency, 60000) { }
        internal CatalogRefresh(AppCatalog initial, Func<AppCatalog> load, Func<long> clock, long interval)
        {
            current = initial; this.load = load; this.clock = clock; this.interval = interval; completedAt = clock();
        }
        public AppCatalog Current { get { return Volatile.Read(ref current); } }
        public bool RequestIfDue()
        {
            lock (gate)
            {
                if (disposed || load == null || running || clock() - completedAt < interval) return false;
                running = true;
                var thread = new Thread(Refresh) { IsBackground = true, Name = "Installed application catalog refresh" };
                thread.SetApartmentState(ApartmentState.MTA);
                try { thread.Start(); }
                catch { running = false; completedAt = clock(); throw; }
                return true;
            }
        }
        void Refresh()
        {
            AppCatalog next = null;
            try { next = load(); }
            catch (Exception) { /* Preserve the last usable snapshot on shell enumeration failure. */ }
            finally
            {
                lock (gate)
                {
                    if (!disposed && next != null) Volatile.Write(ref current, next);
                    completedAt = clock(); running = false;
                }
            }
        }
        public void Dispose() { lock (gate) { disposed = true; } }
    }

    public sealed class QueryPlan
    {
        public string Value, Source;
        public bool RestoreOriginal;
        public KeyboardLayoutPair Pair;
    }

    // Only text previously changed by this instance owns a continuation session.
    // No global mixed-alphabet rewriting is permitted.
    public sealed class QuerySession
    {
        string output, source;
        KeyboardLayoutPair pair;
        KeyboardLayoutCatalog layouts;
        public QuerySession() : this(KeyboardLayoutCatalog.ReadAvailable()) { }
        public QuerySession(KeyboardLayoutCatalog layouts) { this.layouts = layouts; }
        public void UpdateLayouts(KeyboardLayoutCatalog value) { layouts = value; Reset(); }
        public bool SupportsActiveLayout(IntPtr handle) { return layouts.Layouts.Any(l => l.Handle == handle && l.Supported); }
        public void Reset() { output = source = null; pair = null; }
        public void Accept(QueryPlan plan)
        {
            if (plan.RestoreOriginal) { Reset(); return; }
            output = plan.Value; source = plan.Source; pair = plan.Pair;
        }
        public QueryPlan Plan(string value, AppCatalog catalog, bool preferEnglishShortPrefix)
        { return Plan(value, catalog, preferEnglishShortPrefix, IntPtr.Zero); }
        public QueryPlan Plan(string value, AppCatalog catalog, bool preferEnglishShortPrefix, IntPtr activeLayout)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 80) { Reset(); return null; }
            if (output != null)
            {
                if (value == output) return null;
                if (output.StartsWith(value, StringComparison.Ordinal))
                {
                    source = source.Substring(0, value.Length); output = value;
                    return null;
                }
                int common = 0;
                while (common < output.Length && common < value.Length && output[common] == value[common]) common++;
                if (common > 0)
                {
                    string tail = value.Substring(common);
                    string converted; LayoutMappingFailure failure;
                    if (pair != null && pair.TryConvert(tail, out converted, out failure))
                    {
                        string original = source.Substring(0, common) + tail;
                        string mapped = output.Substring(0, common) + converted;
                        if (catalog.HasPrefix(mapped))
                        {
                            if (mapped == value) { output = value; source = original; return null; }
                            return new QueryPlan { Value = mapped, Source = original, Pair = pair };
                        }
                        if (converted != tail)
                            return new QueryPlan { Value = original, Source = original, Pair = pair, RestoreOriginal = true };
                    }
                }
                Reset();
            }
            if (value.Length < 2 || value != value.Trim() || catalog.IsFullName(value)) return null;
            KeyboardLayoutCandidate candidate; LayoutMappingFailure reason;
            if (!layouts.TryUniqueCandidate(value, catalog.HasPrefix, activeLayout, out candidate, out reason)) return null;
            if (catalog.HasPrefix(value) && !(preferEnglishShortPrefix && candidate.Pair.Target.IsEnglish && value.Length == 2)) return null;
            return new QueryPlan { Value = candidate.Value, Source = value, Pair = candidate.Pair };
        }
    }

    public static class PhysicalLayout
    {
        const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?!@#$%^&*()_+";
        const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,!\"№;%:?*()_+";
        public static string Convert(string text, bool fromRussian)
        {
            string from = fromRussian ? Ru : En, to = fromRussian ? En : Ru;
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                int position = from.IndexOf(chars[i]);
                if (position >= 0) chars[i] = to[position];
            }
            return new string(chars);
        }
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Tick; }
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput value);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        public static uint LastInputTick()
        {
            var value = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            if (!GetLastInputInfo(ref value)) throw new InvalidOperationException("GetLastInputInfo failed");
            return value.Tick;
        }
        public static bool ModifierPressed()
        {
            return (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;
        }
    }

    public sealed class SearchSnapshot
    {
        public IntPtr Hwnd;
        public IntPtr KeyboardLayout;
        public uint Pid, InputTick;
        public string RuntimeId, Value;
        public int Caret;
        public bool Collapsed, Coherent;
        internal AutomationElement Element;
        internal ValuePattern Pattern;
        public bool SameAs(SearchSnapshot other)
        {
            return other != null && Hwnd == other.Hwnd && Pid == other.Pid && RuntimeId == other.RuntimeId && Value == other.Value && Caret == other.Caret && Collapsed == other.Collapsed && InputTick == other.InputTick && KeyboardLayout == other.KeyboardLayout;
        }
    }

    public static class SearchTarget
    {
        // The diagnostic harness may observe synthetic test data; the product does not attach a sink.
        public static Action<string> Diagnostic;
        internal static void Trace(string value) { var sink = Diagnostic; if (sink != null) sink(value); }
        static readonly string ExpectedImage = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe");
        public static SearchSnapshot Read()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            uint pid;
            uint threadId = Native.GetWindowThreadProcessId(hwnd, out pid);
            using (Process p = Process.GetProcessById((int)pid))
            {
                if (p.ProcessName != "SearchHost" || !String.Equals(p.MainModule.FileName, ExpectedImage, StringComparison.OrdinalIgnoreCase)) return null;
            }
            uint inputBefore = Native.LastInputTick();
            AutomationElement node = AutomationElement.FocusedElement;
            if (node == null) return null;
            var c = node.Current;
            if (c.ProcessId != pid || c.AutomationId != "SearchTextBox" || c.ClassName != "RichEditBox" || c.FrameworkId != "XAML" || c.ControlType != ControlType.Edit || !c.IsEnabled || c.IsOffscreen || !c.HasKeyboardFocus) return null;
            var pattern = node.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
            if (pattern == null || pattern.Current.IsReadOnly) return null;
            var text = node.GetCurrentPattern(TextPattern.Pattern) as TextPattern;
            if (text == null) return null;
            string valueBefore = pattern.Current.Value;
            var selection = text.GetSelection();
            if (selection.Length != 1) return null;
            var range = text.DocumentRange.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            string value = pattern.Current.Value;
            var result = new SearchSnapshot { Hwnd = hwnd, Pid = pid, InputTick = Native.LastInputTick(), RuntimeId = String.Join(".", node.GetRuntimeId()), Value = value, Caret = range.GetText(-1).Length, Collapsed = selection[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End) == 0, Element = node, Pattern = pattern };
            result.KeyboardLayout = KeyboardLayoutNative.GetKeyboardLayout(threadId);
            if (Native.GetForegroundWindow() != hwnd || !node.Current.HasKeyboardFocus) return null;
            result.Coherent = valueBefore == value && pattern.Current.Value == value && inputBefore == result.InputTick && result.InputTick == Native.LastInputTick();
            return result;
        }
        public static ReplaceResult TryReplace(SearchSnapshot expected, string replacement, Func<bool> isCurrent)
        {
            SearchSnapshot fresh = Read();
            if (!isCurrent() || !expected.SameAs(fresh) || !fresh.Coherent || !fresh.Collapsed || fresh.Caret != fresh.Value.Length || Native.ModifierPressed()) return ReplaceResult.Skipped;
            if (Native.GetForegroundWindow() != fresh.Hwnd || !fresh.Element.Current.HasKeyboardFocus || fresh.Pattern.Current.Value != expected.Value || Native.LastInputTick() != fresh.InputTick || !isCurrent()) return ReplaceResult.Skipped;
            // All final value/focus/selection checks, SetValue and caret movement execute
            // together in the system provider. No separate write or caret fallback is allowed.
            return SearchAtomicNative.Replace(fresh, replacement);
        }
    }

    public enum ReplaceResult { Skipped, Verified, Unconfirmed }

    public sealed class CorrectionEvent
    {
        public string Before, After;
        public bool Verified;
        public DateTime Time;
    }

    public sealed class CorrectionEngine : IDisposable
    {
        readonly CatalogRefresh catalog;
        readonly Thread worker;
        volatile bool stop;
        int enabled, generation, count;
        public const int IdleMilliseconds = 300;
        public event Action<CorrectionEvent> Corrected;
        public event Action<string> Faulted;
        public int Count { get { return Volatile.Read(ref count); } }
        public bool Enabled
        {
            get { return Volatile.Read(ref enabled) != 0; }
            set { Interlocked.Exchange(ref enabled, value ? 1 : 0); Interlocked.Increment(ref generation); }
        }
        public CorrectionEngine(AppCatalog catalog, Func<AppCatalog> refreshCatalog = null)
        {
            this.catalog = new CatalogRefresh(catalog, refreshCatalog);
            worker = new Thread(Run) { IsBackground = true, Name = "Windows Search UIA observer" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        void Run()
        {
            SearchSnapshot previous = null;
            long stableSince = 0;
            int previousGeneration = -1;
            string targetIdentity = null;
            QuerySession session = null;
            IntPtr[] knownLayouts = null;
            while (!stop)
            {
                Thread.Sleep(70);
                int epoch = Volatile.Read(ref generation);
                if (!Enabled) { previous = null; targetIdentity = null; if (session != null) session.Reset(); continue; }
                try
                {
                    if (session == null) { knownLayouts = KeyboardLayoutNative.AvailableHandles(); session = new QuerySession(); }
                    SearchSnapshot current = SearchTarget.Read();
                    if (current == null) { previous = null; targetIdentity = null; session.Reset(); continue; }
                    // A multi-call observation during typing is not evidence of a user caret edit.
                    if (!current.Coherent) { previous = null; continue; }
                    string identity = current.Hwnd + "/" + current.RuntimeId + "/" + current.KeyboardLayout;
                    if (identity != targetIdentity || epoch != previousGeneration)
                    {
                        bool newTarget = identity != targetIdentity;
                        session.Reset(); targetIdentity = identity;
                        if (newTarget)
                        {
                            catalog.RequestIfDue();
                            var available = KeyboardLayoutNative.AvailableHandles();
                            if (!available.SequenceEqual(knownLayouts)) { session.UpdateLayouts(KeyboardLayoutCatalog.ReadAvailable()); knownLayouts = available; }
                        }
                    }
                    // Search can select an inline completion beyond Value ("cha" + selected "tGPT").
                    // Keep ownership while that suggestion is selected; still never write into a selection.
                    if (current.Caret != current.Value.Length || current.Value.Length == 0) session.Reset();
                    if (epoch != previousGeneration || !current.SameAs(previous))
                    {
                        previous = current;
                        previousGeneration = epoch;
                        stableSince = Stopwatch.GetTimestamp();
                        continue;
                    }
                    if (!current.Collapsed || current.Caret != current.Value.Length || Native.ModifierPressed()) continue;
                    if (!session.SupportsActiveLayout(current.KeyboardLayout)) { session.Reset(); continue; }
                    long stableMs = (Stopwatch.GetTimestamp() - stableSince) * 1000 / Stopwatch.Frequency;
                    uint idleMs = unchecked((uint)Environment.TickCount - current.InputTick);
                    if (stableMs < IdleMilliseconds || idleMs < IdleMilliseconds) continue;
                    QueryPlan plan = session.Plan(current.Value, catalog.Current, true, current.KeyboardLayout);
                    if (plan == null) continue;
                    string replacement = plan.Value;
                    ReplaceResult result = SearchTarget.TryReplace(current, replacement, () => !stop && Enabled && Volatile.Read(ref generation) == epoch);
                    previous = null;
                    if (result == ReplaceResult.Skipped) continue;
                    bool verified = result == ReplaceResult.Verified;
                    if (verified) { session.Accept(plan); Interlocked.Increment(ref count); }
                    Action<CorrectionEvent> handler = Corrected;
                    if (handler != null) handler(new CorrectionEvent { Before = current.Value, After = replacement, Verified = verified, Time = DateTime.Now });
                    if (!verified)
                    {
                        Enabled = false;
                        Action<string> fault = Faulted;
                        if (fault != null) fault("Исправление не подтверждено. Режим выключен.");
                    }
                }
                catch (ElementNotAvailableException) { previous = null; if (session != null) session.Reset(); targetIdentity = null; }
                catch (InvalidOperationException) { previous = null; if (session != null) session.Reset(); targetIdentity = null; }
                catch (System.ComponentModel.Win32Exception) { previous = null; if (session != null) session.Reset(); targetIdentity = null; }
                catch (Exception)
                {
                    Enabled = false;
                    previous = null;
                    Action<string> handler = Faulted;
                    if (handler != null) handler("Поле поиска недоступно. Режим выключен.");
                }
            }
        }
        public void Dispose() { stop = true; Enabled = false; catalog.Dispose(); worker.Join(600); }
    }
}
