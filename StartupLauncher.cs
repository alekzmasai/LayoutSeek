using System;
using System.Diagnostics;
using System.IO;

internal static class StartupLauncher
{
    [STAThread]
    static int Main()
    {
        try
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(directory, "SearchLayout.exe"),
                Arguments = "--startup",
                WorkingDirectory = directory,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })) { return process == null ? 1 : 0; }
        }
        catch { return 1; }
    }
}
