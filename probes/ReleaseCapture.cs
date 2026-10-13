using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
internal static class ReleaseCapture
{
    sealed class Backdrop : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var value=base.CreateParams; value.ExStyle|=0x08000000; return value; } }
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int pid=Int32.Parse(args[0]); string expected=Path.GetFullPath(args[1]); string output=Path.GetFullPath(args[2]);
        using(var product=Process.GetProcessById(pid)) {
            if(!String.Equals(product.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase) || product.MainWindowHandle==IntPtr.Zero) throw new InvalidOperationException("Expected visible product identity unavailable");
            IntPtr window=product.MainWindowHandle; RECT original;
            if(!GetWindowRect(window,out original))throw new InvalidOperationException("Window geometry unavailable");
            var area=Screen.PrimaryScreen.WorkingArea;
            if(area.Width<1366 || area.Height<768)throw new InvalidOperationException("Capture requires at least 1366 x 768 physical pixels");
            if(GetForegroundWindow()!=window)throw new InvalidOperationException("Activate the product before capture");
            using(var backdrop=new Backdrop {FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Location=new Point(area.Left,area.Top),ClientSize=new Size(1366,768),BackColor=Color.FromArgb(238,240,243),ShowInTaskbar=false}) {
                try {
                    backdrop.Show(); Application.DoEvents();
                    int x=area.Left+(1366-original.Right+original.Left)/2, y=area.Top+(768-original.Bottom+original.Top)/2;
                    if(!SetWindowPos(backdrop.Handle,window,0,0,0,0,0x0001|0x0002|0x0010|0x0040)||!SetWindowPos(window,IntPtr.Zero,x,y,0,0,0x0001|0x0010|0x0040))throw new InvalidOperationException("Capture windows could not be positioned");
                    for(int i=0;i<10;i++){Application.DoEvents();Thread.Sleep(50);}
                    if(GetForegroundWindow()!=window)throw new InvalidOperationException("Foreground changed before screenshot");
                    using(var bitmap=new Bitmap(1366,768))using(var graphics=Graphics.FromImage(bitmap)){graphics.CopyFromScreen(area.Left,area.Top,0,0,bitmap.Size);bitmap.Save(output,ImageFormat.Png);}
                    Console.WriteLine("Actual screen pixels; 1366 x 768; product PID="+pid+"; path="+output);
                } finally {SetWindowPos(window,IntPtr.Zero,original.Left,original.Top,0,0,0x0001|0x0004|0x0010);backdrop.Close();}
            }
        }
        return 0;
    }
}
