// Run on Windows with the installed executable and its shipped synthetic sample.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

class InstalledPolish {
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    sealed class Fixture : IWebRequestCreate {
        internal Uri Address;
        public WebRequest Create(Uri uri){return WebRequest.CreateDefault(new Uri(Address,uri.AbsolutePath));}
    }
    static void Require(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}
    static void Wait(Func<bool> done){
        var timer=Stopwatch.StartNew();
        while(!done()){Application.DoEvents();Thread.Sleep(8);if(timer.Elapsed.TotalSeconds>20)throw new TimeoutException();}
    }
    static HttpListenerContext Receive(HttpListener server,string path){
        var task=server.GetContextAsync();Wait(()=>task.IsCompleted);
        Require(task.Result.Request.Url.AbsolutePath==path,"unexpected request");return task.Result;
    }
    static void Reply(HttpListenerContext request,object data){
        var bytes=Encoding.UTF8.GetBytes(Json.Serialize(data));
        request.Response.ContentLength64=bytes.Length;request.Response.Close(bytes,false);
    }
    static XDocument Body(ZipArchive zip){
        using(var stream=zip.GetEntry("word/document.xml").Open())return XDocument.Load(stream,LoadOptions.PreserveWhitespace);
    }
    static void CheckSplitThreshold(Form form,HttpListener server,string sample){
        string input=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".docx");
        const string left="The protocol requires at ",right="least 5 mL of reference solution.";
        try{
            File.Copy(sample,input);
            using(var zip=ZipFile.Open(input,ZipArchiveMode.Update)){
                var body=Body(zip);var w=body.Root.Name.Namespace;
                body.Root.Element(w+"body").AddFirst(new XElement(w+"p",
                    new XElement(w+"r",new XElement(w+"t",new XAttribute(XNamespace.Xml+"space","preserve"),left)),
                    new XElement(w+"r",new XElement(w+"rPr",new XElement(w+"i")),new XElement(w+"t",right))));
                zip.GetEntry("word/document.xml").Delete();
                using(var stream=zip.CreateEntry("word/document.xml").Open())body.Save(stream);
            }
            var type=form.GetType();
            type.GetMethod("LoadFile",Fields).Invoke(form,new object[]{input});
            var rows=(ListView)type.GetField("rows",Fields).GetValue(form);
            Wait(()=>rows.Items.Count>0 && rows.Items[0].SubItems[4].Text.Contains(left.Trim()));
            var task=(Task)type.GetMethod("PolishBatch",Fields).Invoke(form,new object[]{new[]{0},"fixture"});
            Reply(Receive(server,"/api/show"),new{model_info=new{fixture=true}});
            Reply(Receive(server,"/api/chat"),new{message=new{content=Json.Serialize(new{run0=left,run1=right.Replace("least","most")})}});
            Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            Require(rows.Items[0].SubItems[3].Text=="模型改动已拦截" && !rows.Items[0].Checked,
                "installed workspace accepted a reversed split threshold");
            Console.WriteLine("PASS installed workspace rejects split threshold");
            task=(Task)type.GetMethod("PolishBatch",Fields).Invoke(form,new object[]{new[]{0},"fixture"});
            Reply(Receive(server,"/api/show"),new{model_info=new{fixture=true}});
            Reply(Receive(server,"/api/chat"),new{message=new{content=Json.Serialize(new{run0=left,run1=right.Replace("reference solution","solution exhibiting a temperature change")})}});
            Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            Require(rows.ShowItemToolTips && rows.Items[0].ToolTipText.Contains("温度变化") && !rows.Items[0].Checked,
                "workspace must explain the rejected measurement change");
        }finally{if(File.Exists(input))File.Delete(input);}
    }
    [STAThread]static void Main(string[] args){
        // Keep the same WinForms synchronization context as the installed application
        // across successive asynchronous imports and reviewer actions.
        EventHandler run=null;
        Exception failure=null;
        run=(sender,e)=>{
            Application.Idle-=run;
            try{CheckWorkspace(args);}catch(Exception ex){failure=ex;}
            finally{Application.ExitThread();}
        };
        Application.Idle+=run;
        Application.Run();
        if(failure!=null)throw new InvalidOperationException("Installed workspace check failed",failure);
    }
    static void CheckWorkspace(string[] args){
        string input=Path.GetFullPath(args[1]);
        string output=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".docx");
        var source=File.ReadAllBytes(input);
        var type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("TechPaper.Workspace",true);
        var statics=BindingFlags.Static|BindingFlags.NonPublic;
        using(var left=new RichTextBox())using(var right=new RichTextBox()){
            string middle=String.Concat(Enumerable.Range(0,320).Select(i=>"测点 "+i+"，保留记录。"));
            bool coarse=(bool)type.GetMethod("ShowDifference",statics).Invoke(null,new object[]{left,right,"试验开始。"+middle+"原始说明。","实验重新开始。"+middle+"说明。"});
            Require(!coarse,"unexpected coarse diff");
            foreach(var box in new[]{left,right}){
                box.Select(box.Text.IndexOf("160"),3);
                Require(box.SelectionBackColor!=System.Drawing.Color.FromArgb(255,229,154),"unchanged middle highlighted");
            }
            right.Select(right.Text.IndexOf("重新"),2);
            Require(right.SelectionBackColor==System.Drawing.Color.FromArgb(255,229,154),"missing inserted text highlight");
        }
        var check=type.Assembly.GetType("TechPaper.Services").GetMethod("ValidateRevision",statics);
        foreach(var example in new[]{
            new[]{"该文报告了支座在潮湿环境下的变形特征[7]。","该文通过反复试验报告了支座在潮湿环境下的变形特征[7]。","研究方法","重复试验"},
            new[]{"At the bench, the observer logged the readings and checked the fixture.","At the bench, the observer logged the readings and checked the fixture. At the bench the observer logged the readings, and checked the fixture.","重复输出","输出了两遍"}}){
            bool refused=false;
            try{check.Invoke(null,new object[]{example[0],example[1]});}
            catch(TargetInvocationException ex){refused=ex.InnerException is InvalidDataException && ex.InnerException.Message.Contains(example[3]);}
            Require(refused,example[2]);
        }
        Console.WriteLine("PASS installed long diff and guards for unsupported methods and doubled output");
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();
        var address=new Uri("http://127.0.0.1:"+((IPEndPoint)reserve.LocalEndpoint).Port+"/");reserve.Stop();
        Require(WebRequest.RegisterPrefix("http://127.0.0.1:11434/api/",new Fixture{Address=address}),"fixture registration");
        using(var server=new HttpListener())
        using(var form=(Form)Activator.CreateInstance(type,true)){
            server.Prefixes.Add(address.AbsoluteUri);server.Start();
            form.StartPosition=FormStartPosition.Manual;form.Left=-6000;form.Show();
            type.GetMethod("LoadFile",Fields).Invoke(form,new object[]{input});
            var rows=(ListView)type.GetField("rows",Fields).GetValue(form);
            Wait(()=>rows.Items.Count>0);
            int cited=rows.Items.Cast<ListViewItem>().Single(r=>r.SubItems[4].Text.Contains("[7]")).Index;
            int confirmed=rows.Items.Cast<ListViewItem>().First(r=>r.Index!=cited && r.SubItems[3].Text=="建议精简").Index;
            rows.Items[confirmed].Checked=true;
            var eligible=(int[])type.GetMethod("PolishIndices",Fields).Invoke(form,new object[]{true});
            Require(!eligible.Contains(confirmed) && eligible.Contains(cited),"selection changed");
            var start=type.GetMethod("PolishBatch",Fields);
            var stop=type.GetMethod("StopPolishing",Fields);
            var batch=(Task)start.Invoke(form,new object[]{new[]{cited},"fixture"});
            Reply(Receive(server,"/api/show"),new{model_info=new{fixture=true}});
            var request=Receive(server,"/api/chat");
            Dictionary<string,object> payload;
            using(var reader=new StreamReader(request.Request.InputStream))payload=Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
            var messages=(System.Collections.IList)payload["messages"];
            var context=Json.Deserialize<Dictionary<string,object>[]>((string)((Dictionary<string,object>)messages[1])["content"]);
            Require(context.Any(r=>(string)r["text"]=="[7]" && !(bool)r["editable"]),"missing read-only citation");
            var edits=context.Where(r=>(bool)r["editable"]).ToDictionary(r=>(string)r["id"],r=>(string)r["text"]);
            string key=edits.Keys.Single(k=>edits[k].StartsWith("值得注意的是，"));
            string original=edits[key];
            edits[key]=original.Substring("值得注意的是，".Length);
            Reply(request,new{message=new{content=Json.Serialize(edits)}});
            Wait(()=>batch.IsCompleted);batch.GetAwaiter().GetResult();
            Require(rows.Items[cited].SubItems[3].Text=="模型建议" && !rows.Items[cited].Checked,"suggestion missing");
            var drifted=new Dictionary<string,string>(edits);
            drifted[key]="仪器已经就位。"+edits[key];
            batch=(Task)start.Invoke(form,new object[]{new[]{cited},"fixture"});
            Reply(Receive(server,"/api/show"),new{model_info=new{fixture=true}});
            Reply(Receive(server,"/api/chat"),new{message=new{content=Json.Serialize(drifted)}});
            Wait(()=>batch.IsCompleted);batch.GetAwaiter().GetResult();
            Require(rows.Items[cited].SubItems[3].Text=="保留先前建议" && rows.Items[cited].ToolTipText.Contains("引用所在"),
                "workspace did not preserve a previous suggestion on citation drift");
            Console.WriteLine("PASS installed citation scope warning preserves earlier suggestion");
            batch=(Task)start.Invoke(form,new object[]{new[]{cited},"fixture"});
            Reply(Receive(server,"/api/show"),new{model_info=new{fixture=true}});
            Receive(server,"/api/chat");
            var watch=Stopwatch.StartNew();stop.Invoke(form,null);
            Wait(()=>batch.IsCompleted);batch.GetAwaiter().GetResult();
            Require(watch.Elapsed.TotalSeconds<5 && rows.Items[confirmed].Checked,"cancel lost confirmation or timed out");
            rows.Items[confirmed].Checked=false;rows.Items[cited].Checked=true;
            try{
                var export=(Task<int>)type.GetMethod("WriteCopy",Fields).Invoke(form,new object[]{output,false,false});
                Wait(()=>export.IsCompleted);Require(export.Result==1,"edit count");
                using(var old=ZipFile.OpenRead(input))using(var copy=ZipFile.OpenRead(output)){
                    var expected=Body(old);var actual=Body(copy);
                    expected.Descendants().Single(n=>n.Name.LocalName=="t" && n.Value==original).Value=edits[key];
                    Require(XNode.DeepEquals(expected.Root,actual.Root),"citation or structure changed");
                }
                Require(source.SequenceEqual(File.ReadAllBytes(input)),"source changed");
            }finally{if(File.Exists(output))File.Delete(output);}
            CheckSplitThreshold(form,server,input);
            using(var timer=new System.Windows.Forms.Timer{Interval=50}){
                timer.Tick+=(s,e)=>{
                    var dlg=Application.OpenForms.Cast<Form>().SingleOrDefault(d=>d.Text=="粘贴文本并自动分段");
                    if(dlg==null)return;
                    timer.Stop();
                    dlg.Controls.OfType<TextBox>().Single().Text="提出以下假设： H1（温度）：在研究的过程中，我们记录了室温。[2] H2（时间）：在相同的条件下记录响应时间。";
                    var ok=dlg.Controls.OfType<Button>().Single();
                    Require(ok.Text=="导入 3 段","paste count");
                    ok.PerformClick();
                };
                timer.Start();type.GetMethod("PasteParagraphs",Fields).Invoke(form,null);
                Wait(()=>rows.Items.Count==3 && rows.Items[1].SubItems[4].Text.StartsWith("H1"));
            }
            Require(rows.CheckedIndices.Count==0,"pasted paragraphs already selected");
            rows.Items[1].Checked=true;
            try{
                var pasted=(Task<int>)type.GetMethod("WriteCopy",Fields).Invoke(form,new object[]{output,false,false});
                Wait(()=>pasted.IsCompleted);Require(pasted.Result==1,"paste adoption count");
                using(var zip=ZipFile.OpenRead(output)){
                    var body=Body(zip);var w=body.Root.Name.Namespace;
                    var paragraphs=body.Descendants(w+"p").Select(p=>p.Value).ToArray();
                    Require(paragraphs.SequenceEqual(new[]{"提出以下假设： ","H1（温度）：在研究中，我们记录了室温。[2] ","H2（时间）：在相同的条件下记录响应时间。"}),"paste export mismatch");
                }
            }finally{if(File.Exists(output))File.Delete(output);}
            Console.WriteLine("PASS installed paste dialog, automatic segmentation and selective export");
        }
        Console.WriteLine("PASS installed paragraph context, stop, confirmed selection and citation roundtrip");
    }
}
