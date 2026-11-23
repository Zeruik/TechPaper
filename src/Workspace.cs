using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
[assembly:AssemblyTitle("TechPaper")]
[assembly:AssemblyProduct("TechPaper")]
[assembly:AssemblyVersion("0.9.0.0")]

namespace TechPaper {
internal sealed partial class Workspace : Form {
    Manuscript doc;
    readonly Color teal=Color.FromArgb(8,81,80), ink=Color.FromArgb(33,52,53);
    Label file,summary,status;
    ListView rows;
    RichTextBox before,after;
    CheckBox clean;
    Button save,cloud,model,open;
    bool stopBatch;
    bool busy;
    internal Workspace() {
        Text="TechPaper · 论文审阅工作台"; Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(248,248,243); ForeColor=ink;
        MinimumSize=new Size(850,620); Size=new Size(1120,780); StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(22,16,22,12)};
        foreach(float h in new float[]{59,64,-53,-47,58,28})layout.RowStyles.Add(new RowStyle(h<0?SizeType.Percent:SizeType.Absolute,Math.Abs(h)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(layout);
        var head=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        head.Controls.Add(new PictureBox{Image=Icon.ToBitmap(),SizeMode=PictureBoxSizeMode.Zoom,Width=42,Height=42,Margin=new Padding(0,3,10,0)});
        head.Controls.Add(new Label{Text="TechPaper",Font=new Font("Georgia",25,FontStyle.Bold),ForeColor=teal,AutoSize=true});
        head.Controls.Add(new Label{Text="论文审阅工作台",AutoSize=true,Margin=new Padding(10,18,0,0)});layout.Controls.Add(head,0,0);
        var bar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        file=LabelAt("打开或拖入论文、幻灯片及图像",12);summary=LabelAt("DOCX / PPTX / PDF / PNG / JPG · 可拖放文件",9);
        bar.Controls.Add(file,0,0);bar.Controls.Add(summary,0,1);
        open=ButtonAt("打开文件",Choose);bar.Controls.Add(open,1,0);bar.SetRowSpan(open,2);layout.Controls.Add(bar,0,1);
        rows=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,CheckBoxes=true,HideSelection=false,MultiSelect=false,BorderStyle=BorderStyle.FixedSingle};
        rows.Columns.Add("采用",65);rows.Columns.Add("段落",58);rows.Columns.Add("文风提示",150);rows.Columns.Add("修订",100);rows.Columns.Add("正文摘录",550);
        rows.SelectedIndexChanged+=(s,e)=>Preview();rows.ItemCheck+=(s,e)=>{if(doc!=null && e.Index<doc.Items.Count && doc.Items[e.Index].Text==doc.Items[e.Index].Suggested)e.NewValue=CheckState.Unchecked;};
        layout.Controls.Add(rows,0,2);
        var compare=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new Padding(0,8,0,0)};
        for(int i=0;i<2;i++)compare.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        compare.RowStyles.Add(new RowStyle(SizeType.Absolute,28));compare.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        compare.Controls.Add(LabelAt("原文",10),0,0);compare.Controls.Add(LabelAt("修订预览",10),1,0);
        before=CompareBox();after=CompareBox();compare.Controls.Add(before,0,1);compare.Controls.Add(after,1,1);layout.Controls.Add(compare,0,3);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,8,0,0)};
        save=ButtonAt("导出 DOCX 副本",Export);save.BackColor=teal;save.ForeColor=Color.White;
        clean=new CheckBox{Text="清理文档元数据",Checked=false,AutoSize=true,Margin=new Padding(8,10,12,0)};
        cloud=ButtonAt("GPTZero 检测…",Online);model=ButtonAt("本机模型润色…",Local);
        actions.Controls.AddRange(new Control[]{save,clean,model,cloud});layout.Controls.Add(actions,0,4);
        status=LabelAt("本地模式 · 打开文档后自动分析措辞、句长和重复句首。",9);layout.Controls.Add(status,0,5);
        SetupTools();EnableActions();FormClosing+=(s,e)=>{if(busy)e.Cancel=true;};
    }
    Label LabelAt(string title,int size){return new Label{Text=title,Font=new Font(Font.FontFamily,size),AutoEllipsis=true,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};}
    RichTextBox CompareBox(){return new RichTextBox{ReadOnly=true,DetectUrls=false,Dock=DockStyle.Fill,BorderStyle=BorderStyle.FixedSingle};}
    TextBox Box(){return new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle};}
    Button ButtonAt(string label,EventHandler handler){var b=new Button{Text=label,AutoSize=true,Height=36,FlatStyle=FlatStyle.Flat,BackColor=Color.White,Padding=new Padding(8,0,8,0)};b.FlatAppearance.BorderColor=Color.Silver;b.Click+=handler;return b;}
    void EnableActions(){open.Enabled=!busy;rows.Enabled=!busy;save.Enabled=cloud.Enabled=model.Enabled=doc!=null&&!busy;if(reportAction!=null) reportAction.Enabled=activeFile!=null&&!busy;}
    async void Run(Func<Task> work){if(busy)return;busy=true;EnableActions();try{await work();}catch(Exception ex){status.Text="操作未完成";MessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);}finally{busy=false;EnableActions();}}
    void Choose(object s,EventArgs e){using(var d=new OpenFileDialog{Filter="支持的文档和图片|*.docx;*.pptx;*.pdf;*.png;*.jpg;*.jpeg"})if(d.ShowDialog(this)==DialogResult.OK)LoadFile(d.FileName);}
    internal void LoadFile(string path){Run(async delegate{
        ValidateFile(path);status.Text="正在读取文件…";
        if(Path.GetExtension(path).ToLowerInvariant()!=".docx"){
            doc=null;activeFile=Path.GetFullPath(path);rows.Items.Clear();before.Clear();after.Clear();
            file.Text=Path.GetFileName(path);summary.Text="原文提取与离线 OCR";status.Text="点击「导出高亮报告」进行检测。";return;
        }
        Manuscript loaded;
        try{loaded=await Task.Run(()=>new Manuscript(path));}catch(InvalidDataException ex){
            if(!ex.Message.Contains("160 MB"))throw;
            doc=null;activeFile=path;rows.Items.Clear();before.Clear();after.Clear();file.Text=Path.GetFileName(path);summary.Text="长文报告模式";status.Text=ex.Message+" 可使用流式高亮报告。";return;
        }
        doc=loaded;activeFile=Path.GetFullPath(path);
        file.Text=Path.GetFileName(path);rows.BeginUpdate();rows.Items.Clear();
        foreach(var p in doc.Items){int n=rows.Items.Count;var row=new ListViewItem("");row.SubItems.AddRange(new[]{(n+1).ToString(),p.Reason,p.Editable?(p.Text==p.Suggested?"原样":"建议精简"):"结构保护",p.Text});rows.Items.Add(row);row.Checked=false;}
        rows.EndUpdate();if(rows.Items.Count>0)rows.Items[0].Selected=true;Preview();
        summary.Text=String.Format("正文段落 {0}    ·    有文风提示 {1}    ·    初始修订建议 {2}",doc.Items.Count,doc.Items.Count(p=>p.Reason!="—"),doc.Items.Count(p=>p.Text!=p.Suggested));
        status.Text="勾选要采用的修订后导出。文风提示仅供复核。";});}
    int Selected(){return rows.SelectedIndices.Count==0?-1:rows.SelectedIndices[0];}
    void Preview(){
        int n=Selected();
        string original=n<0?"":doc.Items[n].Text, revised=n<0?"":doc.Items[n].Suggested;
        before.Text=original;after.Text=revised;
        if(original==revised)return;
        int first=0,last=0,limit=Math.Min(original.Length,revised.Length);
        while(first<limit && original[first]==revised[first])first++;
        while(last<limit-first && original[original.Length-last-1]==revised[revised.Length-last-1])last++;
        MarkChange(before,first,original.Length-first-last);
        MarkChange(after,first,revised.Length-first-last);
    }
    static void MarkChange(RichTextBox box,int start,int length){
        box.Select(start,length);
        box.SelectionBackColor=Color.FromArgb(255,229,154);
        box.SelectionColor=Color.FromArgb(40,35,25);
        box.Select(start,0);
    }
    void Export(object s,EventArgs e){using(var d=new SaveFileDialog{Filter="Word 文档|*.docx",FileName=Path.GetFileNameWithoutExtension(doc.Source)+"-审阅稿.docx",InitialDirectory=Path.GetDirectoryName(doc.Source)}){
        if(d.ShowDialog(this)!=DialogResult.OK)return;var path=d.FileName;var selected=new HashSet<int>(rows.CheckedIndices.Cast<int>());bool scrub=clean.Checked;
        bool removeReview=false;
        try{if(scrub && !ConfirmPrivacy(out removeReview))return;}
        catch(Exception ex){MessageBox.Show(this,"无法读取隐私清单："+ex.Message,Text);return;}
        Run(async delegate{int changes=await Task.Run(()=>doc.Export(path,selected,scrub,removeReview));status.Text="已重开核对正文结构 · 改动 "+changes+" 段 · "+path;});}}
    string Ask(string title,string hint,string preset,bool secret,string preview=null){
        using(var dlg=new Form{Text=title,Width=630,Height=preview==null?215:450,StartPosition=FormStartPosition.CenterParent,Font=Font,MinimizeBox=false,MaximizeBox=false}){
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),RowCount=preview==null?3:4,ColumnCount=1};
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            panel.Controls.Add(LabelAt(hint,9));var input=new TextBox{Text=preset,Dock=DockStyle.Fill,UseSystemPasswordChar=secret};panel.Controls.Add(input);
            if(preview!=null){panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));var box=Box();box.Text=preview;panel.Controls.Add(box);}
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,42));var ok=ButtonAt(secret?"发送以上文本":"生成修订预览",(s,e)=>{});ok.DialogResult=DialogResult.OK;panel.Controls.Add(ok);
            dlg.Controls.Add(panel);dlg.AcceptButton=ok;return dlg.ShowDialog(this)==DialogResult.OK?input.Text.Trim():null;}}
    void Online(object s,EventArgs e){string text=String.Join("\r\n\r\n",doc.Items.Select(p=>p.Text));
        if(text.Length<1 || text.Length>150000){MessageBox.Show("请选用正文在 1 至 150000 字符内的文档。");return;}
        string key=Ask("GPTZero 在线检测","下方正文将发送至 GPTZero，按其套餐计费。请输入 API Key，仅在本次请求中使用。",Environment.GetEnvironmentVariable("GPTZERO_API_KEY")??"",true,text);
        if(String.IsNullOrEmpty(key))return;Run(async delegate{status.Text="等待 GPTZero 返回检测结果…";string result=await Task.Run(()=>Services.Scan(text,key));status.Text="GPTZero 检测已完成";MessageBox.Show(this,result,"GPTZero · 全文检测");});}
    void Local(object s,EventArgs e){
        if(busy){stopBatch=true;model.Enabled=false;status.Text="将在当前段完成后停止。";return;}
        var choice=MessageBox.Show(this,"是：润色全部可处理段落。\r\n否：仅润色当前选中段落。","选择本机润色范围",MessageBoxButtons.YesNoCancel);
        if(choice==DialogResult.Cancel)return;
        int current=Selected();
        var indices=Enumerable.Range(0,doc.Items.Count).Where(i=>(choice==DialogResult.Yes || i==current) &&
            doc.Items[i].Nodes.Any(n=>doc.Items[i].CanChange(n) && n.Value.Trim().Length>=16)).ToArray();
        if(indices.Length==0){MessageBox.Show("此范围内没有足够长的可安全编辑正文片段。");return;}
        string modelName=Ask("本机 Ollama 润色","输入已安装的本地模型名。将向 127.0.0.1:11434 逐段提交 "+indices.Length+" 段，生成结果可逐段对照。","qwen3.5:2b",false);
        if(String.IsNullOrWhiteSpace(modelName))return;
        Run(async delegate{
            int done=0,skipped=0;stopBatch=false;model.Text="停止润色";model.Enabled=true;
            try {
                foreach(int i in indices){
                    if(stopBatch)break;
                    var p=doc.Items[i];status.Text=String.Format("正在处理第 {0}/{1} 段…",done+skipped+1,indices.Length);
                    try {
                        string[] result=(string[])p.Revisions.Clone();var nodes=p.Nodes;
                        for(int j=0;j<nodes.Length;j++){
                            if(!p.CanChange(nodes[j]) || nodes[j].Value.Trim().Length<16)continue;
                            string source=nodes[j].Value;
                            result[j]=await Task.Run(()=>Services.Polish(source,modelName,prompt));
                        }
                        p.Revisions=result;p.Suggested=String.Concat(result);
                    }
                    catch(InvalidDataException){skipped++;continue;}
                    rows.Items[i].SubItems[3].Text="模型建议";rows.Items[i].Checked=false;done++;Preview();
                }
                status.Text=String.Format("已生成 {0} 段模型建议，{1} 段未通过数字或结构检查。核对后可勾选采用。",done,skipped);
            }finally{model.Text="本机模型润色…";}
        });
    }
}
internal static class Program{
    [STAThread]static void Main(string[] args){System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var form=new Workspace();
        if(args.Length==1)form.Shown+=(s,e)=>form.LoadFile(args[0]);Application.Run(form);}
}
}
