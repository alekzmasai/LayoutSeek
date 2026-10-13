using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SearchLayout
{
    internal enum StartupState { Disabled = 0, DisabledByUser = 1, Enabled = 2, DisabledByPolicy = 3, EnabledByPolicy = 4 }
    internal sealed class StartupResult
    {
        internal const int NoPackage = unchecked((int)0x80073D54);
        public readonly int HResult;
        public readonly StartupState? State;
        public readonly bool Portable;
        public bool Available { get { return HResult >= 0 && State.HasValue; } }
        public bool Enabled { get { return Available && (State == StartupState.Enabled || State == StartupState.EnabledByPolicy); } }
        public bool CanChange { get { return Available && (State == StartupState.Disabled || State == StartupState.Enabled); } }
        public StartupResult(int hresult, int state, bool portable = false)
        {
            Portable = portable;
            HResult = hresult;
            if (hresult >= 0)
            {
                if (state >= 0 && state <= 4) State = (StartupState)state;
                else HResult = unchecked((int)0x8000FFFF);
            }
        }
        public static StartupResult Failed() { return new StartupResult(unchecked((int)0x80004005), -1); }
    }
    internal interface IStartupService
    {
        Task<StartupResult> ReadAsync();
        Task<StartupResult> SetEnabledAsync(bool enabled);
    }
    internal sealed class StartupService : IStartupService
    {
        readonly PortableStartupService portable = new PortableStartupService();
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("SearchNative.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        static extern int SearchStartupState(out int state);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("SearchNative.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        static extern int SearchSetStartup(int enable, out int state);
        public Task<StartupResult> ReadAsync() { return Task.Run(() => Read()); }
        public Task<StartupResult> SetEnabledAsync(bool enabled)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Re-read before changing. A setting disabled by the user or policy is never bypassed.
                    var current = Read();
                    if (!current.CanChange || current.Enabled == enabled) return current;
                    if (current.Portable) return portable.SetEnabled(enabled);
                    int state; int result = SearchSetStartup(enabled ? 1 : 0, out state);
                    return new StartupResult(result, state);
                }
                catch (Exception e) { return new StartupResult(e.HResult, -1); }
            });
        }
        StartupResult Read()
        {
            try
            {
                int state; int result = SearchStartupState(out state);
                return result == StartupResult.NoPackage ? portable.Read() : new StartupResult(result, state);
            }
            catch (Exception e) { return new StartupResult(e.HResult, -1); }
        }
    }

    // The portable setting describes our shortcut, not Windows StartupApproved policy.
    internal sealed class PortableStartupService
    {
        const string Marker = "LayoutSeek portable startup v1";
        const string Arguments = "--startup";
        readonly string directory, executable, shortcut;
        readonly object gate = new object();

        internal PortableStartupService() : this(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SearchLayout.exe")) { }

        internal PortableStartupService(string startupDirectory, string executablePath)
        {
            directory = Path.GetFullPath(startupDirectory);
            executable = Path.GetFullPath(executablePath);
            shortcut = Path.Combine(directory, "LayoutSeek.lnk");
        }

        internal StartupResult Read()
        {
            lock (gate)
            {
                try { return ReadCore(); }
                catch (Exception e) { return new StartupResult(e.HResult, -1, true); }
            }
        }

        StartupResult ReadCore()
        {
            if (!File.Exists(shortcut)) return new StartupResult(0, (int)StartupState.Disabled, true);
            if (!OwnsShortcut(shortcut)) return new StartupResult(unchecked((int)0x80070005), -1, true);
            return new StartupResult(0, (int)StartupState.Enabled, true);
        }

        bool OwnsShortcut(string path)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = ((dynamic)shell).CreateShortcut(path);
                return String.Equals((string)((dynamic)link).Description, Marker, StringComparison.Ordinal)
                    && String.Equals(Path.GetFullPath((string)((dynamic)link).TargetPath), executable, StringComparison.OrdinalIgnoreCase)
                    && String.Equals((string)((dynamic)link).Arguments, Arguments, StringComparison.Ordinal);
            }
            finally { Release(link); Release(shell); }
        }

        internal StartupResult SetEnabled(bool enabled)
        {
            lock (gate)
            {
                string temporary = null;
                try
                {
                    var current = ReadCore();
                    if (!current.CanChange || current.Enabled == enabled) return current;
                    if (!enabled)
                    {
                        // ReadCore verified this exact shortcut belongs to this copy of LayoutSeek.
                        File.Delete(shortcut);
                    }
                    else
                    {
                        if (!File.Exists(executable)) throw new FileNotFoundException("Application executable is missing.");
                        Directory.CreateDirectory(directory);
                        temporary = Path.Combine(Path.GetTempPath(), "LayoutSeek-startup-" + Guid.NewGuid().ToString("N") + ".lnk");
                        object shell = null, link = null;
                        try
                        {
                            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                            link = ((dynamic)shell).CreateShortcut(temporary);
                            ((dynamic)link).TargetPath = executable;
                            ((dynamic)link).Arguments = Arguments;
                            ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(executable);
                            ((dynamic)link).Description = Marker;
                            ((dynamic)link).Save();
                        }
                        finally { Release(link); Release(shell); }
                        if (!OwnsShortcut(temporary)) throw new IOException("Startup shortcut verification failed.");
                        // Move refuses to overwrite a shortcut created by someone else in the meantime.
                        File.Move(temporary, shortcut);
                    }
                    return ReadCore();
                }
                catch (Exception e) { return new StartupResult(e.HResult, -1, true); }
                finally
                {
                    if (temporary != null && File.Exists(temporary))
                    {
                        try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    }
}
