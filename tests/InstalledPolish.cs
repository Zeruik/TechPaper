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
    [STAThread]static void Main(string[] args){
        string input=Path.GetFullPath(args[1]);
        string output=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".docx");
        var source=File.ReadAllBytes(input);
        var type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("TechPaper.Workspace",true);
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
        }
        Console.WriteLine("PASS installed paragraph context, stop, confirmed selection and citation roundtrip");
    }
}
