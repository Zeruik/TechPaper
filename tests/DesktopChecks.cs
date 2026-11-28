using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TechPaper;
class DesktopChecks {
    static void Check(bool result,string name){if(!result)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Pump(Func<bool> done){var t=Stopwatch.StartNew();while(!done()){Application.DoEvents();Thread.Sleep(10);if(t.Elapsed.TotalSeconds>90)throw new TimeoutException();}}
    static System.Collections.Generic.IEnumerable<Control> Walk(Control c){foreach(Control child in c.Controls){yield return child;foreach(var item in Walk(child))yield return item;}}
    [STAThread]static void Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string folder=Path.Combine(Path.GetTempPath(),"TechPaper QA "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var f=new Workspace();f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-2500,0);f.Show();
        try{
            string src=Path.Combine(folder,"中文 样件.docx");File.Copy("tests/sample.docx",src);
            var data=new DataObject(DataFormats.FileDrop,new[]{src});
            typeof(Control).GetMethod("OnDragDrop",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,new object[]{new DragEventArgs(data,0,0,0,DragDropEffects.Copy,DragDropEffects.Copy)});
            var rows=Walk(f).OfType<ListView>().Single();Pump(()=>rows.Items.Count>2);Check(rows.CheckedItems.Count==0,"drop requires author selection");
            rows.Items[2].Selected=true;rows.Items[2].Focused=true;Application.DoEvents();
            Check(Walk(f).OfType<Button>().Where(x=>x.Enabled).All(x=>x.TabStop),"keyboard focusable actions");
            using(var left=new RichTextBox())using(var right=new RichTextBox()){
                string oldText="原样本 old [7] 温度 22.5 ℃；末段 slow。";
                string newText="原样本 new [7] 温度 22.5 ℃；末段 fast。";
                Workspace.ShowDifference(left,right,oldText,newText);
                foreach(var box in new[]{left,right}){
                    foreach(var token in new[]{"[7]","22.5","温度"}){
                        box.Select(box.Text.IndexOf(token),token.Length);
                        Check(box.SelectionBackColor!=Color.FromArgb(255,229,154),"unchanged evidence: "+token);
                    }
                    foreach(var token in box==left?new[]{"old","slow"}:new[]{"new","fast"}){
                        box.Select(box.Text.IndexOf(token),token.Length);
                        Check(box.SelectionBackColor==Color.FromArgb(255,229,154),"distinct change: "+token);
                    }
                }
                Workspace.ShowDifference(left,right,newText,newText);
                right.Select(newText.IndexOf("fast"),4);
                Check(right.SelectionBackColor!=Color.FromArgb(255,229,154),"clear stale highlights");
            }
            var manuscript=(Manuscript)typeof(Workspace).GetField("doc",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);
            int adopted=manuscript.Items.FindIndex(item=>item.Text!=item.Suggested);
            rows.Items[adopted].Selected=true;
            var modeField=typeof(Workspace).GetField("theme",BindingFlags.Instance|BindingFlags.NonPublic);
            var applyTheme=typeof(Workspace).GetMethod("ApplyTheme",BindingFlags.Instance|BindingFlags.NonPublic);
            foreach(string mode in new[]{"暗色","亮色","暗色"}){
                modeField.SetValue(f,mode);
                applyTheme.Invoke(f,null);
                foreach(var box in Walk(f).OfType<RichTextBox>()){
                    Check(box.BackColor.ToArgb()==rows.BackColor.ToArgb(),"comparison uses theme surface");
                    bool commonText=false,colorsMatch=true;
                    for(int k=0;k<box.TextLength;k++){
                        box.Select(k,1);
                        if(box.SelectionBackColor.ToArgb()==Color.FromArgb(255,229,154).ToArgb()){
                            colorsMatch &= box.SelectionColor.ToArgb()==Color.FromArgb(40,35,25).ToArgb();
                        }else{
                            commonText=true;
                            colorsMatch &= box.SelectionColor.ToArgb()==box.ForeColor.ToArgb();
                        }
                    }
                    Check(commonText && colorsMatch,"readable comparison after theme switch");
                }
            }
            rows.Items[adopted].Checked=true;
            var copy=Path.Combine(folder,"confirmed.docx");
            Check(f.WriteCopy(copy,false,false).GetAwaiter().GetResult()==1,"export one author-confirmed change");
            var reopened=new Manuscript(copy);
            Check(Enumerable.Range(0,manuscript.Items.Count).All(i=>reopened.Items[i].Text==(i==adopted?manuscript.Items[i].Suggested:manuscript.Items[i].Text)),"unconfirmed suggestions excluded on reopen");
            if(args.Contains("--screenshot")){
                var theme=typeof(Workspace).GetField("theme",BindingFlags.Instance|BindingFlags.NonPublic);
                foreach(string mode in new[]{"亮色","暗色"}){
                    theme.SetValue(f,mode);typeof(Workspace).GetMethod("ApplyTheme",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,null);
                    var panel=f.Controls[0];using(var im=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(im,panel.ClientRectangle);im.Save(mode=="亮色"?"assets/workspace.png":"build/workspace-dark.png");}
                }
            }
            string report=Path.Combine(folder,"测试.html");
            Task job=f.WriteReport(src,report,null,false);Pump(()=>job.IsCompleted);job.GetAwaiter().GetResult();
            Check(File.ReadAllText(report).Contains("覆盖统计"),"actual desktop report");
            var same=f.WriteReport(src,report,null,false);Pump(()=>same.IsCompleted);Check(same.IsFaulted,"existing report refused");
            string canceled=Path.Combine(folder,"cancel.html");var stop=f.WriteReport(src,canceled,null,false);f.CancelReport();Pump(()=>stop.IsCompleted);stop.GetAwaiter().GetResult();
            Check(!File.Exists(canceled) && !Directory.EnumerateDirectories(folder,".techpaper-*").Any(),"cancel cleanup");
            foreach(string ext in new[]{".pdf",".docx",".pptx",".png",".xyz"}){
                string broken=Path.Combine(folder,"bad"+ext);File.WriteAllText(broken,"invalid");
                bool failed=false;try{Workspace.ValidateFile(broken);}catch{failed=true;}Check(failed,"invalid "+ext);
            }
        }finally{f.Close();foreach(string path in Directory.EnumerateFiles(folder))File.Delete(path);Directory.Delete(folder);}
    }
}
