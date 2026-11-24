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
            foreach(var pair in new[] {
                new[]{"值得注意的是，采样周期为 5 s。","采样周期为 5 s。"},
                new[]{"先校零。需要指出的是，误差可能增大。","先校零。误差可能增大。"},
                new[]{"在研究的过程中，我们记录了 12 个读数。","在研究中，我们记录了 12 个读数。"},
                new[]{"我们对两个序列进行了比较。","我们比较了两个序列。"},
                new[]{"It is worth noting that the peak may shift.","The peak may shift."},
                new[]{"We repeated the test in order to estimate drift.","We repeated the test to estimate drift."},
                new[]{"更值得注意的是，误差可能与方向有关。","更值得注意的是，误差可能与方向有关。"},
                new[]{"我们认为需要指出的是，样本量有限。","我们认为需要指出的是，样本量有限。"},
                new[]{"The instrument cannot resolve peaks below 5 Hz.","The instrument cannot resolve peaks below 5 Hz."},
                new[]{"这里只讨论 1 s、5 s 和 10 s 的采样结果[1]。","这里只讨论 1 s、5 s 和 10 s 的采样结果[1]。"}}) {
                var result=Editor.Revise(pair[0]);
                Check(result==pair[1],"modern editorial fixture");
                Services.ValidateRevision(pair[0],result);
            }
            foreach(var example in new[] {
                new[]{"实验组为 22，对照组为 25[1]。","对照组为 22，实验组为 25[1]。"},
                new[]{"Group A was lower than group B [1].","Group B was lower than group A [1]."},
                new[]{"甲组高于乙组[1]。","甲组低于乙组[1]。"},
                new[]{"差异未达到显著水平。","差异达到显著水平。"},
                new[]{"结果提示存在差异[1]。","结果证实存在差异[1]。"},
                new[]{"采样间隔为 5 s。","采样间隔为 5 h。"},
                new[]{"剂量为 1 g。","剂量为 1 mg。"},
                new[]{"峰值可能变化。","峰值变化。"},
                new[]{"模型不能确定真实频率。","模型能确定真实频率。"},
                new[]{"观察值见[1]。","观察值见1。"}}) {
                bool rejected=false;
                try {Services.ValidateRevision(example[0],example[1]);} catch(InvalidDataException){rejected=true;}
                Check(rejected,"protected scientific detail: "+example[0]);
            }
            foreach (var safe in new[] {
                new[]{"在研究的过程中，我们在照射前检测了材料甲的吸光度。","在研究中，我们在照射前检测了材料甲的吸光度。"},
                new[]{"由于仪器漂移，我们排除了两次重复测量。","因仪器漂移，我们排除了两次重复测量。"},
                new[]{"We repeated the measurement in order to check the 5 nM standard.","We repeated the measurement to check the 5 nM standard."},
                new[]{"The two runs could explain the shift after calibration.","Both runs could explain the shift after calibration."}
            }) {
                Services.ValidateRevision(safe[0], safe[1]);
                Check(true,"scientific clarity edit allowed");
            }
            var changesOfMeaning = new[] {
                new[]{"The signal rose after exposure [2].","The signal fell after exposure [2]."},
                new[]{"The signal increased before exposure [2].","The signal increased after exposure [2]."},
                new[]{"The samples were excluded from the analysis.","The samples were included in the analysis."},
                new[]{"The mean exceeded the reference value.","The mean approached the reference value."},
                new[]{"The calibration could explain the difference.","The calibration explains the difference."},
                new[]{"We cannot rule out a change in the signal.","We cannot establish a change in the signal."},
                new[]{"A concentration of 5 nM was used.","A concentration of 5 mM was used."},
                new[]{"我们在照射前测量了吸光度[2]。","我们在照射后测量了吸光度[2]。"},
                new[]{"材料甲的均值高于材料乙。","材料乙的均值高于材料甲。"},
                new[]{"该指标与温度呈正向关联。","该指标与温度呈反向关联。"},
                new[]{"样本符合纳入标准。","样本符合排除标准。"},
                new[]{"结果支持该假设。","结果推翻该假设。"}
            };
            int blocked = 0;
            foreach (var pair in changesOfMeaning) {
                try { Services.ValidateRevision(pair[0], pair[1]); }
                catch (InvalidDataException) { blocked++; }
            }
            Console.WriteLine("Scientific counterexamples blocked: "+blocked+"/"+changesOfMeaning.Length);
            Check(blocked==changesOfMeaning.Length, "new scientific counterexamples blocked: "+blocked+"/"+changesOfMeaning.Length);
            Services.ValidateRevision("我们对 5 s 序列的峰值进行了比较[1]。","我们比较了 5 s 序列的峰值[1]。");
            bool bad=false;try{Services.ValidateRevision("偏移为 -2 mm","偏移为 2 cm");}catch(InvalidDataException){bad=true;}Check(bad,"signed numbers and unit protection");
            Check(Services.ReadScore(json).Contains("fixture"),"GPTZero response fixture");
        }finally{if(File.Exists(path))File.Delete(path);}
    }
}
