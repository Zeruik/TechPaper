using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TechPaper {
internal sealed class Passage {
    internal XElement Paragraph;
    internal string Text, Suggested, Reason;
    internal bool Editable;
    internal string[] Revisions;
    internal readonly HashSet<XElement> FieldText = new HashSet<XElement>();
    internal void SetFragment(int index,string value){
        var nodes=Nodes;
        if(index<0 || index>=nodes.Length || !CanChange(nodes[index]))throw new InvalidOperationException("该片段受结构保护。");
        Services.ValidateRevision(nodes[index].Value,value);
        var revised=(string[])Revisions.Clone();
        revised[index]=value;
        string suggested=String.Concat(revised);
        if(nodes.Length>1)Services.ValidateRevision(Text,suggested);
        ValidateInlineReferences(revised);
        Revisions=revised;Suggested=suggested;
    }
    internal void ValidateInlineReferences(string[] revised){
        var w=Manuscript.W;
        var original=new StringBuilder();var proposed=new StringBuilder();
        int index=0;bool hasAnchor=false;
        foreach(var node in Paragraph.Descendants()){
            bool isText=node.Name==w+"t";
            if(!isText && node.Name!=w+"footnoteReference" && node.Name!=w+"endnoteReference")continue;
            bool anchor=!isText || FieldText.Contains(node) ||
                (Regex.IsMatch(node.Value,@"^\s*[0-9]+\s*$") &&
                 node.Parent.Elements(w+"rPr").Elements(w+"vertAlign").Any(a=>(string)a.Attribute(w+"val")=="superscript"));
            if(anchor){original.Append("[0]");proposed.Append("[0]");hasAnchor=true;}
            else{original.Append(node.Value);proposed.Append(revised[index]);}
            if(isText)index++;
        }
        if(hasAnchor)Services.ValidateCitationScope(original.ToString(),proposed.ToString());
    }
    internal XElement[] Nodes { get { return Paragraph.Descendants(Manuscript.W+"t").ToArray(); } }
    internal bool CanChange(XElement n) {
        var r=n.Parent;
        return Editable && !FieldText.Contains(n) && r!=null && r.Name==Manuscript.W+"r" && !r.Descendants(Manuscript.W+"vertAlign").Any() &&
               !Regex.IsMatch(n.Value,@"[\[［]\s*\d|【|DOI\s*:",RegexOptions.IgnoreCase);
    }
}

internal static class Editor {
    internal static readonly string[,] Rules = {
        {"值得注意的是，", ""}, {"需要指出的是，", ""}, {"众所周知，", ""},
        {"在此基础之上", "在此基础上"}, {"在实际应用的过程中", "在实际应用中"},
        {"在研究的过程中", "在研究中"}, {"在相同的条件下", "在相同条件下"},
        {"在此情况下", "此时"}, {"所得到的结果", "结果"},
        {"所需要的时间", "所需时间"}, {"在随后的分析过程中", "在后续分析中"},
        {"在测量的过程中", "测量时"}, {"以便能够", "以便"}, {"对其进行了分析", "对其作了分析"},
        {"由此可以看出，", "由此可见，"},
        {"It is worth noting that ", ""}, {"in order to ", "to "},
        {"due to the fact that ", "because "}, {"at the present time", "currently"}
    };
    internal static string Revise(string s) {
        foreach (int i in Enumerable.Range(0, Rules.GetLength(0))) {
            string from = Rules[i,0], to = Rules[i,1];
            if (from == "It is worth noting that ") {
                if (s.StartsWith(from, StringComparison.Ordinal) && s.Length > from.Length)
                    s = Char.ToUpperInvariant(s[from.Length]) + s.Substring(from.Length + 1);
            } else if (to.Length==0) {
                // Remove complete sentence openings; preserve phrases embedded in a claim.
                s=Regex.Replace(s,@"(^|[。！？]\s*)"+Regex.Escape(from),"$1");
            } else if (from[0] < 128)
                s = Regex.Replace(s, @"\b" + Regex.Escape(from), to);
            else s = s.Replace(from,to);
        }
        // Keep the measured quantity verbatim. Punctuation, citations and extra clauses stop the match.
        s=Regex.Replace(s,@"对([\p{IsCJKUnifiedIdeographs}A-Za-z0-9℃°%·²³μ]{2,18})进行了(测量|记录|统计|比较)",m=>
            m.Groups[2].Value+"了"+m.Groups[1].Value);
        s=Regex.Replace(s,@"可以(?=通过|根据|采用|实现|降低|得到|观察|使用|提高|作为|获得|避免)","可");
        return s;
    }
    internal static string Signals(string s) {
        var signals = new List<string>();
        if (Revise(s) != s) signals.Add("可精简措辞");
        var sentences = Regex.Split(s, @"[。！？.!?]+").Where(p=>p.Trim().Length>0).ToArray();
        if (sentences.Length >= 4) {
            double avg = sentences.Average(p=>(double)p.Length);
            if (avg > 12 && Math.Sqrt(sentences.Average(p=>Math.Pow(p.Length-avg,2)))/avg < 0.15)
                signals.Add("句长接近");
            if (sentences.GroupBy(p=>p.Trim().Substring(0,Math.Min(4,p.Trim().Length))).Any(g=>g.Count()>=3))
                signals.Add("句首重复");
        }
        return signals.Count==0 ? "—" : String.Join(" · ", signals);
    }
}

internal sealed class Manuscript {
    internal static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    readonly Dictionary<string,byte[]> parts = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
    readonly XDocument body;
    internal readonly List<Passage> Items = new List<Passage>();
    internal readonly string Source;
    internal static XDocument Parse(byte[] bytes) {
        using(var ms=new MemoryStream(bytes))
        using(var r=XmlReader.Create(ms,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=64*1024*1024 }))
            return XDocument.Load(r,LoadOptions.PreserveWhitespace);
    }
    static byte[] Pack(XDocument doc) {
        using(var m=new MemoryStream()){ doc.Save(m,SaveOptions.DisableFormatting); return m.ToArray(); }
    }
    internal Manuscript(string path) {
        Source=Path.GetFullPath(path);
        using(var archive=ZipFile.OpenRead(Source)) {
            if(archive.Entries.Count>12000 || archive.Entries.Sum(e=>e.Length)>160L*1024*1024)
                throw new InvalidDataException("文档解压后超过 160 MB 或条目过多。");
            foreach(var e in archive.Entries) {
                if(e.FullName.StartsWith("_xmlsignatures/",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("当前版本不修改带数字签名的文档。");
                using(var s=e.Open()) using(var m=new MemoryStream()){s.CopyTo(m);parts.Add(e.FullName,m.ToArray());}
            }
        }
        if(!parts.ContainsKey("word/document.xml")) throw new InvalidDataException("请选择标准 Word DOCX 文件。");
        body=Parse(parts["word/document.xml"]);
        if(body.Root.Name!=W+"document") throw new InvalidDataException("请在 Word 中另存为常规 DOCX 后再导入。");
        ReadParagraphs();
    }
    const string pastedCitation=@"[\[［【]\s*\d+(?:\s*[,，、;；\-–—]\s*\d+)*\s*[\]］】]";
    static readonly Regex hypothesisStart=new Regex(
        @"(?:^|(?<intro>[:：])|[。！？；;.!?](?:[ \t]*"+pastedCitation+@")*)[ \t]*(?<marker>H(?<number>[1-9][0-9]{0,2}))(?:(?<label>[ \t]*[（(:：、])|[ \t]+)");
    internal static string[] SplitPastedText(string text){
        var result=new List<string>();
        foreach(string line in Regex.Split(text,@"\r\n|[\r\n\u2028\u2029]")){
            if(String.IsNullOrWhiteSpace(line))continue;
            var markers=hypothesisStart.Matches(line).Cast<Match>().ToArray();
            if(markers.Length<2){result.Add(line);continue;}
            bool intro=markers[0].Groups["intro"].Success;
            bool labelled=markers[0].Groups["label"].Success;
            if((markers[0].Index!=0 && !intro) || (intro && !labelled)){
                result.Add(line);continue;
            }
            markers=markers.Where((m,i)=>(i==0 || !m.Groups["intro"].Success) &&
                (!labelled || m.Groups["label"].Success)).ToArray();
            if(markers.Length<2 || markers.Where((m,i)=>Int32.Parse(m.Groups["number"].Value)!=i+1).Any()){
                result.Add(line);continue;
            }
            if(intro)result.Add(line.Substring(0,markers[0].Groups["marker"].Index));
            for(int i=0;i<markers.Length;i++){
                int start=i==0 && !intro?0:markers[i].Groups["marker"].Index;
                int end=i+1<markers.Length?markers[i+1].Groups["marker"].Index:line.Length;
                result.Add(line.Substring(start,end-start));
            }
        }
        return result.ToArray();
    }
    internal static Manuscript FromPastedText(string text){
        var lines=SplitPastedText(text);
        if(lines.Length==0)throw new InvalidDataException("请输入至少一段正文。");
        return new Manuscript(lines);
    }
    private Manuscript(string[] lines){
        const string citations="("+pastedCitation+")";
        body=new XDocument(new XElement(W+"document",new XAttribute(XNamespace.Xmlns+"w",W.NamespaceName),new XElement(W+"body",
            lines.Select(line=>new XElement(W+"p",Regex.Split(line,citations).Where(t=>t.Length>0)
                .Select(t=>new XElement(W+"r",new XElement(W+"t",new XAttribute(XNamespace.Xml+"space","preserve"),t))))))));
        parts["word/document.xml"]=Pack(body);
        parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes("<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        parts["_rels/.rels"]=Encoding.UTF8.GetBytes("<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
        ReadParagraphs();
    }
    void ReadParagraphs(){
        var styles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(parts.ContainsKey("word/styles.xml")){
            var definitions=Parse(parts["word/styles.xml"]).Descendants(W+"style").ToList();
            foreach(var st in definitions){
                string name=(string)st.Elements(W+"name").Attributes(W+"val").FirstOrDefault() ?? "";
                if(st.Descendants(W+"outlineLvl").Any() || st.Descendants(W+"numPr").Any() ||
                   Regex.IsMatch(name,@"heading|title|caption|bibliography|标题|题注",RegexOptions.IgnoreCase))
                    styles.Add((string)st.Attribute(W+"styleId") ?? "");
            }
            bool added;
            do{added=false;foreach(var st in definitions){
                var parent=(string)st.Elements(W+"basedOn").Attributes(W+"val").FirstOrDefault();
                if(parent!=null && styles.Contains(parent))added|=styles.Add((string)st.Attribute(W+"styleId") ?? "");
            }}while(added);
        }
        int fieldDepth=0;
        bool references=false;
        foreach(var p in body.Root.Element(W+"body").Elements(W+"p")) {
            bool unsafeField=fieldDepth>0;
            var fieldText=new HashSet<XElement>();
            bool hasField=false;
            foreach(var part in p.Descendants()){
                if(part.Name==W+"fldChar"){
                    hasField=true;var type=(string)part.Attribute(W+"fldCharType");
                    if(type=="begin")fieldDepth++;
                    else if(type=="end"){if(fieldDepth==0)unsafeField=true;fieldDepth=Math.Max(0,fieldDepth-1);}
                    else if(type!="separate" || fieldDepth==0)unsafeField=true;
                }else if(part.Name==W+"t" && fieldDepth>0)fieldText.Add(part);
            }
            var instructions=p.Descendants(W+"instrText").Select(n=>n.Value.Trim()).ToArray();
            unsafeField|=fieldDepth!=0 || hasField && (instructions.Length!=1 || !Regex.IsMatch(instructions[0],@"^REF\s",RegexOptions.IgnoreCase));
            string t=String.Concat(p.Descendants(W+"t").Where(n=>!n.Ancestors(W+"del").Any()).Select(n=>n.Value));
            if(String.IsNullOrWhiteSpace(t)) continue;
            if(Regex.IsMatch(t.Trim(),@"^(参考文献|references|bibliography)\s*[:：]?$",RegexOptions.IgnoreCase)) references=true;
            var pr=p.Element(W+"pPr");
            bool special=pr!=null && (pr.Element(W+"numPr")!=null || pr.Element(W+"outlineLvl")!=null ||
                styles.Contains((string)pr.Elements(W+"pStyle").Attributes(W+"val").FirstOrDefault() ?? ""));
            bool plain=p.Elements().All(n=>n.Name==W+"r" || n.Name==W+"pPr") &&
                p.Elements(W+"r").All(r=>r.Elements().All(n=>n.Name==W+"rPr" || n.Name==W+"t" || n.Name==W+"footnoteReference" || n.Name==W+"endnoteReference" || n.Name==W+"instrText" || n.Name==W+"fldChar")) &&
                !p.Descendants(W+"vanish").Any() &&
                !Regex.IsMatch(t, "[“”「」『』\"《》]");
            var item=new Passage{Paragraph=p,Text=t,Editable=!references && !unsafeField && !special && plain && !p.Descendants(W+"pPrChange").Any() && !p.Descendants(W+"rPrChange").Any() && t.Length>=16,Reason=Editor.Signals(t)};
            item.FieldText.UnionWith(fieldText);
            item.Revisions=item.Nodes.Select(n=>item.CanChange(n)?Editor.Revise(n.Value):n.Value).ToArray();
            item.Suggested=item.Editable ? String.Concat(item.Revisions) : t;
            if(item.Revisions.Length>1 && item.Suggested!=t && item.Suggested!=Editor.Revise(t)){
                // Formatting can split a word or clause. Keep the paragraph when
                // applying rules per run disagrees with the continuous text.
                item.Revisions=item.Nodes.Select(n=>n.Value).ToArray();
                item.Suggested=t;
            }
            Items.Add(item);
        }
    }
    static bool HiddenTextRun(XElement node) {
        if(node.Name!=W+"r" || !node.Elements(W+"t").Any() ||
           node.Elements().Any(n=>n.Name!=W+"rPr" && n.Name!=W+"t")) return false;
        return node.Elements(W+"rPr").Elements(W+"vanish").Any(n=>
            !new[]{"false","0","off"}.Contains((string)n.Attribute(W+"val"),StringComparer.OrdinalIgnoreCase));
    }
    static bool PersonalPart(string name,bool comments) {
        return (!name.StartsWith("word/",StringComparison.OrdinalIgnoreCase) || comments) && Regex.IsMatch(name,@"^(word/(comments[^/]*|people|person)\.xml|docProps/(custom\.xml|thumbnail\.[^/]+))$",RegexOptions.IgnoreCase);
    }
    static bool RemovableHiddenRun(XElement node) {
        return HiddenTextRun(node) && !node.Ancestors().Any(a=>a.Name==W+"ins" || a.Name==W+"del" ||
            a.Name==W+"moveFrom" || a.Name==W+"moveTo");
    }
    static IEnumerable<XElement> ExtendedProperties(XDocument doc) {
        return doc.Root.Elements().Where(n=>new[]{"Company","Manager","Template","HyperlinkBase","TotalTime","TitlesOfParts","HeadingPairs"}.Contains(n.Name.LocalName));
    }
    static string RelTarget(string relFile,string target) {
        string owner=relFile=="_rels/.rels" ? "" : relFile.Replace("/_rels/","/").Substring(0,relFile.Replace("/_rels/","/").Length-5);
        return Uri.UnescapeDataString(new Uri(new Uri("http://local/"+owner),target).AbsolutePath.TrimStart('/'));
    }
    string privacyDetails;
    static readonly Regex identityField=new Regex(
        @"^\s*(?:AUTHOR|LASTSAVEDBY|USERNAME|USERINITIALS|USERADDRESS|FILENAME|DOCPROPERTY)(?=\s|\\|$)",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    static int CountIdentityFields(XDocument part) {
        int count=0;
        var codes=new Stack<StringBuilder>();
        foreach(var node in part.Descendants()){
            if(node.Name==W+"fldSimple"){
                if(identityField.IsMatch((string)node.Attribute(W+"instr")??""))count++;
            }else if(node.Name==W+"fldChar"){
                string kind=(string)node.Attribute(W+"fldCharType");
                if(kind=="begin")codes.Push(new StringBuilder());
                else if(codes.Count>0 && (kind=="end" || kind=="separate")){
                    var code=codes.Pop();
                    if(code!=null && identityField.IsMatch(code.ToString()))count++;
                    if(kind=="separate")codes.Push(null); // Cached result follows.
                }
            }else if(node.Name==W+"instrText" && codes.Count>0 && codes.Peek()!=null){
                codes.Peek().Append(node.Value);
            }
        }
        return count;
    }
    string GetPrivacyDetails() {
        string[] props={"docProps/core.xml","docProps/app.xml","docProps/custom.xml"};
        var rows=new List<string>();
        foreach(string key in props) if(parts.ContainsKey(key)){
            var properties=Parse(parts[key]);
            var nodes=key=="docProps/app.xml"?ExtendedProperties(properties):properties.Root.Elements();
            var names=nodes.Select(n=>(string)n.Attribute("name")??n.Name.LocalName).ToArray();
            if(names.Length>0)rows.Add(key+" · "+String.Join(", ",names));
        }
        int hidden=0;
        var retained=new List<string>();
        foreach(var part in parts.Where(k=>k.Key.StartsWith("word/") && k.Key.EndsWith(".xml") && !PersonalPart(k.Key,true))){
            var xml=Parse(part.Value);
            hidden+=xml.Descendants().Count(RemovableHiddenRun);
            int count=CountIdentityFields(xml);
            if(count>0)retained.Add(part.Key+" · "+count);
        }
        rows.Add("直接隐藏的纯文本片段："+hidden+"；缩略图："+parts.Keys.Count(k=>k.StartsWith("docProps/thumbnail.")));
        if(retained.Count>0){
            rows.Add("以下位置含姓名、文件名或文档属性域。清理会保留域结构，已显示的姓名或路径可能仍在副本中，请在分享前核对：");
            rows.AddRange(retained);
        }
        return String.Join("\r\n",rows);
    }
    internal string PrivacyPreview(bool comments=false) {
        if(privacyDetails==null)privacyDetails=GetPrivacyDetails();
        int reviewParts=parts.Keys.Count(k=>k.StartsWith("word/") && PersonalPart(k,true));
        return privacyDetails+"\r\n"+(comments ? "将删除批注及人员部件："+reviewParts+"；移除批注标记并匿名化审阅者信息，保留修订正文。" :
            "保留批注及人员部件："+reviewParts+"。保留修订记录及其中的隐藏文字。");
    }
    internal int Export(string target,HashSet<int> selected,bool clean,bool comments=false) {
        target=Path.GetFullPath(target);
        if(File.Exists(target) || String.Equals(target,Source,StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择新的文件名，原稿和已有文件会保留。");
        var clone=new XDocument(body);
        var edits=Items.Select((item,i)=>new {item,i}).Where(v=>selected.Contains(v.i) && v.item.Editable && v.item.Suggested!=v.item.Text)
            .ToDictionary(v=>v.item.Paragraph,v=>v.item);
        foreach(var pair in body.Root.Element(W+"body").Elements(W+"p").Zip(clone.Root.Element(W+"body").Elements(W+"p"),(a,b)=>new {a,b})) {
            if(!edits.ContainsKey(pair.a)) continue;
            int index=0;
            foreach(var node in pair.b.Descendants(W+"t")) {
                string revised=pair.b.Descendants(W+"t").Take(2).Count()==1 ? edits[pair.a].Suggested : edits[pair.a].Revisions[index];
                index++;
                if(revised!=node.Value){node.Value=revised;if(revised.Trim()!=revised)node.SetAttributeValue(XNamespace.Xml+"space","preserve");}
            }
        }
        string tmp=Path.Combine(Path.GetDirectoryName(target),".techpaper-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            using(var z=ZipFile.Open(tmp,ZipArchiveMode.Create)) foreach(var entry in parts) {
                string name=entry.Key;
                if(clean && (PersonalPart(name,comments) || comments && Regex.IsMatch(name,@"^word/_rels/(comments[^/]*|people|person)\.xml\.rels$",RegexOptions.IgnoreCase))) continue;
                XDocument x=name=="word/document.xml" && edits.Count>0 ? clone : null;
                bool changed=x!=null;
                if(clean && (name.EndsWith(".xml",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels"))) {
                    if(x==null)x=name=="word/document.xml"?clone:Parse(entry.Value);
                    IEnumerable<XElement> remove=Enumerable.Empty<XElement>();
                    if(name=="docProps/core.xml") remove=x.Root.Elements();
                    else if(name=="docProps/app.xml") remove=ExtendedProperties(x);
                    else if(name=="[Content_Types].xml") remove=x.Root.Elements().Where(n=>PersonalPart(((string)n.Attribute("PartName")??"").TrimStart('/'),comments));
                    else if(name.EndsWith(".rels")) remove=x.Root.Elements().Where(n=>(string)n.Attribute("TargetMode")!="External" && PersonalPart(RelTarget(name,(string)n.Attribute("Target")),comments));
                    else if(name.StartsWith("word/") && !PersonalPart(name,true)) remove=x.Descendants().Where(n=>comments && (n.Name==W+"commentRangeStart" || n.Name==W+"commentRangeEnd" || n.Name==W+"commentReference") || RemovableHiddenRun(n));
                    var doomed=remove.ToList();changed|=doomed.Count>0;doomed.Remove();
                    if(comments && name.StartsWith("word/")) foreach(var a in x.Descendants().Attributes().Where(a=>a.Name.Namespace==W && (a.Name.LocalName=="author" || a.Name.LocalName=="initials" || a.Name.LocalName=="date")).ToList()) {
                        if(a.Name.LocalName=="author") a.Value="Anonymous"; else a.Remove();changed=true;
                    }
                }
                var output=z.CreateEntry(name);output.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
                byte[] data=changed?Pack(x):entry.Value;
                using(var stream=output.Open()) stream.Write(data,0,data.Length);
            }
            using(var check=ZipFile.OpenRead(tmp)) {
                var entry=check.GetEntry("word/document.xml");
                using(var stream=entry.Open()) {
                    var reopened=XDocument.Load(stream,LoadOptions.PreserveWhitespace);
                    if (!XNode.DeepEquals(reopened.Root,clone.Root))
                        throw new InvalidDataException("复核副本时发现正文、格式或引用结构与预期不一致。");
                }
            }
            File.Move(tmp,target);
            return edits.Count;
        } finally {if(File.Exists(tmp))File.Delete(tmp);}
    }
}
}
