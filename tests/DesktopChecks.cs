using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using System.Web.Script.Serialization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TechPaper;
class DesktopChecks {
    sealed class LocalFixture : IWebRequestCreate {
        public Uri Address;
        public WebRequest Create(Uri uri){return WebRequest.CreateDefault(new Uri(Address,uri.AbsolutePath));}
    }
    static void CheckParagraphContext(HttpListener server){
        var w=Manuscript.W;
        const string left=" We repeated the reading in order to estimate drift";
        const string right=". The reference remained below the limit.";
        var passage=new Passage{Editable=true,Text=left+"[7]"+right,Paragraph=new XElement(w+"p",
            new XElement(w+"r",new XElement(w+"t",left)),
            new XElement(w+"r",new XElement(w+"rPr",new XElement(w+"vertAlign",new XAttribute(w+"val","superscript"))),new XElement(w+"t","[7]")),
            new XElement(w+"r",new XElement(w+"t",right))),Revisions=new[]{left,"[7]",right}};
        var json=new JavaScriptSerializer();
        foreach(string mode in new[]{"valid","punctuation","protected","missing","splitClaim"}){
            var task=Task.Run(()=>Services.PolishParagraph(passage,"fixture","",CancellationToken.None));
            var request=server.GetContextAsync();Pump(()=>request.IsCompleted);
            var ctx=request.Result;
            string body;
            using(var reader=new StreamReader(ctx.Request.InputStream))body=reader.ReadToEnd();
            var payload=json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(body);
            Check(body.Contains("[7]") && json.Serialize(payload["format"]).Contains("run2") &&
                !json.Serialize(payload["format"]).Contains("run1"),"read-only citation in paragraph context");
            string revised=left.Trim().Replace("in order to","to");
            var values=new System.Collections.Generic.Dictionary<string,string>{{"run0",revised},{"run2",right}};
            if(mode=="punctuation"){values["run0"]+=".";values["run2"]=right.Substring(2);}
            if(mode=="protected")values["run1"]="[8]";
            if(mode=="missing")values.Remove("run2");
            if(mode=="splitClaim")values["run0"]="We repeated the reading. We then estimated drift";
            var answer=Encoding.UTF8.GetBytes(json.Serialize(new{message=new{content=json.Serialize(values)}}));
            ctx.Response.ContentLength64=answer.Length;ctx.Response.Close(answer,false);
            Pump(()=>task.IsCompleted);
            if(mode=="valid")Check(task.Result.SequenceEqual(new[]{" "+revised,"[7]",right}),"apply context edit preserving whitespace and citation");
            else Check(task.IsFaulted && task.Exception.GetBaseException() is InvalidDataException,"reject malformed or displaced citation context: "+mode);
            Check(passage.Revisions.SequenceEqual(new[]{left,"[7]",right}),"paragraph remains unchanged until validation completes");
        }
        const string prefix="The protocol requires at ";
        const string suffix="least 5 mL of the reference solution.";
        var split=new Passage{Editable=true,Text=prefix+suffix,Suggested=prefix+suffix,
            Paragraph=new XElement(w+"p",new XElement(w+"r",new XElement(w+"t",prefix)),
                new XElement(w+"r",new XElement(w+"t",suffix))),Revisions=new[]{prefix,suffix}};
        var changed=suffix.Replace("least","most");
        var pending=Task.Run(()=>Services.PolishParagraph(split,"fixture","",CancellationToken.None));
        var next=server.GetContextAsync();Pump(()=>next.IsCompleted);
        var reply=Encoding.UTF8.GetBytes(json.Serialize(new{message=new{content=json.Serialize(new{run0=prefix,run1=changed})}}));
        next.Result.Response.ContentLength64=reply.Length;next.Result.Response.Close(reply,false);
        Pump(()=>pending.IsCompleted);
        Check(pending.IsFaulted && pending.Exception.GetBaseException() is InvalidDataException,
            "model cannot reverse a threshold split across Word runs");
        bool rejected=false;
        try{split.SetFragment(1,changed);}catch(InvalidDataException){rejected=true;}
        Check(rejected && split.Revisions.SequenceEqual(new[]{prefix,suffix}) && split.Suggested==split.Text,
            "manual split-threshold rejection preserves preview");
        split.SetFragment(1,suffix.Replace("of the reference solution","of reference solution"));
        Check(split.Suggested.Contains("at least 5 mL of reference solution"),"safe edit beside split threshold");
    }
    static void CheckModelStop(Workspace form,ListView rows,Manuscript manuscript,int adopted){
        var reserve=new TcpListener(IPAddress.Loopback,0);
        reserve.Start();
        var address=new Uri("http://127.0.0.1:"+((IPEndPoint)reserve.LocalEndpoint).Port+"/");
        reserve.Stop();
        using(var server=new HttpListener()){
            server.Prefixes.Add(address.AbsoluteUri);
            server.Start();
            Check(WebRequest.RegisterPrefix("http://127.0.0.1:11434/api/",new LocalFixture{Address=address}),"test-only local model fixture");
            CheckParagraphContext(server);
            rows.Items[adopted].Checked=true;
            var indices=form.PolishIndices(true);
            Check(indices.Length>0 && !indices.Contains(adopted),"keep confirmed paragraph outside batch");
            var saved=manuscript.Items.Select(p=>p.Suggested).ToArray();
            var batch=form.PolishBatch(indices,"synthetic-model");
            var show=server.GetContextAsync();Pump(()=>show.IsCompleted);
            Check(show.Result.Request.Url.AbsolutePath=="/api/show","metadata before text");
            var response=Encoding.UTF8.GetBytes("{\"model_info\":{\"fixture\":true}}");
            show.Result.Response.ContentLength64=response.Length;
            show.Result.Response.Close(response,false);
            var chat=server.GetContextAsync();Pump(()=>chat.IsCompleted);
            Check(chat.Result.Request.Url.AbsolutePath=="/api/chat","synthetic generation pending");
            var watch=Stopwatch.StartNew();
            form.StopPolishing();
            Pump(()=>batch.IsCompleted);batch.GetAwaiter().GetResult();
            Check(watch.Elapsed.TotalSeconds<5,"stop without waiting for model reply");
            Check(rows.Items[adopted].Checked && saved.SequenceEqual(manuscript.Items.Select(p=>p.Suggested)),"cancel preserves prior suggestions and selection");
            rows.Items[adopted].Checked=false;
        }
    }
    static void Check(bool result,string name){if(!result)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Pump(Func<bool> done){var t=Stopwatch.StartNew();while(!done()){Application.DoEvents();Thread.Sleep(10);if(t.Elapsed.TotalSeconds>90)throw new TimeoutException();}}
    static System.Collections.Generic.IEnumerable<Control> Walk(Control c){foreach(Control child in c.Controls){yield return child;foreach(var item in Walk(child))yield return item;}}
    [STAThread]static void Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        EventHandler run=null;
        Exception failure=null;
        run=(s,e)=>{
            Application.Idle-=run;
            try{CheckWorkspace(args);}catch(Exception ex){failure=ex;}
            finally{Application.ExitThread();}
        };
        Application.Idle+=run;
        Application.Run();
        if(failure!=null)throw new InvalidOperationException("Workspace checks failed",failure);
    }
    static void CheckWorkspace(string[] args) {
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
            CheckModelStop(f,rows,manuscript,adopted);
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
            using(var enter=new System.Windows.Forms.Timer{Interval=50}){
                enter.Tick+=(s,e)=>{
                    var dlg=Application.OpenForms.Cast<Form>().SingleOrDefault(v=>v.Text=="粘贴文本并自动分段");
                    if(dlg==null)return;
                    enter.Stop();
                    Walk(dlg).OfType<TextBox>().Single().Text="H1（温度）：在研究的过程中，我们记录了室温[2]。 H2（时间）：在相同的条件下记录响应时间。";
                    var button=Walk(dlg).OfType<Button>().Single();
                    Check(button.Enabled && button.Text=="导入 2 段","paste dialog previews paragraph count");
                    button.PerformClick();
                };
                enter.Start();
                typeof(Workspace).GetMethod("PasteParagraphs",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,null);
                Pump(()=>rows.Items.Count==2 && rows.Items[0].SubItems[4].Text.StartsWith("H1"));
            }
            Check(rows.CheckedItems.Count==0,"pasted suggestions require author selection");
            var pastedReport=Path.Combine(folder,"pasted.html");
            var review=f.WriteReport(null,pastedReport,null,false);Pump(()=>review.IsCompleted);review.GetAwaiter().GetResult();
            var markup=File.ReadAllText(pastedReport);
            Check(markup.Contains("在研究的过程中") && markup.Contains("[2]") && markup.Contains("H2") &&
                !Directory.EnumerateDirectories(folder,".techpaper-*").Any(),"paste exports original highlights and removes temporary input");
            foreach(string ext in new[]{".pdf",".docx",".pptx",".png",".xyz"}){
                string broken=Path.Combine(folder,"bad"+ext);File.WriteAllText(broken,"invalid");
                bool failed=false;try{Workspace.ValidateFile(broken);}catch{failed=true;}Check(failed,"invalid "+ext);
            }
        }finally{f.Close();foreach(string path in Directory.EnumerateFiles(folder))File.Delete(path);Directory.Delete(folder);}
    }
}
