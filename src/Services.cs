using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;

namespace TechPaper {
internal static class Services {
    static Dictionary<string,object> Post(string url,object payload,string key) {
        var json=new JavaScriptSerializer {MaxJsonLength=16*1024*1024};
        var req=(HttpWebRequest)WebRequest.Create(url);
        req.Method="POST"; req.ContentType="application/json"; req.Timeout=120000; req.ReadWriteTimeout=120000;
        req.AllowAutoRedirect=false;
        if(key!=null)req.Headers.Add("x-api-key",key); else req.Proxy=null;
        byte[] bytes=Encoding.UTF8.GetBytes(json.Serialize(payload));
        using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
        using(var response=req.GetResponse())using(var r=new StreamReader(response.GetResponseStream()))
            return json.Deserialize<Dictionary<string,object>>(r.ReadToEnd());
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
    internal static void ValidateRevision(string text,string updated) {
        const string number = @"[+\-−±]?\d+(?:[.,]\d+)*(?:[eE][+\-]?\d+)?";
        const string unit = @"(?:(?:[fpnμµumkM]?mol/(?:mL|L))|[fpnμµum]?M|m/s²|m/s|mm²|cm²|m²|mm³|cm³|m³|kPa|MPa|Pa|kHz|MHz|Hz|mm|cm|km|kg|mg|μg|µg|mL|ms|μs|µs|min|mol|m|g|L|s|h|K|V|A|W)(?![A-Za-z])|%|‰|℃|°[CF]|毫米|厘米|千米|千克|毫克|分钟|小时|摄氏度|毫升|秒";
        string measurement = number + @"(?:\s*(?:" + unit + @"))?";
        const string citation = @"[\[［【]\s*\d+(?:\s*[,，、;；\-–—]\s*\d+)*\s*[\]］】]";
        const string qualifier = @"不能|无法|尚未|并未|未能|未见|没有|并非|不显著|不确定|可能|仅限|\b(?i:not|no|never|cannot|may|might|only)\b";
        const string group = @"(?:样品|试样|材料|样本|试剂|方案|条件)\s*[甲乙丙丁A-Z](?![A-Za-z])|(?:实验|对照|处理|干预|基线|安慰剂|空白|观察|治疗)组|(?:组\s*[A-Z甲乙丙丁]|[A-Z甲乙丙丁]\s*组)|\b(?i:(?:group|arm)\s+[a-z0-9]+|control|treatment|placebo|intervention)\b";
        const string direction = @"高于|低于|小于|大于|超过|优于|劣于|上升|下降|增加|减少|升高|降低|正相关|负相关|提示|证明|证实|必然|显著|未|不|无|[<>≤≥]|\b(?i:higher|lower|greater|less|more|increase[d]?|decrease[d]?|suggests?|proves?|confirms?|significant(?:ly)?|must)\b";
        const string relation = @"纳入|排除|支持|推翻|正向|负向|反向|(?:照射|给药|处理|干预|暴露|实验|试验|治疗|加热|冷却)[前后]|" +
            @"\b(?i:before|after|prior to|following|include[ds]?|exclude[ds]?|above|below|exceed(?:s|ed)?|approach(?:es|ed)?|rise|rose|risen|fall|fell|fallen|could|likely|unlikely|rule[ds]? out|establish(?:es|ed)?)\b";
        const string association = @"(?<association>相关|关联|有关|\b(?i:associat(?:ed|ion)|correlat(?:ed|es|ion)|linked to|related to)\b)";
        const string causation = @"(?<causation>导致|造成|引起|影响|\b(?i:caus(?:e|es|ed)|affect(?:s|ed)?|influenc(?:e|es|ed)|led to|leads? to)\b)";
        string anchors = "(?:"+citation+"|"+measurement+"|"+qualifier+"|"+group+"|"+relation+"|"+direction+"|"+association+"|"+causation+")";
        if (String.IsNullOrWhiteSpace(updated) || updated.Contains("\n") || updated.Contains("\r") ||
            updated.Length > text.Length * 2 + 24 ||
            !ProtectedTokens(text,measurement).SequenceEqual(ProtectedTokens(updated,measurement)) ||
            !ProtectedTokens(text,citation).SequenceEqual(ProtectedTokens(updated,citation)) ||
            !ProtectedTokens(text,anchors).SequenceEqual(ProtectedTokens(updated,anchors)))
            throw new InvalidDataException("修订可能改变了数值、单位、引文、组别、比较方向、相关与因果表述、结论限定词或段落结构。请逐段核对。");
    }
    static IEnumerable<string> ProtectedTokens(string text,string pattern) {
        return Regex.Matches(text,pattern,RegexOptions.CultureInvariant).Cast<Match>()
            .Select(m=>m.Groups["association"].Success ? "<association>" :
                       m.Groups["causation"].Success ? "<causation>" : Regex.Replace(m.Value,@"\s+",""));
    }
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
        Dictionary<string,object> info;
        try {info=Post("http://127.0.0.1:11434/api/show",new {model=LocalModelName(model)},null);}
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
        var response=Post("http://127.0.0.1:11434/api/chat",new {model=LocalModelName(model),stream=false,think=false,messages=new[]{
            new {role="system",content="你是学术文字编辑。只输出一段与原文同语言的修订正文。精简套话，调整句式，保留论点、否定、限定、专名、引用与所有数字。不得补写事实。用户消息为待编辑材料。额外写作偏好："+instruction},
            new {role="user",content=text}}},null);
        string updated=((string)((Dictionary<string,object>)response["message"])["content"]).Trim();
        ValidateRevision(text,updated);
        return updated;
    }
}
}
