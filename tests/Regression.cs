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
    static void CheckPrivacy(string source, Type manuscriptType) {
        var w=Manuscript.W;
        string folder=Path.Combine("build","privacy-"+Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string input=Path.Combine(folder,"source.docx");
        var parts=Read(source);
        Func<string,XElement> hidden=s=>new XElement(w+"r",new XElement(w+"rPr",new XElement(w+"vanish")),new XElement(w+"t",s));
        var body=Manuscript.Parse(parts["word/document.xml"]);
        body.Descendants(w+"ins").First().Add(hidden("hidden revision"));
        body.Descendants(w+"p").First().Add(hidden("hidden ordinary"));
        var comments=Manuscript.Parse(parts["word/comments.xml"]);
        comments.Descendants(w+"p").First().Add(hidden("hidden comment"));
        var types=Manuscript.Parse(parts["[Content_Types].xml"]);
        types.Root.Add(new XElement(types.Root.Name.Namespace+"Override",new XAttribute("PartName","/docProps/custom.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.custom-properties+xml")));
        var rels=Manuscript.Parse(parts["_rels/.rels"]);
        rels.Root.Add(new XElement(rels.Root.Name.Namespace+"Relationship",new XAttribute("Id","rPrivateFixture"),new XAttribute("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties"),new XAttribute("Target","docProps/custom.xml")));
        var utf8=System.Text.Encoding.UTF8;
        var header=Manuscript.Parse(parts["word/header1.xml"]);
        var hp=header.Descendants(w+"p").First();
        Func<string,XElement> fieldMark=kind=>new XElement(w+"r",new XElement(w+"fldChar",new XAttribute(w+"fldCharType",kind)));
        Func<string,XElement> instruction=value=>new XElement(w+"r",new XElement(w+"instrText",value));
        hp.Add(new XElement(w+"fldSimple",new XAttribute(w+"instr"," FILENAME \\p "),
                   new XElement(w+"r",new XElement(w+"t","SYNTHETIC_PATH"))),
               fieldMark("begin"),instruction(" laSt"),instruction("SavedBy "),fieldMark("separate"),
               new XElement(w+"r",new XElement(w+"t","SYNTHETIC_EDITOR")),fieldMark("end"),
               new XElement(w+"fldSimple",new XAttribute(w+"instr","PAGE")));
        var footer=Manuscript.Parse(parts["word/footer1.xml"]);
        footer.Descendants(w+"p").First().Add(fieldMark("begin"),instruction("IF "),fieldMark("begin"),
               instruction("AUTHOR"),fieldMark("separate"),fieldMark("end"),fieldMark("separate"),fieldMark("end"));
        parts["word/header1.xml"]=utf8.GetBytes(header.ToString());
        parts["word/footer1.xml"]=utf8.GetBytes(footer.ToString());
        parts["word/document.xml"]=utf8.GetBytes(body.ToString());
        parts["word/comments.xml"]=utf8.GetBytes(comments.ToString());
        parts["[Content_Types].xml"]=utf8.GetBytes(types.ToString());
        parts["_rels/.rels"]=utf8.GetBytes(rels.ToString());
        parts["docProps/custom.xml"]=utf8.GetBytes("<Properties xmlns='http://schemas.openxmlformats.org/officeDocument/2006/custom-properties'><property name='FixtureOwner' pid='2' fmtid='{D5CDD505-2E9C-101B-9397-08002B2CF9AE}'><s:lpwstr xmlns:s='http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes'>Synthetic editor</s:lpwstr></property></Properties>");
        try {
            using(var z=ZipFile.Open(input,ZipArchiveMode.Create))
                foreach(var part in parts) using(var stream=z.CreateEntry(part.Key).Open())stream.Write(part.Value,0,part.Value.Length);
            var original=File.ReadAllBytes(input);
            var flags=System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var open=manuscriptType.GetConstructor(flags,null,new[]{typeof(string)},null);
            var doc=open.Invoke(new object[]{input});
            foreach(bool extra in new[]{false,true}){
                string preview=(string)manuscriptType.GetMethod("PrivacyPreview",flags).Invoke(doc,new object[]{extra});
                Check(preview.Contains("FixtureOwner") && preview.Contains("直接隐藏的纯文本片段：1；") && preview.Contains(extra?"将删除批注":"保留批注"),"privacy preview matches selected mode");
                Check(preview.Contains("word/header1.xml · 2") && preview.Contains("word/footer1.xml · 1") &&
                    !preview.Contains("SYNTHETIC_EDITOR") && !preview.Contains("SYNTHETIC_PATH"),
                    "preview counts retained identity fields without displaying cached values");
                string output=Path.Combine(folder,extra?"extra.docx":"default.docx");
                manuscriptType.GetMethod("Export",flags).Invoke(doc,new object[]{output,new HashSet<int>(),true,extra});
                var result=Read(output);
                Check(result["word/header1.xml"].SequenceEqual(parts["word/header1.xml"]) &&
                    result["word/footer1.xml"].SequenceEqual(parts["word/footer1.xml"]),
                    "identity field codes, cached results and unrelated page number preserved");
                Check(!result.ContainsKey("docProps/custom.xml") && !utf8.GetString(result["_rels/.rels"]).Contains("docProps/custom.xml") && !utf8.GetString(result["[Content_Types].xml"]).Contains("docProps/custom.xml"),"custom property and package references removed");
                var revised=Manuscript.Parse(result["word/document.xml"]);
                Check(revised.Descendants(w+"ins").Any(n=>n.Value.Contains("hidden revision")) && !revised.Root.Value.Contains("hidden ordinary"),"hidden revision retained and ordinary hidden text removed");
                Check(extra?!result.ContainsKey("word/comments.xml"):result["word/comments.xml"].SequenceEqual(parts["word/comments.xml"]),"comment part matches preview");
                open.Invoke(new object[]{output});
            }
            Check(original.SequenceEqual(File.ReadAllBytes(input)),"privacy source intact");
        } finally {
            foreach(string file in Directory.GetFiles(folder))File.Delete(file);
            Directory.Delete(folder);
        }
    }
    static void CheckRunBoundaries(string source){
        var w=Manuscript.W;
        string input=Path.Combine("build",Guid.NewGuid()+".docx"),output=input+".copy.docx";
        var parts=Read(source);
        var xml=Manuscript.Parse(parts["word/document.xml"]);
        var body=xml.Root.Element(w+"body");
        Func<string,string,XElement> paragraph=(left,right)=>new XElement(w+"p",
            new XElement(w+"r",new XElement(w+"t",left)),
            new XElement(w+"r",new XElement(w+"rPr",new XElement(w+"i")),new XElement(w+"t",right)));
        body.AddFirst(paragraph("我们认为","值得注意的是，样本量有限。"),
                      paragraph("We repeated the test ","in order to measure drift."));
        parts["word/document.xml"]=System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        try{
            using(var zip=ZipFile.Open(input,ZipArchiveMode.Create))
                foreach(var part in parts)using(var stream=zip.CreateEntry(part.Key).Open())
                    stream.Write(part.Value,0,part.Value.Length);
            var doc=new Manuscript(input);
            Check(doc.Items[0].Suggested==doc.Items[0].Text,"formatting split is not a new sentence opening");
            Check(doc.Items[1].Suggested=="We repeated the test to measure drift.","safe multi-run rule edit");
            doc.Export(output,new HashSet<int>{0,1},false);
            var actual=Manuscript.Parse(Read(output)["word/document.xml"]);
            body.Elements(w+"p").Skip(1).First().Descendants(w+"t").Last().Value="to measure drift.";
            Check(XNode.DeepEquals(xml.Root,actual.Root),"only confirmed text changes in multi-run roundtrip");
        }finally{File.Delete(input);File.Delete(output);}
    }
    static void Main(string[] args) {
        if(args.Length==3 && args[0]=="--installed-privacy"){
            var assembly=System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[1]));
            CheckPrivacy(args[2],assembly.GetType("TechPaper.Manuscript",true));
            Console.WriteLine("PASS installed privacy preview and both exports");
            return;
        }
        if(args.Length==3 && args[0]=="--office-math"){
            var before=File.ReadAllBytes(args[1]);
            var manuscript=new Manuscript(args[1]);
            var edit=manuscript.Items.Single(p=>p.Editable);
            Check(edit.Suggested=="We repeated the test to estimate drift.","expected edit outside Office Math");
            manuscript.Export(args[2],new HashSet<int>{manuscript.Items.IndexOf(edit)},false);
            var expected=Manuscript.Parse(Read(args[1])["word/document.xml"]);
            expected.Descendants(Manuscript.W+"t").First().Value=edit.Suggested;
            var actual=Manuscript.Parse(Read(args[2])["word/document.xml"]);
            Check(XNode.DeepEquals(expected.Root,actual.Root),"all formula and revision XML retained after edit");
            Check(new Manuscript(args[2]).Items.Any(p=>p.Text==edit.Suggested),"reopened edited Office Math fixture");
            Check(before.SequenceEqual(File.ReadAllBytes(args[1])),"Office Math source intact");
            return;
        }
        if(args.Length==2){
            var longDoc=new Manuscript(args[0]);longDoc.Export(args[1],new HashSet<int>(),false);
            var copy=new Manuscript(args[1]);
            Check(longDoc.Items.Select(v=>v.Text).SequenceEqual(copy.Items.Select(v=>v.Text)),"long DOCX round-trip");
            return;
        }
        string source="tests/sample.docx", path=Path.Combine("build",Guid.NewGuid()+".docx");
        var original=File.ReadAllBytes(source);var a=Read(source);var doc=new Manuscript(source);var w=Manuscript.W;
        CheckPrivacy(source,typeof(Manuscript));
        CheckRunBoundaries(source);
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
                new[]{"需要检验滤波器是否能够分辨两组信号。","需检验滤波器能否分辨两组信号。"},
                new[]{"We examined whether or not the estimates differed.","We examined whether the estimates differed."},
                new[]{"本研究提出三项待验证的假设。","本研究提出三项待检验的假设。"},
                new[]{"测量重复了二次。","测量重复了两次。"},
                new[]{"记录了传感器温度的变化。","记录了传感器的温度变化。"},
                new[]{"两处探头的温度差为 2 °C。","两处探头的温差为 2 °C。"},
                new[]{"We measured the change in temperature.","We measured the temperature change."},
                new[]{"The relative displacement was 3 mm.","The measured relative displacement was 3 mm."},
                new[]{"仅纳入浓度 ≥ 5 nM 的样本。","仅纳入浓度 >= 5 nM 的样本。"},
                new[]{"Samples with p != 0.05 were retained.","We retained samples with p ≠ 0.05."},
                new[]{"差异与温度有关。","差异与温度相关。"},
                new[]{"The shift was associated with temperature.","The shift was related to temperature."},
                new[]{"Temperature caused the shift.","Temperature led to the shift."},
                new[]{"在研究的过程中，我们在照射前检测了材料甲的吸光度。","在研究中，我们在照射前检测了材料甲的吸光度。"},
                new[]{"由于仪器漂移，我们排除了两次重复测量。","因仪器漂移，我们排除了两次重复测量。"},
                new[]{"We repeated the measurement in order to check the 5 nM standard.","We repeated the measurement to check the 5 nM standard."},
                new[]{"The two runs could explain the shift after calibration.","Both runs could explain the shift after calibration."}
            }) {
                Services.ValidateRevision(safe[0], safe[1]);
                Check(true,"scientific clarity edit allowed");
            }
            Check(Services.LocalModelName("local-editor")=="local-editor:latest:local","explicit local source and default tag");
            Check(Services.LocalModelName("registry.example:5000/team/editor")=="registry.example:5000/team/editor:latest:local","registry port is not a model tag");
            Check(Services.LocalModelName(" editor:2b:local ")=="editor:2b:local","preserve local selector and tag");
            foreach(string name in new[]{"x:cloud","x:8b-cloud","x:Cloud:local","x:local:local","",":local"}){
                bool invalidModel=false;
                try{Services.LocalModelName(name);}catch(InvalidOperationException){invalidModel=true;}
                Check(invalidModel,"reject empty or nonlocal selector");
            }
            var changesOfMeaning = new[] {
                new[]{"研究将检验两组读数是否一致。","研究发现两组读数一致。"},
                new[]{"研究将检验两组读数是否一致。","研究将检验两组读数是否能够一致。"},
                new[]{"这项试验考察滤波器能否分辨信号。","这项试验表明滤波器能分辨信号。"},
                new[]{"We examined whether vibration affected drift.","We found that vibration affected drift."},
                new[]{"本研究据此提出三项研究假设。","本研究据此提出两项研究假设。"},
                new[]{"本研究将从两种条件下取样。","本研究将从多种条件下取样。"},
                new[]{"在测量的过程中，我们对传感器温度进行了记录。","我们在测量过程中记录了传感器的温度变化。"},
                new[]{"探头温度为 2 °C。","探头温差为 2 °C。"},
                new[]{"相对位移为 -1.25 mm。","绝对位移为 -1.25 mm。"},
                new[]{"We recorded the temperature.","We recorded the change in temperature."},
                new[]{"The temperature difference was 2 °C.","The temperature change was 2 °C."},
                new[]{"Relative displacement was 3 mm.","Absolute displacement was 3 mm."},
                new[]{"H1（采样假设）：低照度条件可能增加读数偏差。","G1（采样假设）：低照度条件可能增加读数偏差。"},
                new[]{"校准结束后再次核对了传感器安装位置。","校准结束后再次核对了传感器安装位置。校准结束后再次核对了传感器安装位置。"},
                new[]{"纳入条件为 p < 0.05[1]。","纳入条件为 p <= 0.05[1]。"},
                new[]{"The cutoff was p = 0.05 [1].","The cutoff was p ≠ 0.05 [1]."},
                new[]{"至少 3 次重复[2]。","至多 3 次重复[2]。"},
                new[]{"The sample contained at most 5 mL.","The sample contained at least 5 mL."},
                new[]{"我们重复了测量，以便能够判断差异是否与温度有关。现有数据不足以排除抽样误差。","为检验观测差异是否受温度影响，我们进行了重复测量；然而，现有数据尚不足以排除抽样误差的干扰。"},
                new[]{"差异与温度有关。","差异由温度导致。"},
                new[]{"Temperature was associated with drift.","Temperature caused drift."},
                new[]{"Drift was caused by temperature.","Drift was linked to temperature."},
                new[]{"X caused drift [1]. Y correlated with noise [2].","X correlated with drift [1]. Y caused noise [2]."},
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
            const string intro="本次测试包含两个不同的测量步骤：";
            var pasted=Manuscript.FromPastedText(intro+"\r\n\r\nH1（温度）：在研究的过程中，我们记录了室温[2]。 H2（时间）：在相同的条件下记录了响应时间。");
            Check(pasted.Items.Count==3 && pasted.Items[0].Text==intro && pasted.Items[1].Text.EndsWith("[2]。 ") &&
                pasted.Items[2].Text.StartsWith("H2") && !pasted.Items[1].CanChange(pasted.Items[1].Nodes.Single(n=>n.Value=="[2]")),
                "paste preserves hypothesis boundaries, spacing and citations");
            Check(Manuscript.SplitPastedText("在 H1 和 H2 条件下测试。读数为 -1.25 mm。").Length==1,"ordinary prose stays one paragraph");
            var pastedFile=Path.Combine("build",Guid.NewGuid()+".docx");
            try{
                pasted.Export(pastedFile,new HashSet<int>{1},false);
                Check(new Manuscript(pastedFile).Items.Select(p=>p.Text).SequenceEqual(new[]{intro,pasted.Items[1].Suggested,pasted.Items[2].Text}),"pasted DOCX reopens with only selected edits");
            }finally{File.Delete(pastedFile);}
            bool bad=false;try{Services.ValidateRevision("偏移为 -2 mm","偏移为 2 cm");}catch(InvalidDataException){bad=true;}Check(bad,"signed numbers and unit protection");
            Check(Services.ReadScore(json).Contains("fixture"),"GPTZero response fixture");
        }finally{if(File.Exists(path))File.Delete(path);}
    }
}
