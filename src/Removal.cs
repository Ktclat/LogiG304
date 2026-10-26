// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Principal;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using GHubLocal;
namespace GHubSetup {
    static class Removal {
        public static void Apply(string root){
            SafeFs.NoLinks(root);
            var j=ParentAccess.Find(root,WindowsIdentity.GetCurrent().User.Value);
            if(j==null||j.FormatVersion!=4||!(j.Phase=="complete"||j.Phase=="complete-with-backups"||j.Phase=="uninstall-incomplete"))throw new IOException("没有找到此目录已完成的本地版安装记录。");
            Store.ConfigureRoot(root);
            var maps=Layout.Maps(root);
            foreach(var m in maps){
                string linked=Junction.Read(m.Alias);
                if(linked!=null&&!Store.Eq(linked,m.Target))throw new IOException("目录入口已被其他安装接管，未执行卸载："+m.Key);
                if(linked==null&&Directory.Exists(m.Alias))throw new IOException("目录入口已变成普通文件夹，未执行卸载："+m.Key);
            }
            if(Install.ServiceExists()){
                if(!Junction.Matches(Store.AliasApp,Store.App))throw new IOException("当前后台服务不属于这个安装目录，未操作其他服务。");
                Processes.CheckService();
            }
            var rules=j.LiveRules??j.Rules;
            if(rules==null)throw new IOException("安装规则记录缺失。");
            LegacyImport.ValidateRules(rules,root);
            dynamic policy=Rules.Policy();
            Rules.Read(policy,rules);
            j.Phase="uninstall-incomplete";
            Install.Save(j);
            Install.Stop(j);
            if(Install.ServiceExists())Install.Sc("delete","LGHUBUpdaterService");
            foreach(var m in maps.AsEnumerable().Reverse())if(Junction.Matches(m.Alias,m.Target))Junction.Remove(m.Alias,m.Target);
            foreach(var rule in Rules.Read(policy,rules))policy.Rules.Remove(rule.Name);
            Registration.Remove(root);
            j.Registered=false;
            if(!String.IsNullOrEmpty(j.Shortcut)&&File.Exists(j.Shortcut)){
                dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
                dynamic shortcut=shell.CreateShortcut(j.Shortcut);
                if(Store.Eq((string)shortcut.TargetPath,Path.Combine(root,"G HUB Offline.exe")))File.Delete(j.Shortcut);
            }
            j.RootReady=false;
            Install.Save(j);
            TargetPermissions.CheckDetached(j);
            var combined=(j.LegacyParents??new List<ParentPermission>()).Concat(j.Parents??new List<ParentPermission>()).GroupBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            ParentAccess.Restore(new Journal{
                Root=root,Parents=combined
            }
            );
            TargetPermissions.Restore(j);
            j.Phase="uninstalled-files-kept";
            Install.Save(j);
        }
        static void Helper(string request){
            if(!Store.Admin())throw new IOException("未获得管理员权限。");
            SafeFs.NoLinks(request);
            string folder=Path.GetDirectoryName(request),name=Path.GetFileName(folder);
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^GHubOfflineRemove-[a-f0-9]{32}$")||!SafeFs.Inside(folder,Path.GetTempPath()))throw new IOException("卸载请求位置不匹配。");
            var input=SetupMessages.ReadRequired(request);
            string root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if(Convert.ToString(input["userSid"])!=WindowsIdentity.GetCurrent().User.Value||!Store.Eq(Convert.ToString(input["root"]),root))throw new IOException("卸载请求用户或目录不匹配。");
            using(var parent=Process.GetProcessById(Convert.ToInt32(input["parent"])))if(parent.StartTime.ToUniversalTime().Ticks!=Convert.ToInt64(input["ticks"])||parent.SessionId!=Process.GetCurrentProcess().SessionId||!Store.Eq(Processes.Image(parent.Id),Assembly.GetExecutingAssembly().Location))throw new IOException("卸载请求来源不匹配。");
            using(var mutex=new Mutex(false,@"Global\GHub-Offline-Mode-Switch")){
                if(!mutex.WaitOne(0))throw new IOException("另一个安装或模式切换正在进行。");
                try{
                    Apply(root);
                }
                finally{
                    mutex.ReleaseMutex();
                }
            }
            SetupMessages.WriteRequired(Path.Combine(folder,"result.json"),new{
                success=true,filesKept=true,root=root
            }
            );
        }
        [STAThread]static int Main(string[] args){
            try{
                if(DownloadGuide.BlockDistributionTool(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),args.Length!=0))return 2;
                if(args.Length==2&&args[0]=="--remove-helper"){
                    Helper(args[1]);
                    return 0;
                }
                if(args.Length!=0)throw new ArgumentException("不支持的卸载参数。");
                if(Store.Admin())throw new IOException("请普通双击打开卸载入口，在操作时确认管理员提示。");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RemoveWindow());
                return 0;
            }
            catch(Exception e){
                if(args.Length==2&&args[0]=="--remove-helper"){
                    try{
                        SetupMessages.WriteRequired(Path.Combine(Path.GetDirectoryName(args[1]),"result.json"),new{
                            success=false,error=e.Message
                        }
                        );
                    }
                    catch{
                    }
                }
                else MessageBox.Show(e.Message,"G HUB 本地版卸载",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
    sealed class RemoveWindow:Form {
        Label status;
        Button action;
        bool busy;
        public RemoveWindow(){
            Text="解除 G HUB 本地版安装";
            ClientSize=new Size(620,260);
            Font=new Font("Microsoft YaHei UI",10);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;
            status=new Label{
                Text="移除此本地版的后台服务、目录映射、断网规则和安装登记。\n保留安装目录中的程序文件、Data 与 Backups，并恢复目录权限。\n共享 Windows 驱动保留；确认不再需要配置后，可自行删除此文件夹。\n\n目录："+Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),Location=new Point(24,24),Size=new Size(570,160)
            }
            ;
            Controls.Add(status);
            action=new Button{
                Text="解除安装并保留文件",Location=new Point(350,200),Size=new Size(244,38)
            }
            ;
            Controls.Add(action);
            action.Click+=async(s,e)=>await Run();
            FormClosing+=(s,e)=>{
                if(busy)e.Cancel=true;
            }
            ;
        }
        async Task Run(){
            busy=true;
            action.Enabled=false;
            try{
                string folder=Path.Combine(Path.GetTempPath(),"GHubOfflineRemove-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string request=Path.Combine(folder,"request.json");
                using(var self=Process.GetCurrentProcess())SetupMessages.WriteRequired(request,new{
                    root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),userSid=WindowsIdentity.GetCurrent().User.Value,parent=self.Id,ticks=self.StartTime.ToUniversalTime().Ticks
                }
                );
                status.Text="请确认管理员提示。正在解除安装，保留配置文件…";
                var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--remove-helper "+Install.Quote(request)){
                    UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden
                }
                ;
                using(var p=Process.Start(info))await Task.Run(()=>p.WaitForExit());
                var result=SetupMessages.ReadRequired(Path.Combine(folder,"result.json"));
                if(!(bool)result["success"])throw new IOException(Convert.ToString(result["error"]));
                status.Text="已解除本地版安装。\n程序文件、Data 和 Backups 已保留，文件夹可以正常处理。";
                action.Text="已完成";
            }
            catch(Exception e){
                status.Text=e.Message;
                action.Enabled=true;
            }
            finally{
                busy=false;
            }
        }
    }
}
