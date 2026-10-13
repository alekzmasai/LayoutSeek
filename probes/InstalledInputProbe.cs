using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;
using System.Web.Script.Serialization;
using System.Collections.Generic;
namespace SearchLayout {
internal static class InstalledInputProbe {
 [STAThread] static int Main(string[] args) {
  TestInput.SetProcessDpiAwarenessContext(new IntPtr(-4));
  var results=new List<object>(); int failed=0;
  try {
  using(var app=Process.GetProcessById(Int32.Parse(args[0]))) {
   if(!String.Equals(app.MainModule.FileName,Path.GetFullPath(args[1]),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Product identity changed");
   var window=AutomationElement.FromHandle(app.MainWindowHandle);
   var on=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Выключить"));
   if(on!=null)((InvokePattern)on.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
   Thread.Sleep(300);
   if(window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Включить"))==null)throw new InvalidOperationException("Product must be OFF");
   try { foreach(int delay in new[]{0,0,350,350}) {
    TestInput.Open(false); Thread.Sleep(delay);
    TestInput.Type("с",35); var first=SearchTarget.Read();
    TestInput.Type("р",35); var second=SearchTarget.Read();
    Thread.Sleep(500); var settled=SearchTarget.Read();
    bool pass=first!=null&&first.Value=="с"&&second!=null&&second.Value=="ср"&&settled!=null&&settled.Value=="ср";
    results.Add(new{delay,first=first==null?null:first.Value,second=second==null?null:second.Value,settled=settled==null?null:settled.Value,pass});
    if(!pass)failed++;
   }} finally {TestInput.CloseShell();}
  }
  } catch(Exception ex) {failed++; results.Add(new{error=ex.Message});}
  File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{ProductDisabled=true,Failed=failed,Results=results})); return failed==0?0:1;
 }
}}
