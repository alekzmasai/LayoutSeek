using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SearchLayout
{
    internal static class StartupTests
    {
        static int checks;
        static void Check(string name, bool condition)
        {
            if (!condition) throw new Exception(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        static void WriteShortcut(string path, string target, string description, string arguments)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = ((dynamic)shell).CreateShortcut(path);
                ((dynamic)link).TargetPath = target;
                ((dynamic)link).Arguments = arguments;
                ((dynamic)link).Description = description;
                ((dynamic)link).Save();
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }
        [STAThread]
        static int Main(string[] args)
        {
            string root = Path.GetFullPath(args[0]);
            if (Directory.Exists(root)) throw new InvalidOperationException("Use a new empty fixture directory.");
            Directory.CreateDirectory(root);
            string exe = Path.Combine(root, "SearchLayout.exe");
            File.WriteAllText(exe, "test fixture; never executed");
            string startup = Path.Combine(root, "Startup"), link = Path.Combine(startup, "LayoutSeek.lnk");
            var service = new PortableStartupService(startup, exe);
            try
            {
                var initial = service.Read();
                Check("fresh state is disabled and portable", initial.Portable && initial.Available && !initial.Enabled && initial.CanChange);
                Check("reading does not create a directory", !Directory.Exists(startup));
                Check("enable from thread pool creates an owned shortcut", Task.Run(() => service.SetEnabled(true)).Result.Enabled && File.Exists(link));
                byte[] bytes = File.ReadAllBytes(link);
                Check("read from thread pool confirms shortcut", Task.Run(() => service.Read()).Result.Enabled);
                Check("enable is idempotent", service.SetEnabled(true).Enabled && Convert.ToBase64String(bytes) == Convert.ToBase64String(File.ReadAllBytes(link)));
                Check("disable removes the owned shortcut", !service.SetEnabled(false).Enabled && !File.Exists(link));
                Check("disable is idempotent", service.SetEnabled(false).Available && !File.Exists(link));

                WriteShortcut(link, exe, "Someone else's shortcut", "--startup");
                string foreignBytes = Convert.ToBase64String(File.ReadAllBytes(link));
                Check("foreign shortcut blocks the setting", !service.Read().CanChange);
                Check("foreign shortcut cannot be enabled or deleted", !service.SetEnabled(true).Available && !service.SetEnabled(false).Available && foreignBytes == Convert.ToBase64String(File.ReadAllBytes(link)));
                File.Delete(link); // This fixture was created above in this new test directory.

                WriteShortcut(link, Path.Combine(root, "Other.exe"), "LayoutSeek portable startup v1", "--startup");
                string movedBytes = Convert.ToBase64String(File.ReadAllBytes(link));
                Check("another application path is preserved", !service.Read().Available && !service.SetEnabled(false).Available && movedBytes == Convert.ToBase64String(File.ReadAllBytes(link)));
                File.Delete(link);

                var missing = new PortableStartupService(startup, Path.Combine(root, "Missing.exe"));
                Check("missing executable cannot enable startup", !missing.SetEnabled(true).Available && !File.Exists(link));
                string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
                Check("filesystem errors do not report success", !new PortableStartupService(blocked, exe).SetEnabled(true).Available);
                Console.WriteLine("Passed " + checks + " startup checks. Windows sign-in was not performed.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
        }
    }
}
