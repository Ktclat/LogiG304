// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.ServiceProcess;
using GHubLocal;
namespace GHubSetup {
    sealed class Journal {
        public string Id,Root,UserSid,Phase,Error;
        public bool ServiceExisted,ServiceRunning,ServiceCreated;
        public List<FolderMap> Maps;
        public List<Rule> PriorRules;
        public List<Rule> Rules;
        public string Shortcut,OldShortcutBackup;
        public int FormatVersion;
        public bool RootReady,Touched;
        public List<ParentPermission> Parents;
        public bool TargetCaptured,TargetExisted,TargetPermissionsTouched;
        public string TargetBefore;
        public string ImportRoot,ImportJournalId;
        public List<Rule> LiveRules,LegacyRules;
        public List<ParentPermission> LegacyParents;
        public bool Registered,DriverInstallAttempted;
        public int DriverExitCode,DriverPid;
        public long DriverTicks;
        public bool DriverPending;
        public string RetryBackup;
    }
    static class PackageReader {
        public const string ManifestHash="0000000000000000000000000000000000000000000000000000000000000000";
        public static Stream Open(){
            var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip");
            if(stream==null)throw new IOException("此构建仅包含开源工具，不含罗技核心。请阅读仓库构建说明；它不是可直接安装的完整客户端。");
            return stream;
        }
        public static Dictionary<string,object> Manifest(ZipArchive z){
            var e=z.GetEntry("payload-manifest.json");
            if(e==null||e.Length>1024*1024)throw new IOException("资源清单缺失。");
            byte[] b;
            using(var s=e.Open())using(var m=new MemoryStream()){
                s.CopyTo(m);
                b=m.ToArray();
            }
            using(var hash=new System.Security.Cryptography.SHA256Cng())if(BitConverter.ToString(hash.ComputeHash(b)).Replace("-","").ToLowerInvariant()!=ManifestHash)throw new IOException("资源清单校验失败。");
            return Store.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(b));
        }
        public static void Extract(string target,Action<string> progress,Func<bool> cancel){
            using(var stream=Open())using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
                var manifest=Manifest(zip);
                var files=(Dictionary<string,object>)manifest["files"];
                if(zip.Entries.Count!=files.Count+1)throw new IOException("资源包条目不匹配。");
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int done=0;
                foreach(var e in zip.Entries){
                    if(!seen.Add(e.FullName))throw new IOException("资源包路径重复。");
                    if(e.FullName=="payload-manifest.json")continue;
                    if(cancel())throw new OperationCanceledException("安装已取消。");
                    object expected;
                    if(!files.TryGetValue(e.FullName,out expected))throw new IOException("资源包存在未知文件。");
                    string path=SafeFs.Child(target,e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using(var source=e.Open())using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        source.CopyTo(output);
                    }
                    if(Store.Hash(path)!=Convert.ToString(expected))throw new IOException("资源文件校验失败："+e.FullName);
                    if(++done%40==0)progress("正在展开和校验离线资源 "+done+" / "+files.Count);
                }
            }
        }
        public static object Verify(){
            long bytes=0;
            int count=0;
            using(var stream=Open())using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
                var m=Manifest(zip);
                var files=(Dictionary<string,object>)m["files"];
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var e in zip.Entries){
                    if(!seen.Add(e.FullName))throw new IOException("重复文件。");
                    if(e.FullName=="payload-manifest.json")continue;
                    SafeFs.Child(Path.Combine(Path.GetTempPath(),"ghub-check-root"),e.FullName);
                    object value;
                    if(!files.TryGetValue(e.FullName,out value))throw new IOException("未知资源。");
                    using(var h=new System.Security.Cryptography.SHA256Cng())using(var s=e.Open())if(BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant()!=Convert.ToString(value))throw new IOException("资源校验不匹配。");
                    bytes+=e.Length;
                    count++;
                }
                if(count!=files.Count)throw new IOException("资源不完整。");
            }
            return new{
                success=true,files=count,uncompressedBytes=bytes,manifestSha256=ManifestHash
            }
            ;
        }
    }
    static class Install {
        static string RequestDir;
        static int ParentPid;
        static long ParentTicks;
        static string Nonce;
        static string StateFile(Journal j){
            return Path.Combine(j.Root,"installation-journal.json");
        }
        public static void Save(Journal j){
            ParentAccess.SaveState(j);
            if(j.FormatVersion<2||j.RootReady)Store.Write(StateFile(j),j);
        }
        public static void RestorePermissions(Journal j){
            // Restore the parent inheritance source before checking the target's original ACL.
            TargetPermissions.CheckDetached(j);
            ParentAccess.Restore(j);
            TargetPermissions.Restore(j);
        }
        static void Progress(string phase,string text){
            SetupMessages.TryWriteProgress(Path.Combine(RequestDir,"progress.json"),new{
                phase=phase,message=text,time=DateTime.UtcNow.ToString("o")
            }
            );
        }
        static bool Cancelled(){
            if(File.Exists(Path.Combine(RequestDir,"cancel")))return true;
            try{
                using(var p=Process.GetProcessById(ParentPid))return p.HasExited||p.StartTime.ToUniversalTime().Ticks!=ParentTicks;
            }
            catch{
                return true;
            }
        }
        static void ValidateRequest(string path){
            SafeFs.NoLinks(path);
            RequestDir=Path.GetDirectoryName(path);
            string name=Path.GetFileName(RequestDir);
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^GHubOfflineSetup-[a-f0-9]{32}$")||!SafeFs.Inside(RequestDir,Path.GetTempPath()))throw new IOException("安装请求位置无效。");
            var r=SetupMessages.ReadRequired(path);
            Nonce=Convert.ToString(r["id"]);
            if(name!="GHubOfflineSetup-"+Nonce)throw new IOException("安装请求不匹配。");
            ParentPid=Convert.ToInt32(r["parent"]);
            ParentTicks=Convert.ToInt64(r["parentTicks"]);
            if(Convert.ToString(r["userSid"])!=WindowsIdentity.GetCurrent().User.Value)throw new IOException("请使用当前 Windows 用户的管理员授权。");
            using(var p=Process.GetProcessById(ParentPid))if(p.StartTime.ToUniversalTime().Ticks!=ParentTicks||p.SessionId!=Process.GetCurrentProcess().SessionId||!Store.Eq(Processes.Image(p.Id),Assembly.GetExecutingAssembly().Location))throw new IOException("安装请求来源不匹配。");
        }
        public static bool ServiceExists(){
            return ServiceController.GetServices().Any(s=>s.ServiceName=="LGHUBUpdaterService");
        }
        public static void Sc(params string[] args){
            var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe")){
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
            }
            ;
            info.Arguments=String.Join(" ",args.Select(Quote));
            using(var p=Process.Start(info)){
                string output=p.StandardOutput.ReadToEnd(),error=p.StandardError.ReadToEnd();
                if(!p.WaitForExit(15000)||p.ExitCode!=0)throw new IOException("Windows 服务操作失败："+output+error);
            }
        }
        public static string Quote(string value){
            return "\""+value.Replace("\"","\\\"")+"\"";
        }
        static void CheckSource(string root){
            foreach(var m in Layout.Maps(root)){
                if(File.Exists(m.Alias))throw new IOException("G HUB 目录被同名文件占用。");
                if(Directory.Exists(m.Alias))SafeFs.Scan(m.Alias);
            }
            string app=Layout.Maps(root)[0].Alias;
            if(Directory.Exists(app)){
                foreach(var n in Store.Native)if(Store.Hash(Path.Combine(app,n.Key))!=n.Value)throw new IOException("现有 G HUB 版本不匹配，未修改安装。");
                string a=Store.Hash(Path.Combine(app,@"resources\app.asar"));
                if(a!=Store.OfflineHash&&a!=Store.OriginalHash)throw new IOException("现有界面文件不属于已核验版本。");
            }
        }
        public static void Stop(Journal j){
            if(ServiceExists())using(var s=new ServiceController("LGHUBUpdaterService")){
                if(s.Status!=ServiceControllerStatus.Stopped){
                    s.Stop();
                    s.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(20));
                }
            }
            var list=Processes.Installed();
            try{
                foreach(var p in list.OrderBy(p=>p.ProcessName=="lghub_agent"?1:0)){
                    try{
                        if(!p.HasExited){
                            p.Kill();
                            if(!p.WaitForExit(10000))throw new IOException("G HUB 尚未退出。");
                        }
                    }
                    catch(InvalidOperationException){
                    }
                }
            }
            finally{
                foreach(var p in list)p.Dispose();
            }
        }
        static void Start(){
            using(var s=new ServiceController("LGHUBUpdaterService")){
                s.Refresh();
                if(s.Status==ServiceControllerStatus.Stopped)s.Start();
                s.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20));
            }
        }
        static object Expand(object v){
            string s=v as string;
            if(s!=null)return s.Replace("${ROOT}",Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Replace("${PROGRAMDATA}",Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Replace("${LOCALAPPDATA}",Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var d=v as Dictionary<string,object>;
            if(d!=null){
                foreach(string k in d.Keys.ToArray())d[k]=Expand(d[k]);
                return d;
            }
            var a=v as object[];
            if(a!=null){
                for(int i=0; i<a.Length; i++)a[i]=Expand(a[i]);
                return a;
            }
            return v;
        }
        static bool Skip(FolderMap map,string path){
            string rel=path.Substring(map.Alias.Length).TrimStart('\\').Replace('\\','/');
            string first=rel.Split('/')[0];
            if(map.Key=="machine"&&first.Equals("cache",StringComparison.OrdinalIgnoreCase))return true;
            if(map.Key=="telemetry"&&first.Equals("analytics",StringComparison.OrdinalIgnoreCase))return true;
            if(map.Key=="roaming"&&new[]{
                "Cache","Code Cache","GPUCache","DawnCache","DawnWebGPUCache","DawnGraphiteCache","Crashpad","CrashDumps","logs"
            }
            .Contains(first,StringComparer.OrdinalIgnoreCase))return true;
            return false;
        }
        static void Materialize(){
            string seed=Path.Combine(Store.Root,@"Seed\System");
            SafeFs.Copy(seed,Store.SystemFolder,p=>p.EndsWith(".template.json",StringComparison.OrdinalIgnoreCase));
            foreach(string name in new[]{
                "current","next","installation"
            }
            ){
                string source=name=="next"?"current":name;
                var value=(Dictionary<string,object>)Expand(Store.Json().DeserializeObject(File.ReadAllText(Path.Combine(seed,source+".template.json"),Encoding.UTF8)));
                if(name=="installation"){
                    value["extensions"]=new Dictionary<string,object>();
                    value["files"]=((object[])value["files"]).Cast<Dictionary<string,object>>().Where(f=>Layout.Maps(Store.Root).Any(m=>SafeFs.Inside(Convert.ToString(f["path"]),m.Alias))).ToArray();
                }
                Store.Write(Path.Combine(Store.SystemFolder,name+".json"),value);
            }
            Store.Write(Path.Combine(Store.SystemFolder,"periodic_check.json"),new{
                checkPeriodically=false,downloadAutomatically=false,intervalInSeconds=86400
            }
            );
            Directory.CreateDirectory(Store.LocalFolder);
            string localSeed=Path.Combine(Store.Root,@"Seed\Local\LGHUB");
            if(Directory.Exists(localSeed))SafeFs.Copy(localSeed,Store.LocalFolder,null);
        }
        static void SaveShortcut(Journal j){
            j.Shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"G HUB Offline.lnk");
            string expected=Path.Combine(Store.Root,"G HUB Offline.exe");
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            if(File.Exists(j.Shortcut)){
                dynamic prior=shell.CreateShortcut(j.Shortcut);
                string target=(string)prior.TargetPath,args=(string)prior.Arguments;
                bool ours=target.EndsWith(@"\G HUB Offline.exe",StringComparison.OrdinalIgnoreCase)||(target.EndsWith(@"\wscript.exe",StringComparison.OrdinalIgnoreCase)&&args.EndsWith("\\Launch-Offline.vbs\"",StringComparison.OrdinalIgnoreCase));
                if(!ours)j.Shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"G HUB Offline 自选安装.lnk");
                if(File.Exists(j.Shortcut)){
                    j.OldShortcutBackup=Path.Combine(j.Root,@"Backups\desktop-shortcut.lnk");
                    Directory.CreateDirectory(Path.GetDirectoryName(j.OldShortcutBackup));
                    File.Copy(j.Shortcut,j.OldShortcutBackup,false);
                }
            }
            Save(j);
            dynamic link=shell.CreateShortcut(j.Shortcut);
            link.TargetPath=expected;
            link.Arguments="";
            link.WorkingDirectory=j.Root;
            link.IconLocation=Path.Combine(Store.App,"lghub.exe");
            link.Description="G HUB offline local device control";
            link.Save();
        }
        static void BackupAndClean(Journal j){
            string backup=Path.Combine(j.Root,"Backups");
            Directory.CreateDirectory(backup);
            var records=new List<object>();
            foreach(var m in j.Maps){
                if(!m.Existed)continue;
                if(!Store.Eq(m.Backup,m.Alias+".ghub-backup-"+j.Id))throw new IOException("备份路径不匹配。");
                if(!String.IsNullOrEmpty(m.OriginalLink))continue;
                SafeFs.NoLinks(m.Backup);
                foreach(string file in Directory.GetFiles(m.Backup,"*",SearchOption.AllDirectories)){
                    SafeFs.NoLinks(file);
                    string rel=file.Substring(m.Backup.Length+1);
                    bool immutable=m.Key=="app"&&!rel.Equals(@"resources\app.asar",StringComparison.OrdinalIgnoreCase)||m.Key=="machine"&&rel.StartsWith("depots\\",StringComparison.OrdinalIgnoreCase);
                    bool omit=m.Key=="machine"&&rel.StartsWith("cache\\",StringComparison.OrdinalIgnoreCase)||m.Key=="telemetry"||m.Key=="images";
                    if(omit)continue;
                    string hash=Store.Hash(file);
                    if(immutable){
                        string final=Path.Combine(m.Target,rel);
                        if(!File.Exists(final)||Store.Hash(final)!=hash)throw new IOException("原程序或资源与目标不一致，保留旧备份："+m.Key+"/"+rel);
                    }
                    else{
                        if(m.Key=="roaming"&&new[]{
                            "Cache","Code Cache","GPUCache","DawnCache","DawnWebGPUCache","DawnGraphiteCache","Crashpad","CrashDumps","logs"
                        }
                        .Contains(rel.Split('\\')[0],StringComparer.OrdinalIgnoreCase))continue;
                        string to=Path.Combine(backup,"BeforeInstall",m.Key,rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(file,to,true);
                        if(Store.Hash(to)!=hash)throw new IOException("安装前数据备份校验失败。");
                        records.Add(new{
                            kind=m.Key,path=rel,sha256=hash
                        }
                        );
                    }
                }
            }
            Store.Write(Path.Combine(backup,"before-install-files.json"),records);
            if(j.Parents!=null)Store.Write(Path.Combine(backup,"parent-permissions.json"),j.Parents);
            Store.Write(Path.Combine(backup,"target-permissions.json"),new{
                path=j.Root,existed=j.TargetExisted,before=j.TargetBefore
            }
            );
            SafeFs.Protect(backup,j.UserSid,true,true);
            foreach(var m in j.Maps){
                if(!m.Existed)continue;
                if(!Store.Eq(m.Backup,m.Alias+".ghub-backup-"+j.Id))throw new IOException("备份路径越界。");
                if(!String.IsNullOrEmpty(m.OriginalLink))Junction.Remove(m.Backup,m.OriginalLink);
                else SafeFs.DeleteOwnedTree(m.Backup,Path.GetDirectoryName(m.Alias));
            }
            SafeFs.DeleteOwnedTree(Path.Combine(j.Root,"Seed"),j.Root);
        }
        static void Rollback(Journal j){
            var errors=new List<string>();
            try{
                Stop(j);
            }
            catch(Exception e){
                errors.Add("停止 G HUB："+e.Message);
            }
            foreach(var m in j.Maps.AsEnumerable().Reverse()){
                try{
                    if(Junction.Matches(m.Alias,m.Target))Junction.Remove(m.Alias,m.Target);
                    else if(m.Linked)throw new IOException("映射被更改，未删除原目录。");
                    if(m.Existed&&Directory.Exists(m.Backup)){
                        if(Directory.Exists(m.Alias)||File.Exists(m.Alias))throw new IOException("原目录被占用。");
                        Directory.Move(m.Backup,m.Alias);
                    }
                }
                catch(Exception e){
                    errors.Add(m.Key+"："+e.Message);
                }
            }
            if(j.Registered)try{
                Registration.Remove(j.Root);
            }
            catch(Exception e){
                errors.Add("安装登记："+e.Message);
            }
            if(j.Rules!=null&&j.PriorRules!=null)try{
                Rules.Restore(Rules.Policy(),j.Rules,j.PriorRules);
            }
            catch(Exception e){
                errors.Add("规则："+e.Message);
            }
            if(j.ServiceCreated)try{
                Sc("delete","LGHUBUpdaterService");
            }
            catch(Exception e){
                errors.Add("服务："+e.Message);
            }
            else if(j.ServiceRunning)try{
                Start();
            }
            catch(Exception e){
                errors.Add("恢复服务："+e.Message);
            }
            if(!String.IsNullOrEmpty(j.Shortcut))try{
                if(!String.IsNullOrEmpty(j.OldShortcutBackup)&&File.Exists(j.OldShortcutBackup))File.Copy(j.OldShortcutBackup,j.Shortcut,true);
                else if(File.Exists(j.Shortcut)){
                    dynamic sh=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
                    dynamic l=sh.CreateShortcut(j.Shortcut);
                    if(Store.Eq((string)l.TargetPath,Path.Combine(j.Root,"G HUB Offline.exe")))File.Delete(j.Shortcut);
                }
            }
            catch(Exception e){
                errors.Add("快捷方式："+e.Message);
            }
            if(errors.Count==0&&j.FormatVersion>=2){
                j.RootReady=false;
                Save(j);
                try{
                    RestorePermissions(j);
                }
                catch(Exception e){
                    errors.Add("恢复目录权限："+e.Message);
                }
            }
            j.Phase=errors.Count==0?"rolled-back":"rollback-incomplete";
            j.Error=(j.Error??"")+String.Join("；",errors);
            Save(j);
            if(errors.Count>0)throw new IOException(String.Join("；",errors));
        }
        public static void Recover(string request){
            if(!Store.Admin())throw new IOException("需要管理员授权。");
            ValidateRequest(request);
            var input=SetupMessages.ReadRequired(request);
            string target=SafeFs.Target(Convert.ToString(input["target"]));
            var j=ParentAccess.Find(target,WindowsIdentity.GetCurrent().User.Value);
            if(j==null){
                SafeFs.CheckParents(target);
                string state=Path.Combine(target,"installation-journal.json");
                SafeFs.NoLinks(state);
                j=Store.Json().Deserialize<Journal>(File.ReadAllText(state,Encoding.UTF8));
            }
            if(j==null||!Store.Eq(j.Root,target)||j.UserSid!=WindowsIdentity.GetCurrent().User.Value||!System.Text.RegularExpressions.Regex.IsMatch(j.Id??"","^[a-f0-9]{32}$"))throw new IOException("安装恢复记录无效。");
            if(j.Phase=="complete"||j.Phase=="complete-with-backups"||j.Phase=="uninstalled-files-kept")throw new IOException("此安装已完成；该恢复按钮只用于被中断的安装。");
            var expected=Layout.Maps(target);
            if(j.Maps==null||j.Maps.Count!=expected.Count)throw new IOException("恢复映射数量不匹配。");
            foreach(var m in j.Maps){
                var e=expected.SingleOrDefault(x=>x.Key==m.Key);
                if(e==null||!Store.Eq(m.Alias,e.Alias)||!Store.Eq(m.Target,e.Target)||!Store.Eq(m.Backup,m.Alias+".ghub-backup-"+j.Id))throw new IOException("恢复路径不匹配。");
                if(m.Existed&&m.Linked&&!Directory.Exists(m.Backup))throw new IOException("原目录备份已清理，不能执行中断恢复。请保留当前安装。");
            }
            Store.ConfigureRoot(target);
            if(ServiceExists())Processes.CheckService();
            if(j.Rules!=null){
                foreach(var rule in j.Rules)if(rule.Group!=Rules.Group||(!Rules.Allowed(rule.Program)&&!(j.LegacyRules!=null&&j.LegacyRules.Any(old=>Rules.Same(old,rule)))))throw new IOException("恢复规则范围不匹配。");
            }
            LegacyImport.ValidateRules(j.LegacyRules,j.ImportRoot??j.Root);
            LegacyImport.ValidateRecovery(j);
            DriverSetup.CheckNotRunning(j);
            if(!j.ServiceExisted&&ServiceExists()&&Junction.Matches(Store.AliasApp,Store.App)){
                j.ServiceCreated=true;
                Save(j);
            }
            ParentAccess.ValidateRecords(j);
            Progress("recovering","正在恢复安装前的目录、权限和服务…");
            if(j.FormatVersion>=2&&!j.Touched){
                j.RootReady=false;
                Save(j);
                RestorePermissions(j);
                j.Phase="rolled-back";
                Save(j);
            }
            else Rollback(j);
            SetupMessages.WriteRequired(Path.Combine(RequestDir,"result.json"),new{
                success=true,phase=j.Phase,root=target,recovered=true
            }
            );
        }
        public static void Run(string request){
            if(!Store.Admin())throw new IOException("需要管理员授权。");
            ValidateRequest(request);
            using(var payload=PackageReader.Open()){}
            var input=SetupMessages.ReadRequired(request);
            string target=SafeFs.Target(Convert.ToString(input["target"]));
            ParentAccess.Preview(target);
            string retryBackup=null;
            if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())retryBackup=FailedRetry.Archive(target,WindowsIdentity.GetCurrent().User.Value,s=>Progress("retry-backup",s));
            if(ServiceExists())throw new IOException("检测到现有 G HUB 服务。请先卸载旧客户端；本包自带核心，无需预装官方程序。");
            Registration.CheckAvailable(target);
            var previous=LegacyImport.Inspect(input.ContainsKey("importRoot")?Convert.ToString(input["importRoot"]):LegacyImport.Detect(),WindowsIdentity.GetCurrent().User.Value);
            if(Processes.ForeignAgent())throw new IOException("请先退出便携测试版。");
            Store.ConfigureRoot(target);
            if(ServiceExists())Processes.CheckService();
            var j=new Journal{
                Id=Nonce,Root=target,UserSid=WindowsIdentity.GetCurrent().User.Value,Phase="preparing-parents",RetryBackup=retryBackup,Maps=Layout.Maps(target),ServiceExisted=ServiceExists(),FormatVersion=4,Parents=new List<ParentPermission>()
            }
            ;
            LegacyImport.Attach(j,previous);
            LegacyImport.InspectAliases(j);
            if(j.ServiceExisted)using(var s=new ServiceController("LGHUBUpdaterService"))j.ServiceRunning=s.Status==ServiceControllerStatus.Running;
            foreach(var m in j.Maps){
                m.Existed=Directory.Exists(m.Alias);
                m.Backup=m.Alias+".ghub-backup-"+j.Id;
                if(Directory.Exists(m.Backup)||File.Exists(m.Backup))throw new IOException("回退备份目录已存在。");
            }
            if(Process.GetProcessesByName("lghub_agent").Any(p=>p.SessionId!=Process.GetCurrentProcess().SessionId))throw new IOException("其他 Windows 用户正在使用 G HUB，请稍后迁移。");
            bool touched=false;
            ParentAccess.InitializeState(j.UserSid);
            TargetPermissions.Capture(j);
            Save(j);
            try{
                Progress("permissions","正在创建多级目录并备份、设置所需权限…");
                ParentAccess.Prepare(j,()=>Save(j));
                TargetPermissions.MarkPrepared(j);
                Save(j);
                SafeFs.Protect(target,j.UserSid,false,false);
                j.RootReady=true;
                j.Phase="extracting";
                Save(j);
                PackageReader.Extract(target,s=>Progress("extracting",s),Cancelled);
                Materialize();
                foreach(var m in j.Maps)Directory.CreateDirectory(m.Target);
                SafeFs.Protect(Store.Data,j.UserSid,true,true);
                if(Cancelled())throw new OperationCanceledException("安装已取消。");
                Progress("stopping","正在保存迁移状态并停止 G HUB…");
                touched=true;
                j.Touched=true;
                Save(j);
                Stop(j);
                LegacyImport.CopyConfiguration(j,s=>Progress("copying",s));
                var offlineSettings=Store.Read(Store.Update);
                offlineSettings["checkPeriodically"]=false;
                offlineSettings["downloadAutomatically"]=false;
                Store.Write(Store.Update,offlineSettings);
                foreach(var m in j.Maps)SafeFs.Protect(m.Target,j.UserSid,m.Role=="private",true);
                SafeFs.Protect(Store.Data,j.UserSid,true,true);
                SafeFs.Protect(Path.Combine(target,"backup"),j.UserSid,false,true);
                SafeFs.Protect(Store.App,j.UserSid,false,true);
                foreach(string file in Directory.GetFiles(target)){
                    var access=File.GetAccessControl(file);
                    access.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                    File.SetAccessControl(file,access);
                }
                var programs=Rules.CurrentPrograms();
                Store.Write(Path.Combine(target,@"backup\applied-state.json"),new{
                    version="2025.9.814156",programs=programs
                }
                );
                j.LiveRules=Rules.Plan();
                j.Rules=LegacyImport.Combined(j.LiveRules,j.LegacyRules);
                dynamic policy=Rules.Policy();
                if(!Rules.ProfilesEnabled(policy))throw new IOException("请启用 Windows 防火墙后再安装。");
                j.PriorRules=Rules.Read(policy,j.Rules);
                Save(j);
                LegacyImport.ApplyRules(policy,j);
                DriverSetup.InstallVirtual(j,s=>Progress("drivers",s));
                if(Cancelled())throw new OperationCanceledException("安装已取消。");
                foreach(var m in j.Maps){
                    if(Cancelled())throw new OperationCanceledException("安装已取消。");
                    Progress("mapping","正在设置本地目录映射："+m.Key);
                    if(m.Existed)Directory.Move(m.Alias,m.Backup);
                    Directory.CreateDirectory(Path.GetDirectoryName(m.Alias));
                    Junction.Create(m.Alias,m.Target);
                    m.Linked=true;
                    Save(j);
                }
                Store.Write(Path.Combine(target,"install-state.json"),new{
                    format="ghub-offline-custom-install-v1",version="2025.9.814156",userSid=j.UserSid,id=j.Id,root=target,distribution="self-contained-official-core",deviceResources="G304/G305",requiresOfficialPreinstall=false
                }
                );
                if(!j.ServiceExisted){
                    Sc("create","LGHUBUpdaterService","binPath=","\""+Path.Combine(Store.AliasApp,"lghub_updater.exe")+"\" --run-as-service","start=","auto","DisplayName=","LGHUB Updater Service");
                    j.ServiceCreated=true;
                    Save(j);
                }
                Start();
                Store.Init();
                Store.CheckVersion();
                j.Phase="prepared";
                Save(j);
                Progress("prepared","目录迁移完成，正在验证离线设备界面…");
                DateTime until=DateTime.UtcNow.AddSeconds(150);
                bool confirmed=false;
                string confirm=Path.Combine(RequestDir,"confirm.json");
                while(DateTime.UtcNow<until){
                    if(Cancelled())throw new OperationCanceledException("安装已取消。");
                    if(File.Exists(confirm)){
                        var result=SetupMessages.ReadRequired(confirm);
                        if(Convert.ToString(result["id"])!=Nonce)throw new IOException("验证回执不匹配。");
                        if(!(bool)result["success"])throw new IOException("设备界面验证未通过，正在恢复原安装。");
                        confirmed=true;
                        break;
                    }
                    Thread.Sleep(250);
                }
                if(!confirmed)throw new IOException("未收到设备界面验证结果，正在恢复原安装。");
                j.Phase="verified";
                Save(j);
                j.Registered=true;
                Save(j);
                Registration.Register(j.Root);
                SaveShortcut(j);
                Progress("committing","设备界面验证通过，正在保留配置备份并清理旧缓存…");
                try{
                    BackupAndClean(j);
                    j.Phase="complete";
                }
                catch(Exception e){
                    j.Phase="complete-with-backups";
                    j.Error=e.Message;
                }
                Save(j);
                SetupMessages.WriteRequired(Path.Combine(RequestDir,"result.json"),new{
                    success=true,phase=j.Phase,root=target,warning=j.Error,installedDataHere=true,previousFailedFiles=j.RetryBackup
                }
                );
                Progress("complete","安装完成，默认保持离线。");
            }
            catch(Exception error){
                j.Error=error.Message;
                if(j.DriverPending){
                    j.Phase="driver-pending";
                    Save(j);
                    j.Error+="；驱动进程仍在执行，暂未回退。请等待其退出后使用同一安装位置的「恢复中断的安装」。";
                }
                else if(touched){
                    try{
                        Rollback(j);
                    }
                    catch(Exception rollback){
                        j.Error+="；"+rollback.Message;
                    }
                }
                else{
                    j.Phase="failed-before-migration";
                    j.RootReady=false;
                    Save(j);
                    try{
                        RestorePermissions(j);
                    }
                    catch(Exception restore){
                        j.Phase="permissions-restore-incomplete";
                        j.Error+="；"+restore.Message;
                    }
                    Save(j);
                }
                SetupMessages.WriteRequired(Path.Combine(RequestDir,"result.json"),new{
                    success=false,phase=j.Phase,error=j.Error,root=target
                }
                );
                throw;
            }
        }
    }
    sealed class SetupWindow:Form {
        TextBox path,folderText,importText;
        Label status,preview;
        Button browse,install,recover,cancel;
        bool busy,cancelling,closeAfter;
        string requestDir;
        string SelectedTarget(){
            return InstallPath.Resolve(path.Text,folderText.Text);
        }
        void PreviewTarget(){
            try{
                preview.Text="实际安装位置："+SelectedTarget();
            }
            catch{
                preview.Text="请输入上级目录和单独的软件文件夹名称。";
            }
        }
        void CancelInstall(){
            if(!busy)return;
            cancelling=true;
            cancel.Enabled=false;
            status.Text="正在取消；驱动安装需先完成，随后恢复目录与权限…";
            try{
                if(!String.IsNullOrEmpty(requestDir)&&Directory.Exists(requestDir))SetupMessages.WriteRequired(Path.Combine(requestDir,"cancel"),new{
                    cancel=true
                }
                );
            }
            catch(Exception e){
                cancelling=false;
                cancel.Enabled=true;
                status.Text="取消请求暂未送达，请重试："+e.Message;
            }
        }
        public SetupWindow(){
            Text="G HUB 本地离线安装 · 驱动就绪修正版 v3";
            ClientSize=new Size(760,570);
            Font=new Font("Microsoft YaHei UI",10);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;
            Controls.Add(new Label{
                Text="G304，装在你选择的位置",Font=new Font(Font.FontFamily,18,FontStyle.Bold),Location=new Point(28,22),Size=new Size(700,42)
            }
            );
            Controls.Add(new Label{
                Text="已包含官方核心和 G304/G305 资源，无需预装官方 G HUB。\n程序与配置保存在所选目录；Windows 仍需登记服务、驱动和兼容入口。\n默认离线，日常双击安装目录内的 EXE；安装后不要再叠装官方客户端。",Location=new Point(30,78),Size=new Size(700,82)
            }
            );
            string drive=DriveInfo.GetDrives().Where(d=>d.IsReady&&d.DriveType==DriveType.Fixed&&d.DriveFormat=="NTFS"&&!Store.Eq(d.RootDirectory.FullName,Path.GetPathRoot(Environment.SystemDirectory))).Select(d=>d.RootDirectory.FullName).FirstOrDefault()??Path.GetPathRoot(Environment.SystemDirectory);
            string preferred=Path.Combine(drive,@"Toolkit\Software");
            if(!Directory.Exists(preferred))preferred=Path.Combine(drive,"Software");
            Controls.Add(new Label{
                Text="存放位置（上级目录）",Location=new Point(30,174),Size=new Size(650,24)
            }
            );
            path=new TextBox{
                Text=preferred,Location=new Point(30,203),Size=new Size(592,30)
            }
            ;
            browse=new Button{
                Text="选择…",Location=new Point(634,199),Size=new Size(96,35)
            }
            ;
            Controls.Add(path);
            Controls.Add(browse);
            browse.Click+=(s,e)=>{
                using(var dialog=new FolderBrowserDialog{
                    Description="选择上级软件目录",ShowNewFolderButton=true
                }
                )if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;
            }
            ;
            Controls.Add(new Label{
                Text="软件文件夹",Location=new Point(30,250),Size=new Size(115,28)
            }
            );
            folderText=new TextBox{
                Text="GHub",Location=new Point(150,247),Size=new Size(472,30)
            }
            ;
            Controls.Add(folderText);
            preview=new Label{
                Location=new Point(30,286),Size=new Size(700,43),ForeColor=Color.FromArgb(0,77,140)
            }
            ;
            Controls.Add(preview);
            path.TextChanged+=(s,e)=>PreviewTarget();
            folderText.TextChanged+=(s,e)=>PreviewTarget();
            PreviewTarget();
            Controls.Add(new Label{
                Text="旧本地版配置来源（可留空；来源目录不会被覆盖）",Location=new Point(30,337),Size=new Size(700,25)
            }
            );
            importText=new TextBox{
                Text=LegacyImport.Detect(),Location=new Point(30,366),Size=new Size(700,30)
            }
            ;
            Controls.Add(importText);
            status=new Label{
                Text="可重试已记录的驱动阶段失败；原文件会保留到相邻的 .failed 目录。\n服务路径需防替换保护：上级目录本身的移动、改名可能需要管理员。\n上级目录中其他软件和普通文件保留原有读写权限。",Location=new Point(30,411),Size=new Size(700,78)
            }
            ;
            Controls.Add(status);
            install=new Button{
                Text="安装并验证",Location=new Point(548,510),Size=new Size(182,40)
            }
            ;
            recover=new Button{
                Text="恢复中断的安装",Location=new Point(30,510),Size=new Size(170,40)
            }
            ;
            cancel=new Button{
                Text="取消安装",Location=new Point(394,510),Size=new Size(140,40),Enabled=false
            }
            ;
            Controls.Add(install);
            Controls.Add(recover);
            Controls.Add(cancel);
            install.Click+=async(s,e)=>await RunInstall(false);
            recover.Click+=async(s,e)=>await RunInstall(true);
            cancel.Click+=(s,e)=>CancelInstall();
            FormClosing+=(s,e)=>{
                if(busy){
                    e.Cancel=true;
                    closeAfter=true;
                    CancelInstall();
                }
            }
            ;
        }
        async Task RunInstall(bool recovering){
            if(Store.Admin()){
                MessageBox.Show(this,"请普通双击打开安装包，只在安装时确认管理员提示。","G HUB",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            busy=true;
            cancelling=false;
            requestDir=null;
            install.Enabled=recover.Enabled=browse.Enabled=path.Enabled=folderText.Enabled=importText.Enabled=false;
            Process helper=null;
            Exception failure=null;
            try{
                string target=SelectedTarget();
                if(!recovering)ParentAccess.Preview(target);
                if(!recovering&&Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())FailedRetry.Inspect(target,WindowsIdentity.GetCurrent().User.Value);
                var drive=new DriveInfo(Path.GetPathRoot(target));
                if(!recovering&&drive.AvailableFreeSpace<4L*1024*1024*1024)throw new IOException("目标磁盘至少需保留 4 GB 空间供安装及配置备份使用。");
                string id=Guid.NewGuid().ToString("N");
                requestDir=Path.Combine(Path.GetTempPath(),"GHubOfflineSetup-"+id);
                Directory.CreateDirectory(requestDir);
                string request=Path.Combine(requestDir,"request.json");
                using(var self=Process.GetCurrentProcess())SetupMessages.WriteRequired(request,new{
                    id=id,target=target,importRoot=importText.Text.Trim(),parent=self.Id,parentTicks=self.StartTime.ToUniversalTime().Ticks,userSid=WindowsIdentity.GetCurrent().User.Value
                }
                );
                try{
                    Store.Write(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"last-setup.json"),new{
                        requestDirectory=requestDir,target=target,time=DateTime.UtcNow.ToString("o")
                    }
                    );
                }
                catch{
                }
                status.Text="请确认 Windows 管理员提示。将安装自带核心并备份导入配置。";
                var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,(recovering?"--recover-helper ":"--install-helper ")+Install.Quote(request)){
                    UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden
                }
                ;
                helper=Process.Start(start);
                cancel.Enabled=true;
                bool ran=false;
                while(!helper.HasExited){
                    Dictionary<string,object> p;
                    if(SetupMessages.TryReadProgress(Path.Combine(requestDir,"progress.json"),out p)){
                        if(!cancelling)status.Text=Convert.ToString(p["message"]);
                        if(Convert.ToString(p["phase"])=="prepared"&&!ran&&!cancelling){
                            ran=true;
                            bool success=false;
                            try{
                                var info=new ProcessStartInfo(Path.Combine(target,"G HUB Offline.exe"),"--verify-launch"){
                                    UseShellExecute=false,WorkingDirectory=target,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
                                }
                                ;
                                using(var app=Process.Start(info)){
                                    bool finished=await Task.Run(()=>app.WaitForExit(90000));
                                    if(finished&&app.ExitCode==0){
                                        var report=SetupMessages.ReadRequired(Path.Combine(target,@"Data\Control\Logs\last-launch.json"));
                                        success=Convert.ToString(report["stage"])=="device-ui-ready";
                                    }
                                }
                            }
                            catch{
                            }
                            SetupMessages.WriteRequired(Path.Combine(requestDir,"confirm.json"),new{
                                id=id,success=success&&!cancelling
                            }
                            );
                        }
                    }
                    await Task.Delay(300);
                }
                string resultPath=Path.Combine(requestDir,"result.json");
                if(!File.Exists(resultPath))throw new IOException("管理员安装没有返回结果。请保留日志，并用同一位置的恢复按钮检查中断状态。");
                var result=SetupMessages.ReadRequired(resultPath);
                if(!(bool)result["success"])throw new IOException(Convert.ToString(result["error"]));
                status.Text=recovering?"已恢复安装前的目录、权限和服务。":"安装完成并通过设备界面验证。\n日常双击目录内或桌面的 G HUB Offline，无需管理员权限。";
                string warning=result.ContainsKey("warning")?Convert.ToString(result["warning"]):"";
                MessageBox.Show(this,(recovering?"已恢复安装前状态。共享驱动与已展开文件保留。":"已安装到："+target+"\n设备界面已就绪，默认保持严格离线。")+(String.IsNullOrEmpty(warning)?"":"\n旧备份已保留："+warning),"安装完成",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception e){
                failure=e;
            }
            if(failure!=null&&helper!=null&&!helper.HasExited){
                try{
                    SetupMessages.WriteRequired(Path.Combine(requestDir,"cancel"),new{
                        cancel=true
                    }
                    );
                }
                catch{
                }
                cancelling=true;
                cancel.Enabled=false;
                status.Text="安装窗口遇到错误，正在等待管理员进程停止并报告恢复结果…";
                await Task.Run(()=>helper.WaitForExit());
            }
            if(failure!=null){
                status.Text=failure.Message;
                if(!closeAfter)MessageBox.Show(this,failure.Message,"安装未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            if(helper!=null)helper.Dispose();
            busy=false;
            cancel.Enabled=false;
            install.Enabled=recover.Enabled=browse.Enabled=path.Enabled=folderText.Enabled=importText.Enabled=true;
            if(closeAfter)Close();
        }
    }
    static class Setup {
        [STAThread]static int Main(string[] args){
            try{
                if(args.Length==2&&(args[0]=="--install-helper"||args[0]=="--recover-helper")){
                    using(var mutex=new Mutex(false,@"Global\GHub-Offline-Mode-Switch")){
                        if(!mutex.WaitOne(0))throw new IOException("另一个 G HUB 安装或模式切换正在进行。");
                        try{
                            if(args[0]=="--recover-helper")Install.Recover(args[1]);
                            else Install.Run(args[1]);
                        }
                        finally{
                            mutex.ReleaseMutex();
                        }
                    }
                    return 0;
                }
                if(args.Length==3&&args[0]=="--preview-path"){
                    Store.Write(Path.GetFullPath(args[2]),ParentAccess.Preview(SafeFs.Target(args[1])));
                    return 0;
                }
                if(args.Length==2&&args[0]=="--verify-package"){
                    Store.Write(Path.GetFullPath(args[1]),PackageReader.Verify());
                    return 0;
                }
                if(args.Length==2&&args[0]=="--driver-state"){
                    Store.Write(Path.GetFullPath(args[1]),DriverReadiness.Read());
                    return 0;
                }
                if(args.Length==2&&args[0]=="--self-test"){
                    Tests.Run(Path.GetFullPath(args[1]));
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using(var payload=PackageReader.Open()){
                }
                Application.Run(new SetupWindow());
                return 0;
            }
            catch(Exception e){
                if(args.Length>0&&(args[0]=="--install-helper"||args[0]=="--recover-helper")){
                    try{
                        string requestDir=Path.GetDirectoryName(args[1]);
                        if(!File.Exists(Path.Combine(requestDir,"result.json")))SetupMessages.WriteRequired(Path.Combine(requestDir,"result.json"),new{
                            success=false,error=e.Message
                        }
                        );
                    }
                    catch{
                    }
                }
                else if(args.Length==0)MessageBox.Show(e.Message,"G HUB",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                else try{
                    Store.Write(Path.GetFullPath(args.Length==3&&args[0]=="--preview-path"?args[2]:args[1]),new{
                        success=false,error=e.ToString()
                    }
                    );
                }
                catch{
                }
                return 1;
            }
        }
    }
    static class Tests {
        public static void Run(string report){
            string dir=Path.Combine(Path.GetDirectoryName(report),"fixture-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var cases=new List<string>();
            Action<bool,string> check=(value,name)=>{
                if(!value)throw new Exception("Test failed: "+name);
                cases.Add(name);
            }
            ;
            foreach(string bad in new[]{
                "../outside","/outside","C:/outside","a/../../x","a\\b","AUX.txt","file:stream","a//b","a/./b","trailing. ","folder/CON"
            }
            ){
                bool failed=false;
                try{
                    SafeFs.Child(dir,bad);
                }
                catch{
                    failed=true;
                }
                check(failed,"reject archive path "+bad);
            }
            check(SafeFs.Inside(SafeFs.Child(dir,"目录 中文/a.txt"),dir),"unicode archive path");
            check(!SafeFs.Inside(dir+"-sibling\\a",dir),"prefix sibling rejected");
            string target=Path.Combine(dir,"真实 数据"),alias=Path.Combine(dir,"alias");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target,"sentinel"),"unchanged");
            Junction.Create(alias,target);
            check(Junction.Matches(alias,target),"junction target");
            check(File.ReadAllText(Path.Combine(alias,"sentinel"))=="unchanged","junction file access");
            bool wrong=false;
            try{
                Junction.Remove(alias,dir);
            }
            catch{
                wrong=true;
            }
            check(wrong&&Directory.Exists(alias),"reject mismatched junction removal");
            bool rejected=false;
            try{
                SafeFs.NoLinks(alias);
            }
            catch{
                rejected=true;
            }
            check(rejected,"reject link as physical source");
            Junction.Remove(alias,target);
            check(Directory.Exists(target)&&File.ReadAllText(Path.Combine(target,"sentinel"))=="unchanged"&&!Directory.Exists(alias),"removing junction preserves data");
            string proposed=SafeFs.Target(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory),"GHub-Offline"));
            SafeFs.CheckParents(proposed);
            cases.Add("D drive protected target accepted");
            ParentTests.Run(check,dir);
            MessageTests.Run(check,dir);
            StandaloneTests.Run(check,dir);
            V3Tests.Run(check,dir);
            Store.Write(report,new{
                success=true,tests=cases.Count,cases=cases
            }
            );
            SafeFs.DeleteOwnedTree(dir,Path.GetDirectoryName(report));
        }
    }
}
