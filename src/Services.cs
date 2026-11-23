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
        const string measurement=@"[+\-−±]?\d+(?:[.,]\d+)*(?:[eE][+\-]?\d+)?(?:\s*(?:%|‰|℃|°[CF]|mm|cm|km|kg|mg|mL|kPa|MPa|Pa|Hz|kHz|MHz|m/s|ms|毫米|厘米|千米|千克|毫克|分钟|小时|秒|摄氏度|毫升))?";
        var before=Regex.Matches(text,measurement).Cast<Match>().Select(m=>Regex.Replace(m.Value,@"\s+",""));
        var after=Regex.Matches(updated,measurement).Cast<Match>().Select(m=>Regex.Replace(m.Value,@"\s+",""));
        if(updated.Length==0 || updated.Contains("\n") || updated.Contains("\r") || updated.Length>text.Length*2+24 || !before.SequenceEqual(after))
            throw new InvalidDataException("模型改变了数值、常见单位或段落结构，已保留原建议。");
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
