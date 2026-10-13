using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace SearchLayout
{
    internal static class SearchAtomicNative
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("SearchNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern int SearchReplace(IntPtr window,uint pid,int[] runtimeId,int runtimeIdLength,string expected,string replacement,out int outcome);
        internal static ReplaceResult Replace(SearchSnapshot expected,string replacement)
        {
            int[] runtime=expected.RuntimeId.Split('.').Select(Int32.Parse).ToArray();
            int outcome;
            int hr=SearchReplace(expected.Hwnd,expected.Pid,runtime,runtime.Length,expected.Value,replacement,out outcome);
            if(hr<0) Marshal.ThrowExceptionForHR(hr);
            return outcome==2 ? ReplaceResult.Verified : outcome==0 ? ReplaceResult.Skipped : ReplaceResult.Unconfirmed;
        }
    }
}
