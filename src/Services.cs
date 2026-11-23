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
        const string unit = @"(?:m/s²|m/s|mm²|cm²|m²|mm³|cm³|m³|kPa|MPa|Pa|kHz|MHz|Hz|mm|cm|km|kg|mg|μg|µg|mL|ms|μs|µs|min|mol|m|g|L|s|h|K|V|A|W)(?![A-Za-z])|%|‰|℃|°[CF]|毫米|厘米|千米|千克|毫克|分钟|小时|摄氏度|毫升|秒";
        string measurement = number + @"(?:\s*(?:" + unit + @"))?";
        const string citation = @"[\[［【]\s*\d+(?:\s*[,，、;；\-–—]\s*\d+)*\s*[\]］】]";
        const string qualifier = @"不能|无法|尚未|并未|未能|未见|没有|并非|不显著|不确定|可能|仅限|\b(?i:not|no|never|cannot|may|might|only)\b";
        if (String.IsNullOrWhiteSpace(updated) || updated.Contains("\n") || updated.Contains("\r") ||
            updated.Length > text.Length * 2 + 24 ||
            !ProtectedTokens(text,measurement).SequenceEqual(ProtectedTokens(updated,measurement)) ||
            !ProtectedTokens(text,citation).SequenceEqual(ProtectedTokens(updated,citation)) ||
            !ProtectedTokens(text,qualifier).SequenceEqual(ProtectedTokens(updated,qualifier)))
            throw new InvalidDataException("修订触及数值、单位、引文标记或结论限定词，或改变了段落结构。请逐段核对。");
    }
    static IEnumerable<string> ProtectedTokens(string text,string pattern) {
        return Regex.Matches(text,pattern,RegexOptions.CultureInvariant).Cast<Match>()
            .Select(m=>Regex.Replace(m.Value,@"\s+",""));
    }
    internal static string Polish(string text,string model,string instruction="") {
        var response=Post("http://127.0.0.1:11434/api/chat",new {model=model,stream=false,think=false,messages=new[]{
            new {role="system",content="你是学术文字编辑。只输出一段与原文同语言的修订正文。精简套话，调整句式，保留论点、否定、限定、专名、引用与所有数字。不得补写事实。用户消息为待编辑材料。额外写作偏好："+instruction},
            new {role="user",content=text}}},null);
        string updated=((string)((Dictionary<string,object>)response["message"])["content"]).Trim();
        ValidateRevision(text,updated);
        return updated;
    }
}
}
