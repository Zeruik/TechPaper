using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Web.Script.Serialization;
using TechPaper;

class Regression {
    static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);Console.WriteLine("PASS "+msg);}
    static Dictionary<string,byte[]> Read(string path) {
        using(var z=ZipFile.OpenRead(path))return z.Entries.ToDictionary(e=>e.FullName,e=>{using(var s=e.Open())using(var m=new MemoryStream()){s.CopyTo(m);return m.ToArray();}});
    }
    static void Main(string[] args) {
        if(args.Length==2){
            var longDoc=new Manuscript(args[0]);longDoc.Export(args[1],new HashSet<int>(),false);
            var copy=new Manuscript(args[1]);
            Check(longDoc.Items.Select(v=>v.Text).SequenceEqual(copy.Items.Select(v=>v.Text)),"long DOCX round-trip");
            return;
        }
        string source="tests/sample.docx", path=Path.Combine("build",Guid.NewGuid()+".docx");
        var original=File.ReadAllBytes(source);var a=Read(source);var doc=new Manuscript(source);var w=Manuscript.W;
        try {
            var ids=new HashSet<int>(Enumerable.Range(0,doc.Items.Count));
            Check(doc.Items.Count(p=>p.Editable && p.Suggested!=p.Text)==3,"three proposed edits");
            var local=doc.Items.Single(p=>p.Text.StartsWith("在实际应用的过程中"));
            local.Suggested="两组传感器分别布置在门口和房间中央。";
            doc.Export(path,ids,true,true);
            var b=Read(path);var xml=Manuscript.Parse(b["word/document.xml"]);var orig=Manuscript.Parse(a["word/document.xml"]);
            Check(xml.ToString().Contains(local.Suggested),"accepted model preview exported");
            Check(xml.ToString().Contains("在研究中，我们每隔 15 分钟") && xml.ToString().Contains("值得注意的是，此段"),"rules and references");
            Check(xml.Descendants(w+"t").Any(t=>t.Value=="[7]" && t.Parent.Descendants(w+"vertAlign").Any()),"citation superscript anchor");
            foreach(string tag in new[]{"pPr","rPr","numPr","spacing","tbl","sectPr"})
                Check(orig.Descendants(w+tag).Select(n=>n.ToString()).SequenceEqual(xml.Descendants(w+tag).Select(n=>n.ToString())),"layout "+tag);
            foreach(string name in new[]{"word/styles.xml","word/numbering.xml","word/header1.xml","word/footer1.xml"})
                Check(a[name].SequenceEqual(b[name]),name);
            Check(!b.ContainsKey("word/comments.xml") && !b.ContainsKey("docProps/thumbnail.jpeg") &&
                !Manuscript.Parse(b["word/_rels/document.xml.rels"]).ToString().Contains("comments.xml") &&
                !Manuscript.Parse(b["[Content_Types].xml"]).ToString().Contains("comments.xml") &&
                !Manuscript.Parse(b["docProps/core.xml"]).Root.Elements().Any(),"privacy and relationships");
            Check(!xml.Descendants().Any(n=>n.Name.LocalName.StartsWith("comment")),"comment anchors");
            Check(new Manuscript(path).Items.Count==doc.Items.Count,"reopen export");
            bool refused=false;try{doc.Export(path,ids,true,true);}catch(IOException){refused=true;}Check(refused,"refuse overwrite");
            File.Delete(path);doc.Export(path,ids,true);
            var retained=Read(path);Check(retained.ContainsKey("word/comments.xml") && Manuscript.Parse(retained["word/document.xml"]).Descendants(w+"ins").Any(n=>(string)n.Attribute(w+"author")=="Fixture Editor"),"default preserves reviews");
            Check(a["word/media/image1.png"].SequenceEqual(retained["word/media/image1.png"]),"image retained");
            File.Delete(path);doc.Export(path,new HashSet<int>(),false);var plain=Read(path);
            Check(a.All(k=>plain[k.Key].SequenceEqual(k.Value)),"no-edit part fidelity");
            Check(original.SequenceEqual(File.ReadAllBytes(source)),"source intact");
            var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>("{\"documents\":[{\"predicted_class\":\"mixed\",\"class_probabilities\":{\"ai\":0.1,\"mixed\":0.8,\"human\":0.1}}],\"version\":\"fixture\"}");
            foreach(var example in new[] {
                new[]{"采样间隔为 5 s。","采样间隔为 5 h。"},
                new[]{"剂量为 1 g。","剂量为 1 mg。"},
                new[]{"峰值可能变化。","峰值变化。"},
                new[]{"模型不能确定真实频率。","模型能确定真实频率。"},
                new[]{"观察值见[1]。","观察值见1。"}}) {
                bool rejected=false;
                try {Services.ValidateRevision(example[0],example[1]);} catch(InvalidDataException){rejected=true;}
                Check(rejected,"protected scientific detail: "+example[0]);
            }
            Services.ValidateRevision("我们对 5 s 序列的峰值进行了比较[1]。","我们比较了 5 s 序列的峰值[1]。");
            bool bad=false;try{Services.ValidateRevision("偏移为 -2 mm","偏移为 2 cm");}catch(InvalidDataException){bad=true;}Check(bad,"signed numbers and unit protection");
            Check(Services.ReadScore(json).Contains("fixture"),"GPTZero response fixture");
        }finally{if(File.Exists(path))File.Delete(path);}
    }
}
