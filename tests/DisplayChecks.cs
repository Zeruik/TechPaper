using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class DisplayChecks {
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint fallback);
    static IEnumerable<Control> Descendants(Control parent){
        foreach(Control c in parent.Controls){
            yield return c;
            foreach(var child in Descendants(c))yield return child;
        }
    }
    static void Pump(int ms){
        var elapsed=Stopwatch.StartNew();
        do{Application.DoEvents();Thread.Sleep(10);}while(elapsed.ElapsedMilliseconds<ms);
    }
    static void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    static Dictionary<IntPtr,uint> DisplayDpis(){
        var values=new Dictionary<IntPtr,uint>();
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previous!=IntPtr.Zero,"Cannot query per-monitor DPI");
        try{
            foreach(var screen in Screen.AllScreens){
                using(var probe=new Form{StartPosition=FormStartPosition.Manual,ShowInTaskbar=false}){
                    probe.Bounds=new Rectangle(screen.WorkingArea.Left+20,screen.WorkingArea.Top+20,80,80);
                    values[MonitorFromWindow(probe.Handle,2)]=GetDpiForWindow(probe.Handle);
                }
            }
        }finally{SetThreadDpiAwarenessContext(previous);}
        return values;
    }
    [STAThread] static void Main(string[] args){
        var dpis=DisplayDpis();
        Application.EnableVisualStyles();
        var type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("TechPaper.Workspace",true);
        using(var form=(Form)Activator.CreateInstance(type,true)){
            form.StartPosition=FormStartPosition.Manual;form.Show();
            type.GetMethod("LoadFile",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(form,new object[]{Path.GetFullPath(args[1])});
            var rows=Descendants(form).OfType<ListView>().Single();
            var wait=Stopwatch.StartNew();
            while(rows.Items.Count==0 && wait.ElapsedMilliseconds<15000)Pump(30);
            Require(rows.Items.Count>0,"Sample import failed");
            var screens=Screen.AllScreens.OrderByDescending(s=>s.Primary).ToList();
            var visited=new HashSet<IntPtr>();
            foreach(var screen in screens.Concat(screens.Take(1))){
                form.Bounds=new Rectangle(screen.WorkingArea.Left+12,screen.WorkingArea.Top+12,
                    form.MinimumSize.Width,form.MinimumSize.Height);
                Pump(200);
                var monitor=MonitorFromWindow(form.Handle,2);
                Require(Screen.FromHandle(form.Handle).DeviceName==screen.DeviceName,"Window did not move to target monitor");
                visited.Add(monitor);
                var controls=Descendants(form).Where(c=>c is Button || c is CheckBox || c is RichTextBox || c is ListView).ToList();
                foreach(var c in controls){
                    Require(c.Width>0 && c.Height>0 && c.Visible,"Missing control "+c.GetType().Name);
                    var rect=c.RectangleToScreen(c.ClientRectangle);
                    for(Control p=c.Parent;p!=null;p=p.Parent)
                        Require(p.RectangleToScreen(p.ClientRectangle).Contains(rect),"Clipped control "+c.Text);
                }
                var actions=controls.Where(c=>c is Button || c is CheckBox).ToList();
                var reached=new HashSet<Control>();
                form.Activate();rows.Focus();
                var tab=typeof(Form).GetMethod("ProcessDialogKey",BindingFlags.NonPublic|BindingFlags.Instance);
                for(int i=0;i<controls.Count*3;i++){
                    tab.Invoke(form,new object[]{Keys.Tab});
                    foreach(var action in actions)if(action.Focused)reached.Add(action);
                }
                Require(actions.All(reached.Contains),"Tab traversal missed an action");
                Console.WriteLine("PASS monitor DPI "+dpis[monitor]+", application DPI "+GetDpiForWindow(form.Handle)+
                    ", window "+form.Width+"x"+form.Height+", "+actions.Count+" keyboard actions");
            }
            Require(visited.Count==dpis.Count,"Not all detected monitors tested");
        }
    }
}
