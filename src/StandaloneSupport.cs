// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using GHubLocal;
namespace GHubSetup {
    static class LegacyImport {
        public static string Detect(){
            string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LGHUB");
            string link=Junction.Read(local),suffix=@"\Data\Profile\Local\LGHUB";
            if(link!=null&&Directory.Exists(link)&&link.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))return link.Substring(0,link.Length-suffix.Length);
            return "";
        }
        public static Journal Inspect(string root,string user){
            if(String.IsNullOrWhiteSpace(root))return null;
            root=SafeFs.Target(root);
            SafeFs.CheckParents(root);
            var prior=ParentAccess.Find(root,user);
            if(prior==null||!(prior.Phase=="complete"||prior.Phase=="complete-with-backups"||prior.Phase=="uninstalled-files-kept"))throw new IOException("配置来源不是当前用户已记录的本地安装目录。");
            var expected=Layout.Maps(root);
            if(prior.Maps==null||prior.Maps.Count!=expected.Count||prior.Maps.Any(m=>!expected.Any(e=>e.Key==m.Key&&Store.Eq(e.Alias,m.Alias)&&Store.Eq(e.Target,m.Target))))throw new IOException("原安装的配置目录记录不匹配。");
            ValidateRules(prior.LiveRules??prior.Rules,root);
            return prior;
        }
        public static void ValidateRules(List<Rule> rules,string root){
            if(rules==null)return;
            var maps=Layout.Maps(root);
            var roots=maps.Where(m=>m.Key=="app").SelectMany(m=>new[]{
                m.Target,m.Alias
            }
            ).Concat(maps.Where(m=>m.Key=="local").SelectMany(m=>new[]{
                Path.Combine(m.Target,"integrations"),Path.Combine(m.Alias,"integrations")
            }
            )).Concat(maps.Where(m=>m.Key=="machine").SelectMany(m=>new[]{
                Path.Combine(m.Target,"depots"),Path.Combine(m.Alias,"depots")
            }
            )).ToArray();
            var programs=rules.Select(r=>r.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach(string p in programs)if(!Path.IsPathRooted(p)||!Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase)||!roots.Any(r=>SafeFs.Inside(p,r)))throw new IOException("旧断网规则包含范围外的程序。");
            var expected=Rules.Expected(programs);
            if(rules.Count!=expected.Count||rules.Select(r=>r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rules.Count||rules.Any(r=>!expected.Any(e=>Rules.Same(r,e))))throw new IOException("旧断网规则与已核验的规则格式不符。");
        }
        public static void Attach(Journal current,Journal previous){
            if(previous==null)return;
            if(Store.Eq(previous.Root,current.Root)||SafeFs.Inside(current.Root,previous.Root)||SafeFs.Inside(previous.Root,current.Root))throw new IOException("新安装目录必须与旧配置目录分开，不能覆盖或嵌套。");
            current.ImportRoot=previous.Root;
            current.ImportJournalId=previous.Id;
            current.LegacyRules=previous.LiveRules??previous.Rules;
            var ancestors=ParentAccess.Ancestors(current.Root);
            current.LegacyParents=(previous.LegacyParents??new List<ParentPermission>()).Concat(previous.Parents??new List<ParentPermission>()).Where(p=>ancestors.Any(a=>Store.Eq(a,p.Path))).GroupBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
        }
        public static void InspectAliases(Journal j){
            var old=String.IsNullOrEmpty(j.ImportRoot)?null:Layout.Maps(j.ImportRoot);
            foreach(var map in j.Maps){
                if(File.Exists(map.Alias))throw new IOException("原 G HUB 目录被同名文件占用。");
                string link=Junction.Read(map.Alias);
                if(link!=null){
                    var known=old==null?null:old.Single(m=>m.Key==map.Key);
                    if(known==null||!Store.Eq(link,known.Target)){
                        if(!Directory.Exists(link)&&!File.Exists(link))throw new IOException("发现卸载遗留的失效目录联接："+map.Alias+"。请先核对该联接来源，使用对应安装的恢复或卸载入口处理。");
                        throw new IOException("发现其他安装的目录映射，请先核实："+map.Alias);
                    }
                    map.OriginalLink=link;
                    map.Source=link;
                    SafeFs.Scan(link);
                }
                else if(Directory.Exists(map.Alias)){
                    SafeFs.Scan(map.Alias);
                    map.Source=map.Alias;
                    if(map.Key=="app"&&Directory.GetFiles(map.Alias,"lghub*.exe").Length>0)throw new IOException("检测到官方客户端核心。请先卸载官方客户端，本安装包不要求预装，也不与其共存。");
                }
                else if(old!=null){
                    string from=old.Single(m=>m.Key==map.Key).Target;
                    if(Directory.Exists(from))map.Source=from;
                }
            }
        }
        public static bool Skip(string key,string root,string path){
            string rel=path.Substring(root.Length).TrimStart('\\').Replace('\\','/'),first=rel.Split('/')[0];
            if(key=="images"||key=="telemetry")return true;
            if(key=="machine")return !new[]{
                "keys.json","groups.json"
            }
            .Contains(first,StringComparer.OrdinalIgnoreCase);
            return new[]{
                "Cache","Code Cache","GPUCache","DawnCache","DawnWebGPUCache","DawnGraphiteCache","Crashpad","CrashDumps","logs","cache"
            }
            .Contains(first,StringComparer.OrdinalIgnoreCase);
        }
        public static void ValidateRecovery(Journal j){
            var prior=String.IsNullOrEmpty(j.ImportRoot)?null:Layout.Maps(j.ImportRoot);
            if(prior!=null&&(Store.Eq(j.ImportRoot,j.Root)||SafeFs.Inside(j.ImportRoot,j.Root)||SafeFs.Inside(j.Root,j.ImportRoot)))throw new IOException("恢复来源与安装目录范围重叠。");
            foreach(var map in j.Maps){
                string known=prior==null?null:prior.Single(m=>m.Key==map.Key).Target;
                if(!String.IsNullOrEmpty(map.OriginalLink)&&!Store.Eq(map.OriginalLink,known))throw new IOException("原目录联接的恢复目标不匹配。");
                if(!String.IsNullOrEmpty(map.Source)&&!Store.Eq(map.Source,map.Alias)&&!Store.Eq(map.Source,known))throw new IOException("配置来源超出记录范围。");
            }
        }
        public static void CopyConfiguration(Journal j,Action<string> progress){
            var manifest=new List<object>();
            foreach(var map in j.Maps.Where(m=>m.Key=="local"||m.Key=="roaming"||m.Key=="machine")){
                if(String.IsNullOrEmpty(map.Source)||!Directory.Exists(map.Source))continue;
                progress("正在备份并导入配置："+map.Key);
                string snapshot=Path.Combine(j.Root,@"Backups\ImportedConfig",map.Key);
                SafeFs.Copy(map.Source,snapshot,p=>Skip(map.Key,map.Source,p));
                foreach(string file in Directory.GetFiles(snapshot,"*",SearchOption.AllDirectories)){
                    string rel=file.Substring(snapshot.Length+1),original=Path.Combine(map.Source,rel);
                    string hash=Store.Hash(file);
                    if(hash!=Store.Hash(original))throw new IOException("配置备份校验失败："+rel);
                    manifest.Add(new{
                        kind=map.Key,path=rel,sha256=hash
                    }
                    );
                }
                SafeFs.Copy(snapshot,map.Target,null);
            }
            Store.Write(Path.Combine(j.Root,@"Backups\imported-config.json"),new{
                source=j.ImportRoot,files=manifest
            }
            );
            SafeFs.Protect(Path.Combine(j.Root,"Backups"),j.UserSid,true,true);
        }
        public static List<Rule> Combined(List<Rule> current,List<Rule> old){
            var result=new List<Rule>(current);
            if(old!=null)foreach(var r in old){
                var existing=result.FirstOrDefault(e=>Store.Eq(e.Name,r.Name));
                if(existing!=null){
                    if(!Rules.Same(existing,r))throw new IOException("新旧断网规则冲突。");
                }
                else result.Add(r);
            }
            return result;
        }
        public static void ApplyRules(dynamic policy,Journal j){
            var names=new HashSet<string>(j.LiveRules.Select(r=>r.Name),StringComparer.OrdinalIgnoreCase);
            foreach(var r in j.PriorRules.Where(r=>!names.Contains(r.Name)))policy.Rules.Remove(r.Name);
            Rules.Set(policy,j.LiveRules,true);
        }
    }
    static class Registration {
        public const string KeyPath=@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GHubLocalOffline";
        public static void CheckAvailable(string root){
            using(var key=Registry.LocalMachine.OpenSubKey(KeyPath)){
                if(key!=null)throw new IOException("已有本地版安装记录："+Convert.ToString(key.GetValue("InstallLocation",""))+"。请先使用它自己的卸载入口解除安装。");
            }
        }
        public static void Register(string root){
            CheckAvailable(root);
            using(var key=Registry.LocalMachine.CreateSubKey(KeyPath)){
                key.SetValue("DisplayName","G HUB 本地离线版（官方核心 / G304）");
                key.SetValue("DisplayVersion","2025.9.814156-local.1");
                key.SetValue("Publisher","Local offline package");
                key.SetValue("InstallLocation",root);
                key.SetValue("DisplayIcon",Path.Combine(root,@"App\lghub.exe"));
                key.SetValue("UninstallString",Install.Quote(Path.Combine(root,"卸载 G HUB 本地版.exe")));
                key.SetValue("NoModify",1,RegistryValueKind.DWord);
                key.SetValue("NoRepair",1,RegistryValueKind.DWord);
                key.SetValue("InstallDate",DateTime.Now.ToString("yyyyMMdd"));
            }
        }
        public static void Remove(string root){
            using(var key=Registry.LocalMachine.OpenSubKey(KeyPath)){
                if(key==null)return;
                if(!Store.Eq(Convert.ToString(key.GetValue("InstallLocation","")),root))throw new IOException("卸载记录属于其他安装，已保留。");
            }
            Registry.LocalMachine.DeleteSubKeyTree(KeyPath,false);
        }
    }
    static class DriverSetup {
        public static void CheckNotRunning(Journal j){
            if(!j.DriverPending)return;
            if(j.DriverPid<=0)throw new IOException("驱动安装状态不完整，请保留安装目录并检查日志。");
            try{
                using(var p=Process.GetProcessById(j.DriverPid))if(!p.HasExited&&p.StartTime.ToUniversalTime().Ticks==j.DriverTicks){
                    string expected=Path.Combine(j.Root,@"Data\System\LGHUB\depots\634218\driver_hid_virtual\virtual_driver_manager.exe");
                    if(!Store.Eq(Processes.Image(p.Id),expected))throw new IOException("驱动进程身份不匹配。");
                    throw new IOException("官方驱动安装进程仍在执行，请等待其退出后再恢复。");
                }
            }
            catch(ArgumentException){
            }
            j.DriverPending=false;
        }
        static bool DriverPresent(string service){
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+service)){
                if(key==null)return false;
                string path=Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ImagePath","")));
                if(path.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase))path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),path.Substring(12));
                return !String.IsNullOrEmpty(path)&&File.Exists(path);
            }
        }
        static void RecordExtension(){
            string file=Path.Combine(Store.SystemFolder,"installation.json");
            var state=Store.Read(file);
            var seed=Store.Read(Path.Combine(Store.Root,@"Seed\System\installation.template.json"));
            var extensions=(Dictionary<string,object>)seed["extensions"];
            state["extensions"]=new Dictionary<string,object>{
                {
                    "driver_hid_virtual",extensions["driver_hid_virtual"]
                }
            }
            ;
            Store.Write(file,state);
        }
        public static void InstallVirtual(Journal j,Action<string> progress){
            string executable=Path.Combine(Store.SystemFolder,@"depots\634218\driver_hid_virtual\virtual_driver_manager.exe");
            if(!File.Exists(executable))throw new IOException("离线包缺少官方虚拟 HID 驱动安装组件。");
            SafeFs.NoLinks(executable);
            progress("正在安装或修复官方虚拟 HID 驱动，请等待 Windows 完成…");
            j.DriverInstallAttempted=true;
            Install.Save(j);
            var info=new ProcessStartInfo(executable,"--install"){
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)
            }
            ;
            var watch=Stopwatch.StartNew();
            using(var process=Process.Start(info)){
                j.DriverPid=process.Id;
                j.DriverTicks=process.StartTime.ToUniversalTime().Ticks;
                j.DriverPending=true;
                Install.Save(j);
                while(!process.WaitForExit(1000)){
                    progress("官方驱动安装中（"+(int)watch.Elapsed.TotalSeconds+" 秒）；此阶段完成后会处理取消请求…");
                    if(watch.Elapsed.TotalMinutes>=3)throw new IOException("官方驱动安装超过 3 分钟，尚未报告完成。已保留进程与安装日志，未强制终止驱动操作。");
                }
                j.DriverPending=false;
                j.DriverExitCode=process.ExitCode;
            }
            List<DriverState> drivers;
            bool present=DriverReadiness.Wait(progress,out drivers);
            Store.Write(Path.Combine(Store.Data,"driver-install.json"),new{
                component="virtual_hid",version="2022.3.0.2",exitCode=j.DriverExitCode,filesPresent=present,drivers=drivers,readinessPolicy="three-stable-snapshots-up-to-30-seconds",restartRequired=j.DriverExitCode==3010,elapsedMs=watch.ElapsedMilliseconds,sharedWindowsComponent=true
            }
            );
            Install.Save(j);
            if(j.DriverExitCode!=0&&j.DriverExitCode!=3010)throw new IOException("官方虚拟 HID 驱动安装返回错误 "+j.DriverExitCode+"。原配置保持在来源目录。");
            if(!present)throw new IOException("Windows 在 30 秒内仍未提供稳定可读的驱动文件。具体路径和错误已写入 Data\\Control\\driver-install.json，请保留日志。");
            RecordExtension();
        }
    }
}
namespace GHubLocal {
    static class NetworkEntry {
        [STAThread]static int Main(string[] args){
            return Program.Main(args.Length==0?new[]{
                "--settings"
            }
            :args);
        }
    }
}
