using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TechPaper {
internal sealed partial class Workspace {
    string activeFile,corpus,prompt="",theme="跟随系统";
    int policyChoice;
    Button reportAction;
    Process worker;
    bool cancelling;
    readonly string settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TechPaper","preferences.txt");
    void SetupTools(){
        AutoScaleMode=AutoScaleMode.Dpi; KeyPreview=true;AllowDrop=true;
        DragEnter+=(s,e)=>{if(!busy && e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
        DragDrop+=(s,e)=>{var paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(!busy && paths!=null && paths.Length==1)LoadFile(paths[0]);else status.Text="请一次拖入一个文件。";};
        var layout=(TableLayoutPanel)Controls[0];var head=(FlowLayoutPanel)layout.GetControlFromPosition(0,0);
        reportAction=ButtonAt("导出高亮报告",Report);head.Controls.Add(reportAction);
        var more=ButtonAt("设置与说明 ▾",(s,e)=>{});head.Controls.Add(more);
        var menu=new ContextMenuStrip();
        menu.Items.Add("粘贴文本并自动分段…",null,(s,e)=>PasteParagraphs());
        menu.Items.Add("编辑当前段安全片段…",null,(s,e)=>EditFragments());
        rows.DoubleClick+=(s,e)=>EditFragments();
        menu.Items.Add("选择本地比对语料 (.txt)…",null,(s,e)=>{using(var d=new OpenFileDialog{Filter="UTF-8 文本|*.txt"})if(d.ShowDialog(this)==DialogResult.OK){corpus=d.FileName;status.Text="本地比对语料："+Path.GetFileName(corpus);}});
        menu.Items.Add("清除本地语料",null,(s,e)=>{corpus=null;status.Text="已清除比对语料。";});
        var policies=new ToolStripMenuItem("润色模式");menu.Items.Add(policies);
        foreach(string label in Services.PolicyChoices)policies.DropDownItems.Add(label,null,(s,e)=>{
            policyChoice=Array.IndexOf(Services.PolicyChoices,((ToolStripItem)s).Text);StorePrefs();
        });
        policies.DropDownOpening+=(s,e)=>{
            for(int i=0;i<policies.DropDownItems.Count;i++)((ToolStripMenuItem)policies.DropDownItems[i]).Checked=i==policyChoice;
        };
        menu.Items.Add("查看内置润色提示词…",null,(s,e)=>{
            using(var dlg=new Form{Text="内置润色提示词",Size=new Size(680,460),StartPosition=FormStartPosition.CenterParent,Font=Font}){
                var text=Box();text.Text=Services.SelectedPolicy(policyChoice).Replace("\n","\r\n\r\n");dlg.Controls.Add(text);dlg.ShowDialog(this);
            }
        });
        menu.Items.Add("润色提示词…",null,(s,e)=>{var value=Ask("润色偏好","补充作者语气或术语偏好，保留引用和数值的规则始终生效。",prompt,false);if(value!=null){prompt=value;StorePrefs();}});
        var themes=new ToolStripMenuItem("外观");menu.Items.Add(themes);
        foreach(string label in new[]{"跟随系统","亮色","暗色"})themes.DropDownItems.Add(label,null,(s,e)=>{theme=((ToolStripItem)s).Text;ApplyTheme();StorePrefs();});
        menu.Items.Add("关于 / 使用说明",null,(s,e)=>MessageBox.Show(this,
            "TechPaper 0.9.0-preview.22 公开预览版\r\n\r\n打开或拖入 DOCX、PPTX、PDF、PNG 或 JPG。导出离线 HTML 可查看原句、所在位置、OCR 置信和本地语料命中。DOCX 可另外导出逐段修订副本。\r\n\r\nAI、MIX、HUMAN 仅为透明规则的低置信写作提示；不足时弃答。来源相似度仅检查指定语料的原文重叠。\r\n\r\n离线分析无需账户。本地模型需要 Ollama；在线 GPTZero 需要用户主动发送预览文本。文档不作遥测。请核对参考文献、数字、分页及批注。\r\n\r\n主程序 MIT；依赖许可见 third-party。Ctrl+O 打开，Ctrl+R 导出报告，Esc 取消报告。","关于 TechPaper"));
        more.Click+=(s,e)=>menu.Show(more,new Point(0,more.Height));
        KeyDown+=(s,e)=>{if(e.Control&&e.KeyCode==Keys.O&&!busy){e.SuppressKeyPress=true;Choose(s,e);}else if(e.Control&&e.KeyCode==Keys.R&&reportAction.Enabled){e.SuppressKeyPress=true;Report(s,e);}else if(e.KeyCode==Keys.Escape&&(worker!=null || polishCancellation!=null)){e.SuppressKeyPress=true;if(polishCancellation!=null)StopPolishing();else CancelReport();}};
        try{if(File.Exists(settings)){var lines=File.ReadAllLines(settings);if(lines.Length>0)theme=lines[0];if(lines.Length>1)prompt=lines[1];if(lines.Length>2)policyChoice=Math.Max(0,Array.IndexOf(Services.PolicyChoices,lines[2]));}}catch(IOException){}
        ApplyTheme();
        Activated+=(s,e)=>{if(theme=="跟随系统")ApplyTheme();};
    }
    void EditFragments(){
        int selected=Selected();if(busy || doc==null || selected<0)return;
        var item=doc.Items[selected];var nodes=item.Nodes;
        var indices=Enumerable.Range(0,nodes.Length).Where(i=>item.CanChange(nodes[i])).ToArray();
        if(indices.Length==0){MessageBox.Show(this,"此段没有可安全编辑的文字片段。");return;}
        using(var dlg=new Form{Text="编辑安全片段",Size=new Size(660,350),Font=Font,StartPosition=FormStartPosition.CenterParent}){
            var select=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};
            var field=new TextBox{Multiline=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
            foreach(var i in indices)select.Items.Add("片段 "+(i+1)+" · "+nodes[i].Value);
            select.SelectedIndexChanged+=(s,e)=>field.Text=item.Revisions[indices[select.SelectedIndex]];
            select.SelectedIndex=0;
            var apply=ButtonAt("保存当前片段并预览",(s,e)=>{
                try{item.SetFragment(indices[select.SelectedIndex],field.Text);dlg.DialogResult=DialogResult.OK;}
                catch(Exception ex){MessageBox.Show(dlg,ex.Message);}
            });
            apply.Dock=DockStyle.Bottom;dlg.Controls.Add(field);dlg.Controls.Add(select);dlg.Controls.Add(apply);
            if(dlg.ShowDialog(this)==DialogResult.OK){rows.Items[selected].ToolTipText="";rows.Items[selected].SubItems[3].Text="手动建议";rows.Items[selected].Checked=false;Preview();status.Text="已更新预览，请核对并勾选采用。";}
        }
    }
    void PasteParagraphs(){
        if(busy)return;
        using(var dlg=new Form{Text="粘贴文本并自动分段",Size=new Size(660,410),StartPosition=FormStartPosition.CenterParent,Font=Font}){
            var input=new TextBox{Multiline=true,AcceptsReturn=true,MaxLength=0,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
            var hint=LabelAt("按换行分段，也识别同一行连续的 H1、H2、H3 假设。保留原排版请直接打开 DOCX。",9);
            hint.Dock=DockStyle.Top;hint.Height=42;
            var accept=ButtonAt("导入 0 段",(s,e)=>{});accept.Enabled=false;accept.Dock=DockStyle.Bottom;accept.DialogResult=DialogResult.OK;
            input.TextChanged+=(s,e)=>{int count=Manuscript.SplitPastedText(input.Text).Length;accept.Text="导入 "+count+" 段";accept.Enabled=count>0;};
            dlg.Controls.Add(input);dlg.Controls.Add(hint);dlg.Controls.Add(accept);dlg.ActiveControl=input;
            if(dlg.ShowDialog(this)!=DialogResult.OK)return;
            var text=input.Text;
            Run(async delegate{var loaded=await Task.Run(()=>Manuscript.FromPastedText(text));ShowManuscript(loaded);});
        }
    }
    void StorePrefs(){try{Directory.CreateDirectory(Path.GetDirectoryName(settings));File.WriteAllLines(settings,new[]{theme,prompt,Services.PolicyChoices[policyChoice]});}catch(Exception ex){status.Text="偏好保存失败："+ex.Message;}}
    bool? appliedDarkTheme;
    void ApplyTheme(){
        bool dark=theme=="暗色" || theme=="跟随系统" && Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0;
        if(appliedDarkTheme==dark)return;
        appliedDarkTheme=dark;
        Color bg=dark?Color.FromArgb(26,38,37):Color.FromArgb(248,248,243), surface=dark?Color.FromArgb(40,55,54):Color.White;
        ForeColor=dark?Color.WhiteSmoke:ink;BackColor=bg;
        foreach(var c in Children(this)){
            c.ForeColor=ForeColor;
            if(c is TextBoxBase || c is ListView || c is Button)c.BackColor=surface;
            else if(c is Panel || c is Label || c is CheckBox)c.BackColor=bg;
        }
        save.BackColor=teal;save.ForeColor=Color.White;
        Preview();
    }
    static IEnumerable<Control> Children(Control c){foreach(Control child in c.Controls){yield return child;foreach(var other in Children(child))yield return other;}}
    internal static void ValidateFile(string path){
        if(!File.Exists(path))throw new FileNotFoundException("文件不存在。");
        string ext=Path.GetExtension(path).ToLowerInvariant();
        if(ext==".docx" || ext==".pptx"){
            using(var z=ZipFile.OpenRead(path))if(z.GetEntry(ext==".docx"?"word/document.xml":"ppt/presentation.xml")==null)throw new InvalidDataException("文件缺少预期的 Office 正文部件。");
        }else if(ext==".jpg" || ext==".jpeg" || ext==".png"){
            using(var fs=File.OpenRead(path))using(var img=Image.FromStream(fs,false,true)){if(img.Width<1)throw new InvalidDataException("图片没有像素。");}
        }else if(ext==".pdf"){
            using(var fs=File.OpenRead(path)){var magic=new byte[1024];int n=fs.Read(magic,0,magic.Length);if(!Encoding.ASCII.GetString(magic,0,n).Contains("%PDF-"))throw new InvalidDataException("PDF 文件头无效。");}
        }else throw new InvalidDataException("支持 DOCX、PPTX、PDF、PNG 和 JPG。");
    }
    bool ConfirmPrivacy(out bool comments){
        using(var dlg=new Form{Text="隐私清理预览",Size=new Size(610,380),Font=Font,StartPosition=FormStartPosition.CenterParent}){
            var details=Box();details.Text=doc.PrivacyPreview();
            var check=new CheckBox{Text="额外删除批注并匿名化修订者（会影响审阅记录）",Dock=DockStyle.Bottom,Height=42};
            check.CheckedChanged+=(s,e)=>details.Text=doc.PrivacyPreview(check.Checked);
            var ok=ButtonAt("采用以上清理并导出",(s,e)=>{});ok.DialogResult=DialogResult.OK;ok.Dock=DockStyle.Bottom;
            dlg.Controls.Add(details);dlg.Controls.Add(check);dlg.Controls.Add(ok);dlg.CancelButton=null;
            bool accepted=dlg.ShowDialog(this)==DialogResult.OK;comments=check.Checked;return accepted;
        }
    }
    internal void CancelReport(){cancelling=true;try{if(worker!=null&&!worker.HasExited)worker.Kill();}catch(InvalidOperationException){}}
    static string Arg(string value){return "\""+value+"\"";}
    void Report(object sender,EventArgs e){
        if(worker!=null){CancelReport();return;}if((activeFile==null&&doc==null)||busy)return;
        using(var dialog=new SaveFileDialog{Filter="离线 HTML 报告|*.html",FileName=(Path.GetFileNameWithoutExtension(activeFile)??"粘贴文本")+"-原文报告.html"}){
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            var dest=dialog.FileName;if(File.Exists(dest)){MessageBox.Show("请选择新的报告文件名。");return;}
            Run(()=>WriteReport(activeFile,dest,corpus,true));
        }
    }
    internal async Task WriteReport(string input,string dest,string refFile,bool offerOpen){
                string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"worker","review-worker.exe");
                if(!File.Exists(exe))exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dist","review-worker","review-worker.exe");
                string dir=Path.Combine(Path.GetDirectoryName(dest),".techpaper-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
                string local=Path.Combine(dir,"report.html");cancelling=false;
                try{
                    if(input==null){
                        input=Path.Combine(dir,"粘贴文本.docx");
                        await Task.Run(()=>doc.Export(input,new HashSet<int>(),false));
                    }
                    using(var proc=new Process{StartInfo=new ProcessStartInfo(exe,Arg(input)+" "+Arg(local)+(refFile==null?"":" --corpus "+Arg(refFile))){
                        UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8}}){
                        proc.Start();worker=proc;reportAction.Text="取消分析";reportAction.Enabled=true;status.Text="正在本机读取文字与图像…";string line,last="";
                        while((line=await proc.StandardOutput.ReadLineAsync())!=null){last=line;status.Text=line;}
                        await Task.Run(()=>proc.WaitForExit());
                        if(cancelling){status.Text="已取消，未导出报告。";return;}
                        if(proc.ExitCode!=0)throw new IOException(last.Length>0?last:"分析进程中止。");
                    }
                    File.Move(local,dest);status.Text="已导出 · "+dest;
                    if(offerOpen && MessageBox.Show(this,"报告已保存。现在打开本地 HTML？","离线审阅",MessageBoxButtons.YesNo)==DialogResult.Yes)Process.Start(new ProcessStartInfo(dest){UseShellExecute=true});
                }finally{worker=null;reportAction.Text="导出高亮报告";foreach(var f in Directory.EnumerateFiles(dir))File.Delete(f);Directory.Delete(dir);}
    }
}
}
