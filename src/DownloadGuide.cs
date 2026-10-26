// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GHubLocal {
    static class DownloadGuide {
        public static readonly string[] PackageFiles = {
            "开始使用.exe", "开始使用.exe.config", "LICENSE", "NOTICE.md", "THIRD_PARTY.md",
            "使用说明.md", "LogiG304-Source.zip", "tools/G HUB Offline.exe", "tools/G HUB Offline.exe.config",
            "tools/网络设置.exe", "tools/网络设置.exe.config", "tools/卸载 G HUB 本地版.exe", "tools/卸载 G HUB 本地版.exe.config"
        };
        public const string HelpText = "这个下载包提供本项目的辅助工具和源码，不含官方核心、驱动或设备资源。\r\n\r\n" +
            "首次使用 G304：此包不能完成新电脑首次安装，也不能单独替代 G HUB。需要另行准备完整、配套的安装包。\r\n\r\n" +
            "已有可用本地版：继续从原安装目录启动。这里可以检查下载文件，或选择已有安装目录检查核心、目录映射与服务。不要把 tools 文件夹里的程序直接覆盖到正在使用的版本。\r\n\r\n" +
            "开发者：包内附有完整对应源码。有关自备资源和构建方法，请阅读源码中的 docs/BUILD.md。\r\n\r\n" +
            "本窗口只做本地读取，不申请管理员权限、不下载资源、不更改服务、权限、联网模式或个人配置。文件校验检查与随包清单是否一致，不是官方签名或来源认证。";

        public static string LocalFolder(string path) {
            if(String.IsNullOrWhiteSpace(path)||!Regex.IsMatch(path,@"^[A-Za-z]:[\\/]"))
                throw new IOException("请选择本机磁盘上的完整文件夹路径，不支持网络共享或相对路径。");
            string full=Path.GetFullPath(path);
            var drive=new DriveInfo(Path.GetPathRoot(full));
            if(drive.DriveType!=DriveType.Fixed&&drive.DriveType!=DriveType.Removable)
                throw new IOException("请选择本机磁盘或移动磁盘，不能检查网络驱动器。");
            SafeFs.NoLinks(full);
            if(!Directory.Exists(full))throw new IOException("所选文件夹不存在。");
            return full;
        }
        static Dictionary<string,object> ReadSmallJson(string root,string relative) {
            string path=SafeFs.Child(root,relative);
            if(!File.Exists(path))throw new IOException("缺少文件："+relative);
            if(new FileInfo(path).Length>128*1024)throw new IOException("文件大小异常："+relative);
            return Store.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
        }
        public static string CheckPackage(string folder) {
            string root=LocalFolder(folder);
            var manifest=ReadSmallJson(root,"package-manifest.json");
            object value;
            foreach(string key in new[]{"containsOfficialCore","containsDrivers","isCompleteInstaller"})
                if(!manifest.TryGetValue(key,out value)||!(value is bool)||(bool)value)
                    throw new IOException("下载包类型声明不正确，请重新下载。");
            if(!manifest.TryGetValue("schemaVersion",out value)||!Object.Equals(value,1)||
               !manifest.TryGetValue("packageKind",out value)||Convert.ToString(value)!="project-tools-only"||
               !manifest.TryGetValue("supportedCoreVersion",out value)||Convert.ToString(value)!="2025.9.814156")
                throw new IOException("不支持此下载包的格式或版本。");
            if(!manifest.TryGetValue("sourceCommit",out value)||!Regex.IsMatch(Convert.ToString(value),"^[a-f0-9]{40}$"))
                throw new IOException("缺少有效的源码版本。");
            string commit=Convert.ToString(value);
            if(!manifest.TryGetValue("files",out value))throw new IOException("文件校验清单缺失。");
            var hashes=value as Dictionary<string,object>;
            if(hashes==null||hashes.Count!=PackageFiles.Length||!new HashSet<string>(hashes.Keys,StringComparer.Ordinal).SetEquals(PackageFiles))
                throw new IOException("下载包文件清单不完整或含有额外文件。");
            var expected=new HashSet<string>(PackageFiles.Concat(new[]{"package-manifest.json","tools"}),StringComparer.Ordinal);
            foreach(string entry in Directory.GetFileSystemEntries(root)) {
                SafeFs.NoLinks(entry);
                if(!expected.Contains(Path.GetFileName(entry)))throw new IOException("文件夹里有额外内容，请解压到新的空文件夹再校验。");
            }
            string tools=SafeFs.Child(root,"tools");
            foreach(string entry in Directory.GetFileSystemEntries(tools)) {
                SafeFs.NoLinks(entry);
                if(!expected.Contains("tools/"+Path.GetFileName(entry)))throw new IOException("tools 文件夹里有额外内容，请重新解压下载包。");
            }
            foreach(string relative in PackageFiles) {
                string path=SafeFs.Child(root,relative);
                if(!File.Exists(path))throw new IOException("缺少文件："+relative+"。请完整解压 ZIP。");
                long length=new FileInfo(path).Length;
                string hash=Convert.ToString(hashes[relative]);
                if(length<=0||length>16*1024*1024||!Regex.IsMatch(hash,"^[a-f0-9]{64}$")||Store.Hash(path)!=hash)
                    throw new IOException("文件校验失败："+relative+"。请从发布页重新下载并解压。");
            }
            return "下载文件校验通过，共 "+PackageFiles.Length+" 个文件。\r\n源码版本："+commit+
                "\r\n\r\n这是辅助工具包，仍需配套安装环境；文件完整不代表已经安装 G HUB。";
        }
        public static bool IsDistributionTool(string root) {
            var folder=new DirectoryInfo(Path.GetFullPath(root));
            return folder.Name.Equals("tools",StringComparison.OrdinalIgnoreCase)&&folder.Parent!=null&&
                File.Exists(Path.Combine(folder.Parent.FullName,"package-manifest.json"))&&
                !File.Exists(Path.Combine(folder.FullName,"install-state.json"));
        }
        public static bool BlockDistributionTool(string root,bool quiet) {
            if(!IsDistributionTool(root))return false;
            if(!quiet)MessageBox.Show("这是下载包中的配套辅助程序，当前目录没有完整安装环境。\r\n\r\n请返回上一级文件夹，双击“开始使用.exe”。已有可用本地版时，请从原安装目录启动。\r\n\r\n本次没有执行启动、模式切换或卸载。","请先打开使用向导",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return true;
        }
        public static bool CheckInstallation(string folder,out string summary) {
            string root=LocalFolder(folder);
            var state=ReadSmallJson(root,"install-state.json");
            object format,user;
            if(!state.TryGetValue("format",out format)||Convert.ToString(format)!="ghub-offline-custom-install-v1")
                throw new IOException("此目录没有本项目的有效安装记录。请选择实际安装目录。");
            if(!state.TryGetValue("userSid",out user)||Convert.ToString(user)!=WindowsIdentity.GetCurrent().User.Value)
                throw new IOException("此安装不属于当前 Windows 用户；未继续检查。");
            bool ok=true;
            var lines=new List<string>();
            Action<string,Action> check=(label,action)=>{
                try { action();lines.Add("通过 · "+label); }
                catch(Exception e) { ok=false;lines.Add("未通过 · "+label+"："+e.Message); }
            };
            check("官方核心与离线界面",()=>{
                var files=Store.Native.ToDictionary(p=>"App/"+p.Key,p=>p.Value);
                files.Add("app.offline.asar",Store.OfflineHash);
                files.Add("backup/app.original.asar",Store.OriginalHash);
                foreach(var pair in files)if(Store.Hash(SafeFs.Child(root,pair.Key))!=pair.Value)throw new IOException("版本或内容不匹配："+pair.Key);
                string active=Store.Hash(SafeFs.Child(root,"App/resources/app.asar"));
                if(active!=Store.OfflineHash&&active!=Store.OriginalHash)throw new IOException("当前界面文件不匹配。");
            });
            check("兼容目录入口",()=>Layout.Verify(root));
            check("后台服务",()=>{
                using(var service=new ServiceController("LGHUBUpdaterService"))
                    if(service.Status!=ServiceControllerStatus.Running)throw new IOException("服务当前未运行。");
            });
            check("设备驱动服务",()=>{
                foreach(string name in GHubSetup.DriverReadiness.Services)
                    using(var driver=new ServiceController(name))if(driver.Status!=ServiceControllerStatus.Running)throw new IOException(name+" 当前未运行。");
            });
            summary=String.Join("\r\n",lines)+"\r\n\r\n检查仅涉及文件、目录入口和服务状态；未验证 G304 实体操作或联网状态。没有读取鼠标配置内容，也没有执行修复。";
            return ok;
        }
        [STAThread]static int Main(string[] args) {
            try {
                if(args.Length==2&&args[0]=="--check-package") { Console.WriteLine(CheckPackage(args[1]));return 0; }
                if(args.Length==2&&args[0]=="--check-installation") { string summary;bool ok=CheckInstallation(args[1],out summary);Console.WriteLine(summary);return ok?0:2; }
                if(args.Length!=0)throw new ArgumentException("不支持的检查参数。");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new DownloadGuideWindow());
                return 0;
            } catch(Exception e) {
                if(args.Length==0)MessageBox.Show(e.Message,"检查未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else Console.WriteLine(e.Message);
                return 2;
            }
        }
    }
    sealed class DownloadGuideWindow:Form {
        readonly TextBox output;
        readonly Button verify,inspect,help;
        bool busy;
        public DownloadGuideWindow() {
            Text="LogiG304 · 开始使用";
            Font=new Font("Microsoft YaHei UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(740,540);
            MinimumSize=new Size(660,480);
            StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(247,249,252);
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var title=new Label { Text="检查下载包，确认已有安装",AutoSize=true,Font=new Font(Font.FontFamily,19,FontStyle.Bold),Margin=new Padding(0,0,0,12) };
            var intro=new Label { Text="辅助工具包不含官方核心和驱动，不能完成新电脑首次安装。\r\n已有可用版本时，继续使用原安装目录中的入口。",AutoSize=true,Margin=new Padding(0,0,0,18) };
            var actions=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,16),WrapContents=true };
            verify=new Button { Text="校验下载文件",AutoSize=true,Padding=new Padding(12,8,12,8),Margin=new Padding(0,0,12,6) };
            inspect=new Button { Text="检查已有安装…",AutoSize=true,Padding=new Padding(12,8,12,8),Margin=new Padding(0,0,12,6) };
            help=new Button { Text="查看使用说明",AutoSize=true,Padding=new Padding(12,8,12,8),Margin=new Padding(0,0,0,6) };
            output=new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.White,Text=DownloadGuide.HelpText };
            actions.Controls.AddRange(new Control[]{verify,inspect,help});
            layout.Controls.Add(title,0,0);layout.Controls.Add(intro,0,1);layout.Controls.Add(actions,0,2);layout.Controls.Add(output,0,3);Controls.Add(layout);
            verify.Click+=async(s,e)=>await Run(()=>DownloadGuide.CheckPackage(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)));
            inspect.Click+=async(s,e)=>{
                using(var picker=new FolderBrowserDialog { Description="选择已经安装好的本地版目录，例如 D:\\Software\\GHub。只检查，不会修改。",ShowNewFolderButton=false }) {
                    if(picker.ShowDialog(this)!=DialogResult.OK)return;
                    string selected=picker.SelectedPath;
                    await Run(()=>{ string summary;DownloadGuide.CheckInstallation(selected,out summary);return summary; });
                }
            };
            help.Click+=(s,e)=>output.Text=DownloadGuide.HelpText;
            FormClosing+=(s,e)=>{ if(busy)e.Cancel=true; };
        }
        async Task Run(Func<string> action) {
            busy=true;verify.Enabled=inspect.Enabled=help.Enabled=false;output.Text="正在进行本地只读检查…";
            try { output.Text=await Task.Run(action); }
            catch(Exception e) { output.Text="检查未通过\r\n\r\n"+e.Message+"\r\n\r\n没有更改安装或配置。"; }
            finally { busy=false;verify.Enabled=inspect.Enabled=help.Enabled=true; }
        }
    }
}
