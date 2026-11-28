using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;

namespace TechPaper {
internal static class Services {
    internal const string EditorialPolicy=
        "你是学术论文编辑。用原文的语言和现代学术语体，交付作者可以逐句核对的修订。\n"+
        "先辨认段落的任务，再决定是否需要改：研究动机保留问题与缺口；假设保留编号、条件和预测；方法保留对象、变量、对照与步骤；结果保留比较双方、观测值和方向；讨论区分观测、解释和局限。\n"+
        "改写时先找清主语、动作和对象，把限定语放回对应主张附近。删去没有信息的开场和重复表达，能直接说的动作就用动词。长句可在同一段内分句，并列事项保持原顺序。必要时保留专业术语的重复；无需为了换词而改写清楚的句子。\n"+
        "编号、工况代号、人名、仪器名、引文及其对应主张保持原样。保留正负号、小数、百分号、单位、参照系，以及先后、否定、可能性和适用条件。不要把相关改成因果、把假设改成发现，或把相对位移说成绝对位移。\n"+
        "维持原有叙述人称和证据强度。不得补写采集数据、重复实验、显著性或实际用途。简短的引导句维持简短。不得增添总结或重复输出段落。原文已清楚时原样返回。写结果段时，把每个读数连同对应工况一起核对，例如加载和卸载时的两次位移。作者的偏好仅用于措辞。";
    internal static readonly string[] PolicyChoices={"标准","扩展","扩展（双引文示例）"};
    internal static string SelectedPolicy(int choice){
        if(choice!=1 && choice!=2)return EditorialPolicy;
        string policy=ReadPolicyBlock(0);
        return choice==1?policy:policy+"\n"+ReadPolicyBlock(1);
    }
    static string ReadPolicyBlock(int i){
        using(var stream=typeof(Services).Assembly.GetManifestResourceStream("TechPaper.Editorial"+i))
        using(var reader=new StreamReader(stream))return reader.ReadToEnd().Replace("\r\n","\n");
    }
    static Dictionary<string,object> Post(string url,object payload,string key,CancellationToken cancel=default(CancellationToken)) {
        var json=new JavaScriptSerializer {MaxJsonLength=16*1024*1024};
        var req=(HttpWebRequest)WebRequest.Create(url);
        req.Method="POST"; req.ContentType="application/json"; req.Timeout=120000; req.ReadWriteTimeout=120000;
        req.AllowAutoRedirect=false;
        if(key!=null)req.Headers.Add("x-api-key",key); else req.Proxy=null;
        byte[] bytes=Encoding.UTF8.GetBytes(json.Serialize(payload));
        using(cancel.Register(req.Abort)){
            try{
                cancel.ThrowIfCancellationRequested();
                using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
                using(var response=req.GetResponse())using(var r=new StreamReader(response.GetResponseStream()))
                    return json.Deserialize<Dictionary<string,object>>(r.ReadToEnd());
            }catch(WebException ex){
                cancel.ThrowIfCancellationRequested();
                if(url!="http://127.0.0.1:11434/api/chat")throw;
                var response=ex.Response as HttpWebResponse;
                using(response){
                    if(response==null){
                        string reason=ex.Status==WebExceptionStatus.Timeout?"本机模型响应超时。":"与本机 Ollama 的连接中断。";
                        throw new InvalidOperationException(reason+"请检查模型运行状态后重试。",ex);
                    }
                    // Service diagnostics may contain local paths or input. Display only known categories.
                    using(var reader=new StreamReader(response.GetResponseStream())){
                        var buffer=new char[4096];
                        string details=new string(buffer,0,reader.ReadBlock(buffer,0,buffer.Length));
                        if(Regex.IsMatch(details,@"out of memory|requires more system memory|insufficient (?:system |GPU |video )?memory",RegexOptions.IgnoreCase))
                            throw new InvalidOperationException("本机模型报告内存或显存不足。请释放内存或选择较小的已安装模型后重试。",ex);
                    }
                    throw new InvalidOperationException("本机 Ollama 推理失败（HTTP "+(int)response.StatusCode+"）。请检查模型运行状态。",ex);
                }
            }catch(Exception){cancel.ThrowIfCancellationRequested();throw;}
        }
    }
    internal static string Scan(string text,string key) {
        var response=Post("https://api.gptzero.me/v2/predict/text",new {document=text},key);
        return ReadScore(response);
    }
    internal static string ReadScore(Dictionary<string,object> response) {
        var doc=(Dictionary<string,object>)((System.Collections.IList)response["documents"])[0];
        var scores=(Dictionary<string,object>)doc["class_probabilities"];
        return String.Format("模型版本  {0}\r\n预测类别  {1}\r\n\r\n人工写作  {2:P1}\r\nAI 生成  {3:P1}\r\n混合写作  {4:P1}\r\n\r\n数值为整篇分类的概率，不能换算为 AI 写作字数占比。",
            response.ContainsKey("version")?response["version"]:"服务未提供",doc["predicted_class"],Convert.ToDouble(scores["human"]),Convert.ToDouble(scores["ai"]),Convert.ToDouble(scores["mixed"]));
    }
    const string citation = @"[\[［【]\s*\d+(?:\s*[,，、;；\-–—]\s*\d+)*\s*[\]］】]";
    static readonly Regex citationEvents=new Regex("(?<ref>"+citation+@")|[。！？!?；;]+|\.(?=\s|$|[\[［【])");
    static IEnumerable<int> CitationClauses(string text){
        int clause=0,firstContent=0;
        while(firstContent<text.Length && Char.IsWhiteSpace(text[firstContent]))firstContent++;
        foreach(Match item in citationEvents.Matches(text)){
            if(item.Groups["ref"].Success){
                yield return clause*2+(firstContent==item.Index?1:0);
            }else{
                clause++;firstContent=item.Index+item.Length;
                while(firstContent<text.Length && Char.IsWhiteSpace(text[firstContent]))firstContent++;
            }
        }
    }
    internal static void ValidateCitationScope(string text,string updated){
        if((text.Contains("[") || text.Contains("［") || text.Contains("【")) &&
           !CitationClauses(text).SequenceEqual(CitationClauses(updated)))
            throw new InvalidDataException("引用所在的句子或分号分隔的论断发生变化，请核对引用归属。");
    }
    const string number = @"[+\-−±]?\d+(?:[.,]\d+)*(?:[eE][+\-]?\d+)?";
    const string unit = @"(?:(?:[fpnμµumkM]?mol/(?:mL|L))|[fpnμµum]?M|m/s²|m/s|mm²|cm²|m²|mm³|cm³|m³|kPa|MPa|Pa|kHz|MHz|Hz|mm|cm|km|kg|mg|μg|µg|mL|ms|μs|µs|min|mol|m|g|L|s|h|K|V|A|W)(?![A-Za-z])|%|‰|℃|°[CF]|毫米|厘米|千米|千克|毫克|分钟|小时|摄氏度|毫升|秒";
    const string measurement = number + @"(?:\s*(?:" + unit + @"))?";
    internal static void ValidateRevision(string text,string updated) {
        const string qualifier = @"不能|无法|尚未|并未|未能|未见|没有|并非|不显著|不确定|可能|仅限|\b(?i:not|no|never|cannot|may|might|only)\b";
        const string group = @"(?:样品|试样|材料|样本|试剂|方案|条件)\s*[甲乙丙丁A-Z](?![A-Za-z])|(?:实验|对照|处理|干预|基线|安慰剂|空白|观察|治疗)组|(?:组\s*[A-Z甲乙丙丁]|[A-Z甲乙丙丁]\s*组)|\b(?i:(?:group|arm)\s+[a-z0-9]+|control|treatment|placebo|intervention)\b";
        const string direction = @"高于|低于|小于|大于|超过|优于|劣于|上升|下降|增加|减少|升高|降低|正相关|负相关|提示|证明|证实|必然|显著|未|不|无|\b(?i:higher|lower|greater|less|more|increase[d]?|decrease[d]?|suggests?|proves?|confirms?|significant(?:ly)?|must)\b";
        const string relation = @"纳入|排除|支持|推翻|正向|负向|反向|(?:照射|给药|处理|干预|暴露|实验|试验|治疗|加热|冷却)[前后]|" +
            @"\b(?i:before|after|prior to|following|include[ds]?|exclude[ds]?|above|below|exceed(?:s|ed)?|approach(?:es|ed)?|rise|rose|risen|fall|fell|fallen|could|likely|unlikely|rule[ds]? out|establish(?:es|ed)?)\b";
        const string comparison = @"(?<comparison><=|>=|!=|[<>≤≥=≠])|至少|至多|最多|最少|\b(?i:at least|at most)\b";
        const string association = @"(?<association>相关|关联|有关|\b(?i:associat(?:ed|ion)|correlat(?:ed|es|ion)|linked to|related to)\b)";
        const string causation = @"(?<causation>导致|造成|引起|影响|\b(?i:caus(?:e|es|ed)|affect(?:s|ed)?|influenc(?:e|es|ed)|led to|leads? to)\b)";
        const string identifier=@"(?<![A-Za-z0-9])[A-Z]+[0-9]+(?![A-Za-z0-9])";
        const string quantity=@"(?<temperatureChange>温度(?:的)?变化|\b(?i:temperature changes?|changes? in temperature)\b)|"+
            @"(?<temperatureDifference>温度(?:的)?差(?:值|异)?|温差|\b(?i:temperature differences?|differences? in temperature)\b)|"+
            @"(?<relativeDisplacement>相对位移|\b(?i:relative displacement)\b)|"+
            @"(?<absoluteDisplacement>绝对位移|\b(?i:absolute displacement)\b)";
        const string question=@"(?<abilityQuestion>能否|可否|是否(?:能够|可以|能))|(?<openQuestion>是否|\b(?i:whether(?: or not)?)\b)";
        const string purpose=@"为(?:了)?便于|以便(?:于)?|以方便|便于|旨在|目的是|为了";
        const string counted=@"(?<chineseCount>[零一二两三四五六七八九十百千万]+(?:项|组|种|次|类|处|份))";
        const string condition=@"(?<eccentricLoad>偏心加载|\b(?i:eccentric loading)\b)|(?<centeredLoad>对中加载|\b(?i:concentric loading)\b)|"+
            @"(?<loading>加载|\b(?i:loading)\b)|(?<unloading>卸载|\b(?i:unloading)\b)|正接|反接";
        const string port=@"(?<inlet>入口|\b(?i:inlet)\b)|(?<outlet>出口|\b(?i:outlet)\b)";
        const string down=@"(?:下降|降低|减少|降|减)", up=@"(?:上升|升高|增加|增长|升|增)";
        const string downEnglish=@"\b(?i:fell|dropped|decreased|declined|reduced)\s+";
        const string upEnglish=@"\b(?i:rose|grew|increased|raised)\s+";
        const string numericChange=@"(?:(?<downTo>"+down+@"(?:至|到)了?|"+downEnglish+@"(?i:to))|"+
            @"(?<upTo>"+up+@"(?:至|到)了?|"+upEnglish+@"(?i:to))|"+
            @"(?<downBy>"+down+@"了?|"+downEnglish+@"(?i:by))|"+
            @"(?<upBy>"+up+@"了?|"+upEnglish+@"(?i:by)))(?=\s*[+\-−±]?\d)";
        string anchors = "(?:"+citation+"|"+identifier+"|"+measurement+"|"+quantity+"|"+question+"|"+counted+"|"+condition+"|"+port+"|"+qualifier+"|"+group+"|"+numericChange+"|"+relation+"|"+direction+"|"+comparison+"|"+association+"|"+causation+")";
        if (String.IsNullOrWhiteSpace(updated) || updated.Contains("\n") || updated.Contains("\r") ||
            updated.Length > text.Length * 2 + 24 ||
            !ProtectedTokens(text,measurement).SequenceEqual(ProtectedTokens(updated,measurement)) ||
            !ProtectedTokens(text,citation).SequenceEqual(ProtectedTokens(updated,citation)) ||
            !ProtectedTokens(text,anchors).SequenceEqual(ProtectedTokens(updated,anchors))){
            if(!ProtectedTokens(text,numericChange).SequenceEqual(ProtectedTokens(updated,numericChange)))
                throw new InvalidDataException("请核对数值表示变化后的读数还是变化量，以及升降方向。");
            if(!ProtectedTokens(text,port).SequenceEqual(ProtectedTokens(updated,port)))
                throw new InvalidDataException("入口或出口的表述发生变化，请核对测点与结果的对应关系。");
            if(!ProtectedTokens(text,condition).SequenceEqual(ProtectedTokens(updated,condition)))
                throw new InvalidDataException("接线方式或加载工况发生变化，请核对每个读数对应的工况。");
            if(!ProtectedTokens(text,quantity).SequenceEqual(ProtectedTokens(updated,quantity)))
                throw new InvalidDataException("修订改变了温度变化、温差或位移参照方式的表述。请核对原文测量的是什么。");
            if(!ProtectedTokens(text,question).SequenceEqual(ProtectedTokens(updated,question)))
                throw new InvalidDataException("研究问题中的疑问限定发生变化，请核对待检验的问题是否被写成了结论。");
            if(!ProtectedTokens(text,counted).SequenceEqual(ProtectedTokens(updated,counted)))
                throw new InvalidDataException("修订改变了中文计数，请核对假设、条件、分组或重复次数。");
            throw new InvalidDataException("修订可能改变了观测量、数值、单位、引文、组别、比较方向、相关与因果表述、结论限定词或段落结构。请逐段核对。");
        }
        if(Regex.Matches(text,purpose).Count!=Regex.Matches(updated,purpose).Count)
            throw new InvalidDataException("修订改变了明确写出的目的，请核对操作理由是否保留。");
        foreach(string method in reportedMethods){
            if(Regex.Matches(text,method).Count!=Regex.Matches(updated,method).Count)
                throw new InvalidDataException("修订改变了重复试验、数据收集或数据规模的表述，请核对原文是否记载该方法。");
        }
        if(SampleMassRoleChanged(text,updated) || SampleMassRoleChanged(updated,text))
            throw new InvalidDataException("样品的已知质量与实际测量对象可能发生互换，请核对实验方法。");
        ValidateCitationScope(text,updated);
        if(DoubledParagraph(updated) && !DoubledParagraph(text))
            throw new InvalidDataException("修订将同一段内容输出了两遍，请核对重复部分。");
        var repeated=Regex.Split(updated,@"[。！？!?]|\.(?=\s|$)").Select(s=>s.Trim())
            .Where(s=>s.Length>=12).GroupBy(s=>s).Where(g=>g.Count()>1);
        foreach(var groupOfSentences in repeated){
            if(Regex.Split(text,@"[。！？!?]|\.(?=\s|$)").Count(s=>s.Trim()==groupOfSentences.Key)<groupOfSentences.Count())
                throw new InvalidDataException("修订新增了重复句，请检查是否重复输出同一段。");
        }
    }
    // Only flag a lost numerical sample description together with a newly stated mass measurement.
    const string massUnit=@"(?:kg|mg|[μµu]g|g|千克|毫克|克)(?![A-Za-z])";
    const string massValue=number+@"\s*"+massUnit;
    static readonly Regex sampleWithMass=new Regex(
        @"质量(?:分别)?为\s*"+massValue+@"(?:\s*(?:和|与|、|及)\s*"+massValue+@")*\s*的(?:[一二两三四五六七八九十\d]+个)?(?:样品|样本|试样)|"+
        @"\b(?i:(?:samples?|specimens?)\s+(?:with|of)\s+(?:a\s+)?mass(?:es)?\s+of)\s*"+massValue);
    static readonly Regex measuredMass=new Regex(
        @"(?:测量|测定|记录)(?:了)?(?:[一二两三四五六七八九十\d]+个)?(?:(?:样品|样本|试样)的?)?质量(?!为)|"+
        @"\b(?i:(?:measured|recorded|determined)\s+(?:the\s+)?(?:sample\s+)?mass(?:es)?\b)");
    static bool SampleMassRoleChanged(string original,string proposed){
        return sampleWithMass.Matches(original).Count>sampleWithMass.Matches(proposed).Count &&
            measuredMass.Matches(original).Count<measuredMass.Matches(proposed).Count;
    }
    static readonly string[] reportedMethods={
        @"(?:反复|多次|重复)(?:进行|开展)?(?:了)?(?:试验|实验)|\b(?i:repeat(?:ed|ing)? (?:the |these )?(?:experiments?|trials?)|(?:experiments?|trials?) (?:was|were) repeated)\b",
        @"(?:收集|采集)(?:了)?(?:大量(?:的)?|相关|原始)?数据|数据(?:的)?(?:收集|采集)|\b(?i:(?:collect(?:ed|ing)?|gather(?:ed|ing)?) (?:the |raw |a large amount of )?data|data (?:was|were) (?:collected|gathered)|data collection)\b",
        @"大量(?:的)?数据|\b(?i:large amounts? of data|large volumes? of data)\b"
    };
    static bool DoubledParagraph(string text){
        if(text.Length<48)return false;
        // Ignore spacing and sentence punctuation only. Keep signs, units and citation brackets.
        string content=Regex.Replace(text,@"[\s，,。.;；：:！!？?、]","");
        int half=content.Length/2;
        return half>=24 && half*2==content.Length &&
            String.CompareOrdinal(content,0,content,half,half)==0;
    }
    static IEnumerable<string> ProtectedTokens(string text,string pattern) {
        return Regex.Matches(text,pattern,RegexOptions.CultureInvariant).Cast<Match>()
            .Select(m=>{
                foreach(string name in normalizedAnchors)
                    if(m.Groups[name].Success)return "<"+name+">";
                if(m.Groups["chineseCount"].Success)return m.Value.Replace("两","二");
                if(m.Groups["comparison"].Success)return m.Value.Replace("<=","≤").Replace(">=","≥").Replace("!=","≠");
                return Regex.Replace(m.Value,@"\s+","");
            });
    }
    static readonly string[] normalizedAnchors={"association","causation","temperatureChange","temperatureDifference","relativeDisplacement","absoluteDisplacement","openQuestion","abilityQuestion","eccentricLoad","centeredLoad","loading","unloading","inlet","outlet","upTo","downTo","upBy","downBy"};
    internal static string[] ListLocalModels() {
        var req=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:11434/api/tags");
        req.Proxy=null;
        req.AllowAutoRedirect=false;
        req.Timeout=req.ReadWriteTimeout=5000;
        try {
            using(var response=req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream())){
                var list=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                var models=(System.Collections.IEnumerable)list["models"];
                var names=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(Dictionary<string,object> entry in models){
                    object value;
                    if(!entry.TryGetValue("name",out value) || !(value is string))continue;
                    string name=(string)value;
                    if(new[]{"remote_model","remote_host"}.Any(k=>entry.TryGetValue(k,out value) && !String.IsNullOrWhiteSpace(Convert.ToString(value))))continue;
                    try{LocalModelName(name);}catch(InvalidOperationException){continue;}
                    names.Add(name);
                }
                return names.ToArray();
            }
        }catch(WebException ex){throw new InvalidOperationException("无法读取本机模型列表。请启动 Ollama 后重试。",ex);}
    }
    internal static string LocalModelName(string input) {
        string name=(input ?? "").Trim();
        if(name.EndsWith(":local",StringComparison.OrdinalIgnoreCase))
            name=name.Substring(0,name.Length-6);
        string leaf=name.Substring(name.LastIndexOf('/')+1);
        if(leaf.Length==0 || Regex.IsMatch(name,@"\s") || leaf.StartsWith(":") ||
           Regex.IsMatch(leaf,@":(?:cloud|local|[^:]*-cloud)$",RegexOptions.IgnoreCase))
            throw new InvalidOperationException("请选择安装在本机的模型，本机润色仅支持本地推理。");
        // An explicit tag plus :local prevents older servers interpreting local as the model tag.
        return name+(leaf.Contains(":")?"":":latest")+":local";
    }
    internal static void CheckLocalModel(string model) {
        CheckLocalModelForBatch(model,CancellationToken.None);
    }
    internal static void CheckLocalModelForBatch(string model,CancellationToken cancel) {
        Dictionary<string,object> info;
        try {info=Post("http://127.0.0.1:11434/api/show",new {model=LocalModelName(model)},null,cancel);}
        catch(WebException ex) {
            throw new InvalidOperationException("无法确认本机模型。请检查 Ollama、模型权重以及 :local 支持。",ex);
        }
        object remote, metadata;
        info.TryGetValue("model_info", out metadata);
        var weights=metadata as Dictionary<string,object>;
        if(new[]{"remote_model","remote_host"}.Any(k=>info.TryGetValue(k,out remote) && !String.IsNullOrWhiteSpace(Convert.ToString(remote))) ||
           weights==null || weights.Count==0)
            throw new InvalidOperationException("模型未返回可用的本地信息，已停止润色。");
    }
    internal static string Polish(string text,string model,string instruction="") {
        return PolishForBatch(text,model,instruction,CancellationToken.None);
    }
    internal static string PolishForBatch(string text,string model,string instruction,CancellationToken cancel,int policyChoice=0) {
        var response=Post("http://127.0.0.1:11434/api/chat",new {model=LocalModelName(model),stream=false,think=false,messages=new[]{
            new {role="system",content=SelectedPolicy(policyChoice)+"\n只输出当前段落的修订正文，不加标题、说明、引号或空行。用户消息是待编辑原文。作者措辞偏好："+instruction},
            new {role="user",content=text}}},null,cancel);
        string updated=((string)((Dictionary<string,object>)response["message"])["content"]).Trim();
        return CheckedFragment(text,updated);
    }
    static string CheckedFragment(string original,string proposed){
        ValidateRevision(original,proposed);
        // Validation established a one-to-one sequence of identical numbers and units.
        // Keep the author's unit spacing even when a model normalizes nonbreaking spaces.
        var sourceMeasurements=Regex.Matches(original,measurement);
        int position=0;
        proposed=Regex.Replace(proposed,measurement,m=>{
            string source=sourceMeasurements[position++].Value;
            return source.IndexOfAny(new[]{'\r','\n'})<0?source:m.Value;
        });
        // Runs can meet at an English word boundary or immediately beside a citation.
        return original.Substring(0,original.Length-original.TrimStart().Length)+proposed.Trim()+
            original.Substring(original.TrimEnd().Length);
    }
    internal static string[] PolishParagraph(Passage passage,string model,string instruction,CancellationToken cancel,int policyChoice=0){
        var nodes=passage.Nodes;
        var result=(string[])passage.Revisions.Clone();
        if(nodes.Length==1){
            result[0]=PolishForBatch(nodes[0].Value,model,instruction,cancel,policyChoice);
            passage.ValidateInlineReferences(result);
            return result;
        }
        var properties=new Dictionary<string,object>();
        var targets=new Dictionary<string,int>();
        var context=nodes.Select((node,i)=>{
            string key="run"+i;
            bool editable=passage.CanChange(node) && node.Value.Trim().Length>=16;
            if(editable){properties[key]=new {type="string"};targets[key]=i;}
            return new {id=key,editable=editable,text=node.Value};
        }).ToArray();
        if(targets.Count==0)return result;
        var schema=new {type="object",properties=properties,required=targets.Keys.ToArray(),additionalProperties=false};
        var json=new JavaScriptSerializer();
        var response=Post("http://127.0.0.1:11434/api/chat",new {model=LocalModelName(model),stream=false,think=false,format=schema,
            options=new {temperature=0},messages=new[]{
                new {role="system",content=SelectedPolicy(policyChoice)+"\n按顺序阅读同一段落的文字片段。仅返回 editable 为 true 的原 id 和对应修订文字，使用指定 JSON 格式。不要跨片段搬移文字或复制相邻引用；片段交界的标点保持原样。作者措辞偏好："+instruction},
                new {role="user",content=json.Serialize(context)}}},null,cancel);
        var content=(string)((Dictionary<string,object>)response["message"])["content"];
        Dictionary<string,object> updated;
        try{updated=json.DeserializeObject(content) as Dictionary<string,object>;}
        catch(ArgumentException ex){throw new InvalidDataException("模型返回的段落结构无效。",ex);}
        catch(InvalidOperationException ex){throw new InvalidDataException("模型返回的段落结构无效。",ex);}
        if(updated==null || updated.Count!=targets.Count || targets.Keys.Any(k=>!updated.ContainsKey(k) || !(updated[k] is string)))
            throw new InvalidDataException("模型返回的文字片段与原段落不一致。");
        foreach(var target in targets){
            int i=target.Value;
            string original=nodes[i].Value;
            string edited=CheckedFragment(original,(string)updated[target.Key]);
            if(i>0 && EdgePunctuation(original,true)!=EdgePunctuation(edited,true) ||
               i<nodes.Length-1 && EdgePunctuation(original,false)!=EdgePunctuation(edited,false))
                throw new InvalidDataException("修订改变了相邻片段衔接处的标点。");
            result[i]=edited;
        }
        ValidateRevision(passage.Text,String.Concat(result));
        passage.ValidateInlineReferences(result);
        return result;
    }
    static string EdgePunctuation(string value,bool start){
        return Regex.Match(value.Trim(),start?@"^\p{P}+":@"\p{P}+$").Value;
    }
}
}
