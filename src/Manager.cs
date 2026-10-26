// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
// G HUB installed offline launcher. Build for .NET Framework 4.8 x64.
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
namespace GHubLocal {
    static class Store {
        public static string Root=Path.GetDirectoryName(typeof(Store).Assembly.Location);
        public static string App=Path.Combine(Root,"App");
        public static string LocalFolder {
            get{
                return Path.Combine(Root,@"Data\Profile\Local\LGHUB");
            }
        }
        public static string SystemFolder {
            get{
                return Path.Combine(Root,@"Data\System\LGHUB");
            }
        }
        public static string AliasApp {
            get{
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"LGHUB");
            }
        }
        public static bool AppImage(string path){
            return Within(path,App)||Within(path,AliasApp);
        }
        public static void ConfigureRoot(string root){
            Root=Path.GetFullPath(root);
            App=Path.Combine(Root,"App");
            Data=Path.Combine(Root,@"Data\Control");
            Logs=Path.Combine(Data,"Logs");
            Asar=Path.Combine(App,@"resources\app.asar");
            Update=Path.Combine(SystemFolder,"periodic_check.json");
        }
        public static string Data=Path.Combine(Root,@"Data\Control");
        public static string Logs=Path.Combine(Data,"Logs");
        public static string Asar=Path.Combine(App,@"resources\app.asar");
        public static string Update=Path.Combine(SystemFolder,"periodic_check.json");
        public const string OfflineHash="89a2d868a6540f2eb51f53ea28a765728d4e69497eaaf386c97577ae607d38bc";
        public const string OriginalHash="ddea4da4a7d4833819dcf3e89b430889f7ec10a4462677cb526a5df558e6d4a0";
        public static readonly Dictionary<string,string> Native=new Dictionary<string,string> {
            {
                "lghub.exe","3958c51c293992fe9d7b382dacd8652d20f4de30f79f7b5895983a355f81de34"
            }
            , {
                "lghub_agent.exe","4dd78336d58683f64632e34662bf123f95f879766b405b4d8b641c9b2be61cfe"
            }
            , {
                "lghub_updater.exe","cf106445a8039be74af11f140bfd070277837397a2c6deadc0a2bd60c9c65e1d"
            }
        }
        ;
        public static JavaScriptSerializer Json(){
            return new JavaScriptSerializer{
                MaxJsonLength=32*1024*1024
            }
            ;
        }
        public static Dictionary<string,object> Read(string file){
            return Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file,Encoding.UTF8));
        }
        public static void Write(string file,object value){
            NoLinks(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                File.WriteAllText(temp,Json().Serialize(value),new UTF8Encoding(false));
                if(File.Exists(file))File.Replace(temp,file,null);
                else File.Move(temp,file);
            }
            finally{
                if(File.Exists(temp))File.Delete(temp);
            }
        }
        public static void Init(){
            NoLinks(Root);
            NoLinks(App);
            NoLinks(Data);
            var state=Read(Path.Combine(Root,"install-state.json"));
            if(Convert.ToString(state["format"])!="ghub-offline-custom-install-v1"||Convert.ToString(state["userSid"])!=WindowsIdentity.GetCurrent().User.Value)throw new IOException("请先用完整安装包为当前 Windows 用户完成安装。");
            Layout.Verify(Root);
            Directory.CreateDirectory(Logs);
        }
        public static bool Eq(string a,string b){
            return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        }
        public static bool Within(string file,string folder){
            return Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
        }
        public static void NoLinks(string file){
            for(string p=Path.GetFullPath(file); !String.IsNullOrEmpty(p); p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("路径包含链接，已停止操作："+Path.GetFileName(file));
        }
        public static string Hash(string file){
            NoLinks(file);
            using(var hash=new SHA256Cng())using(var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan))return BitConverter.ToString(hash.ComputeHash(s)).Replace("-","").ToLowerInvariant();
        }
        public static string HashText(string s){
            using(var hash=new SHA256Cng())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-","");
        }
        public static bool Admin(){
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static void Log(string stage,object detail){
            try{
                File.AppendAllText(Path.Combine(Logs,"launcher.jsonl"),Json().Serialize(new{
                    time=DateTime.UtcNow.ToString("o"),pid=Process.GetCurrentProcess().Id,stage=stage,detail=detail
                }
                )+"\n",new UTF8Encoding(false));
            }
            catch{
            }
        }
        public static string CheckVersion(){
            Layout.Verify(Root);
            foreach(var pair in Native)if(Hash(Path.Combine(App,pair.Key))!=pair.Value)throw new InvalidOperationException("G HUB 程序版本已变化。为避免覆盖新版本，已停止操作。");
            string h=Hash(Asar);
            if(h!=OfflineHash&&h!=OriginalHash)throw new InvalidOperationException("G HUB 界面文件不属于已核验版本，已停止操作。");
            return h;
        }
        public static void CheckPayloads(){
            if(Hash(Path.Combine(Root,"app.offline.asar"))!=OfflineHash||Hash(Path.Combine(Root,@"backup\app.original.asar"))!=OriginalHash)throw new InvalidOperationException("离线补丁或原版备份校验失败。");
        }
    }
    sealed class Rule {
        public string Name,Program,Group,Addresses,Ports;
        public int Protocol,Direction=2,Action=0,Profiles=Int32.MaxValue;
        public string LocalAddresses="*",LocalPorts="*",InterfaceTypes="All",Service="";
        public bool Enabled=true,NoInterfaces=true;
        public Rule Copy(){
            return (Rule)MemberwiseClone();
        }
    }
    static class Rules {
        public const string Group="G HUB Offline Local 2026-10-24";
        public const string External="0.0.0.0-126.255.255.255,128.0.0.0-255.255.255.255,::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";
        public const string Loopback="127.0.0.0-127.255.255.255,::/127";
        public const string Ports="1-9009,9011-9179,9181-45653,45655-65535";
        public static List<string[]> Roots(){
            return new List<string[]>{
                new[]{
                    Store.App,Store.AliasApp
                }
                ,new[]{
                    Path.Combine(Store.LocalFolder,"integrations"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"LGHUB\integrations")
                }
                ,new[]{
                    Path.Combine(Store.SystemFolder,"depots"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"LGHUB\depots")
                }
            }
            ;
        }
        public static bool Allowed(string p){
            return Path.IsPathRooted(p)&&Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&Roots().Any(pair=>Store.Within(p,pair[0])||Store.Within(p,pair[1]));
        }
        public static List<string> CurrentPrograms(){
            var all=new List<string>();
            foreach(var pair in Roots()){
                var physical=new List<string>();
                if(Directory.Exists(pair[0]))Walk(pair[0],physical);
                foreach(string p in physical){
                    all.Add(p);
                    all.Add(Path.Combine(pair[1],p.Substring(pair[0].Length+1)));
                }
            }
            return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static List<Rule> Plan(){
            var state=Store.Read(Path.Combine(Store.Root,@"backup\applied-state.json"));
            var programs=((IEnumerable)state["programs"]).Cast<object>().Select(x=>Path.GetFullPath(Convert.ToString(x))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToList();
            if(programs.Count<6||programs.Any(p=>!Allowed(p)))throw new InvalidOperationException("防火墙安装记录包含无效程序路径。");
            foreach(string p in programs)if(Store.Within(p,Store.Root))Store.NoLinks(p);
            if(!new HashSet<string>(programs,StringComparer.OrdinalIgnoreCase).SetEquals(CurrentPrograms()))throw new InvalidOperationException("G HUB 可执行文件清单发生变化，请重新审阅后更新离线保护。");
            return Expected(programs);
        }
        public static List<Rule> Expected(IEnumerable<string> programs){
            var all=new List<Rule>();
            foreach(string p in programs){
                string prefix="GHUB-Offline-"+Store.HashText(p.ToLowerInvariant()).Substring(0,16);
                all.Add(new Rule{
                    Name=prefix+"-External",Program=p,Group=Group,Addresses=External,Protocol=256
                }
                );
                all.Add(new Rule{
                    Name=prefix+"-LocalTCP",Program=p,Group=Group,Addresses=Loopback,Protocol=6,Ports=Ports
                }
                );
                all.Add(new Rule{
                    Name=prefix+"-LocalUDP",Program=p,Group=Group,Addresses=Loopback,Protocol=17,Ports="*"
                }
                );
            }
            return all;
        }
        static void Walk(string dir,List<string> output){
            Store.NoLinks(dir);
            foreach(string p in Directory.GetFiles(dir,"*.exe")){
                Store.NoLinks(p);
                output.Add(Path.GetFullPath(p));
            }
            foreach(string p in Directory.GetDirectories(dir))Walk(p,output);
        }
        public static dynamic Policy(){
            return Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2",true));
        }
        public static Rule Capture(dynamic r){
            object interfaces=r.Interfaces;
            int protocol=(int)r.Protocol;
            return new Rule{
                Name=(string)r.Name,Program=(string)r.ApplicationName,Group=(string)r.Grouping,Addresses=(string)r.RemoteAddresses,Ports=protocol==6||protocol==17?(string)r.RemotePorts:null,Protocol=protocol,Direction=(int)r.Direction,Action=(int)r.Action,Profiles=(int)r.Profiles,Enabled=(bool)r.Enabled,LocalAddresses=(string)r.LocalAddresses,LocalPorts=protocol==6||protocol==17?(string)r.LocalPorts:"*",InterfaceTypes=(string)r.InterfaceTypes,Service=(string)r.ServiceName,NoInterfaces=interfaces==null||interfaces==DBNull.Value||(interfaces is Array&&((Array)interfaces).Length==0)
            }
            ;
        }
        public static bool Same(Rule a,Rule b){
            return Store.Eq(a.Name,b.Name)&&Store.Eq(a.Program,b.Program)&&Store.Eq(a.Group,b.Group)&&Store.Eq(a.Addresses,b.Addresses)&&Store.Eq(a.Ports,b.Ports)&&a.Protocol==b.Protocol&&a.Direction==2&&b.Direction==2&&a.Action==0&&b.Action==0&&a.Profiles==Int32.MaxValue&&b.Profiles==Int32.MaxValue&&a.NoInterfaces&&b.NoInterfaces&&Store.Eq(a.LocalAddresses,"*")&&Store.Eq(b.LocalAddresses,"*")&&Store.Eq(a.LocalPorts,"*")&&Store.Eq(b.LocalPorts,"*")&&Store.Eq(a.InterfaceTypes,"All")&&Store.Eq(b.InterfaceTypes,"All")&&String.IsNullOrEmpty(a.Service)&&String.IsNullOrEmpty(b.Service);
        }
        public static List<Rule> Read(dynamic policy,List<Rule> plan){
            var result=new List<Rule>();
            var names=plan.ToDictionary(r=>r.Name,StringComparer.OrdinalIgnoreCase);
            foreach(object o in policy.Rules){
                dynamic r=o;
                string name=(string)r.Name;
                if((string)r.Grouping!=Group&&!names.ContainsKey(name))continue;
                Rule value=Capture(r);
                Rule expected;
                if(!names.TryGetValue(name,out expected)||!Same(value,expected)||result.Any(v=>Store.Eq(v.Name,name)))throw new InvalidOperationException("存在冲突的 G HUB 防火墙规则，已停止修改。");
                result.Add(value);
            }
            return result;
        }
        public static bool ProfilesEnabled(dynamic p){
            return (bool)p.FirewallEnabled[1]&&(bool)p.FirewallEnabled[2]&&(bool)p.FirewallEnabled[4];
        }
        static object Create(Rule s){
            dynamic r=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwRule",true));
            r.Name=s.Name;
            r.Grouping=Group;
            r.Description="User-requested G HUB local mode. Managed by G HUB Offline.exe.";
            r.ApplicationName=s.Program;
            r.Direction=2;
            r.Action=0;
            r.Profiles=Int32.MaxValue;
            r.Protocol=s.Protocol;
            r.RemoteAddresses=s.Addresses;
            r.LocalAddresses="*";
            if(s.Protocol==6||s.Protocol==17){
                r.RemotePorts=s.Ports;
                r.LocalPorts="*";
            }
            r.InterfaceTypes="All";
            r.Enabled=s.Enabled;
            return r;
        }
        public static void Set(dynamic policy,List<Rule> plan,bool enabled){
            Read(policy,plan);
            foreach(Rule s in plan){
                dynamic r=null;
                try{
                    r=policy.Rules.Item(s.Name);
                }
                catch{
                }
                if(r==null){
                    Rule copy=s.Copy();
                    copy.Enabled=enabled;
                    policy.Rules.Add(Create(copy));
                }
                else r.Enabled=enabled;
            }
            List<Rule> after=Read(policy,plan);
            if(after.Count!=plan.Count||after.Any(r=>r.Enabled!=enabled))throw new InvalidOperationException("防火墙规则回读不一致。");
        }
        public static void Restore(dynamic policy,List<Rule> plan,List<Rule> before){
            Read(policy,plan);
            foreach(Rule s in plan){
                Rule prior=before.FirstOrDefault(item=>Store.Eq(item.Name,s.Name));
                dynamic r=null;
                try{
                    r=policy.Rules.Item(s.Name);
                }
                catch{
                }
                if(prior==null){
                    if(r!=null)policy.Rules.Remove(s.Name);
                }
                else if(r==null)policy.Rules.Add(Create(prior));
                else r.Enabled=prior.Enabled;
            }
            List<Rule> after=Read(policy,plan);
            if(after.Count!=before.Count||after.Any(r=>!before.Any(b=>Same(r,b)&&r.Enabled==b.Enabled)))throw new IOException("防火墙回退校验失败。");
        }
    }
    static class Processes {
        [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref int length);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool WaitNamedPipe(string name,uint timeout);
        public static bool AgentPipeReady(){
            if(WaitNamedPipe(@"\\.\pipe\e0c10619-60c9-5414-a4e8-5d9e19d4dedc",1))return true;
            int error=Marshal.GetLastWin32Error();
            return error==121||error==231;
        }
        public static string Image(int pid){
            IntPtr h=OpenProcess(0x1000,false,pid);
            if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try{
                int n=32768;
                var b=new StringBuilder(n);
                if(!QueryFullProcessImageName(h,0,b,ref n))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return b.ToString();
            }
            finally{
                CloseHandle(h);
            }
        }
        public static List<Process> Installed(bool allSessions=false){
            var result=new List<Process>();
            foreach(Process p in new[]{
                "lghub","lghub_agent","lghub_updater","lghub_gl","lghub_system_tray","lghub_gl_crashpad_handler","logi_crashpad_handler","lghub_software_manager","lghub_sso_handler"
            }
            .SelectMany(processName=>Process.GetProcessesByName(processName))){
                try{
                    if(!allSessions&&p.SessionId!=Process.GetCurrentProcess().SessionId){
                        p.Dispose();
                        continue;
                    }
                    string name=p.ProcessName;
                    if(!(name.StartsWith("lghub",StringComparison.OrdinalIgnoreCase)||name=="logi_crashpad_handler")){
                        p.Dispose();
                        continue;
                    }
                    string path=Image(p.Id);
                    if(Store.AppImage(path))result.Add(p);
                    else p.Dispose();
                }
                catch(ArgumentException){
                    p.Dispose();
                }
                catch(InvalidOperationException){
                    p.Dispose();
                }
                catch(System.ComponentModel.Win32Exception){
                    p.Dispose();
                    if(allSessions)throw;
                }
                catch{
                    p.Dispose();
                    throw;
                }
            }
            return result;
        }
        public static bool ForeignAgent(){
            foreach(Process p in Process.GetProcessesByName("lghub_agent"))using(p)if(!Store.AppImage(Image(p.Id)))return true;
            return false;
        }
        public static void CheckService(){
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LGHUBUpdaterService")){
                string value=key==null?"":Convert.ToString(key.GetValue("ImagePath",""));
                bool matches=new[]{
                    Path.Combine(Store.App,"lghub_updater.exe"),Path.Combine(Store.AliasApp,"lghub_updater.exe")
                }
                .Any(expected=>Store.Eq(value.Trim('"',' '),expected)||Store.Eq(value.Trim(),"\""+expected+"\" --run-as-service"));
                if(!matches)throw new InvalidOperationException("G HUB 更新服务路径不匹配，已停止操作。");
            }
        }
        public static Process Start(string file,string arguments,bool offline,string log){
            var info=new ProcessStartInfo(file,arguments){
                UseShellExecute=false,WorkingDirectory=Store.App,CreateNoWindow=true,WindowStyle=file.EndsWith("lghub.exe",StringComparison.OrdinalIgnoreCase)?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden
            }
            ;
            if(offline){
                foreach(string key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())if(Regex.IsMatch(key,@"^(https?|all|ftp|no)_proxy$",RegexOptions.IgnoreCase))info.EnvironmentVariables.Remove(key);
                info.EnvironmentVariables["GHUB_OFFLINE_BENCH_LOG"]=log;
            }
            string temp=Path.Combine(Store.Data,"Temp");
            Directory.CreateDirectory(temp);
            info.EnvironmentVariables["TEMP"]=temp;
            info.EnvironmentVariables["TMP"]=temp;
            return Process.Start(info);
        }
    }
    static class Engine {
        public static object Audit(){
            var time=Stopwatch.StartNew();
            string h=Store.CheckVersion();
            var plan=Rules.Plan();
            dynamic policy=Rules.Policy();
            List<Rule> current=Rules.Read(policy,plan);
            bool profiles=Rules.ProfilesEnabled(policy);
            string mode=h==Store.OfflineHash&&profiles&&current.Count==plan.Count&&current.All(r=>r.Enabled)?"offline":h==Store.OriginalHash&&current.All(r=>!r.Enabled)?"online":"incomplete";
            Processes.CheckService();
            return new{
                timeUtc=DateTime.UtcNow.ToString("o"),mode=mode,installedHash=h,version="2025.9.814156",firewallProfilesEnabled=profiles,expectedRules=plan.Count,presentRules=current.Count,enabledRules=current.Count(r=>r.Enabled),rulePropertiesMatch=true,elapsedMs=time.ElapsedMilliseconds
            }
            ;
        }
        public static string Mode(object audit){
            return Convert.ToString(Store.Json().Deserialize<Dictionary<string,object>>(Store.Json().Serialize(audit))["mode"]);
        }
        public static int Launch(bool offline,int readinessSeconds=25){
            if(Store.Admin())throw new InvalidOperationException("请用普通方式打开此入口。G HUB 设备界面不需要以管理员身份运行。");
            var clock=Stopwatch.StartNew();
            Store.Log("launch-request",new{
                mode=offline?"offline":"online"
            }
            );
            object audit=Audit();
            Store.Write(Path.Combine(Store.Logs,"status.json"),audit);
            if(Mode(audit)!=(offline?"offline":"online"))throw new InvalidOperationException("当前模式与启动方式不一致。请打开「网络设置.exe」切换模式后再启动。");
            if(Processes.ForeignAgent())throw new InvalidOperationException("便携测试版仍在占用设备接口，请先退出测试版启动器。");
            long auditMs=clock.ElapsedMilliseconds;
            bool existing=false,agent=false;
            var running=Processes.Installed();
            try{
                existing=running.Any(p=>p.ProcessName=="lghub"&&p.SessionId==Process.GetCurrentProcess().SessionId);
                agent=running.Any(p=>p.ProcessName=="lghub_agent"&&p.SessionId==Process.GetCurrentProcess().SessionId);
            }
            finally{
                foreach(Process p in running)p.Dispose();
            }
            using(var service=new ServiceController("LGHUBUpdaterService"))if(service.Status!=ServiceControllerStatus.Running)throw new InvalidOperationException("G HUB 本地后台服务尚未运行。请在「网络设置.exe」中应用离线模式修复。");
            if(!agent){
                using(var p=Processes.Start(Path.Combine(Store.App,"lghub_agent.exe"),"",offline,Path.Combine(Store.Logs,"startup-timings.jsonl"))){
                    Store.Log("agent-started",new{
                        pid=p.Id
                    }
                    );
                }
                DateTime until=DateTime.UtcNow.AddSeconds(8);
                while(DateTime.UtcNow<until){
                    if(Processes.AgentPipeReady())break;
                    Thread.Sleep(100);
                }
            }
            long beforeUi=clock.ElapsedMilliseconds;
            string bench=Path.Combine(Store.Logs,"startup-timings.jsonl");
            long oldLength=File.Exists(bench)?new FileInfo(bench).Length:0;
            int pid;
            using(var gui=Processes.Start(Path.Combine(Store.App,"lghub.exe"),offline?"--no-proxy-server --disable-background-networking":"",offline,bench))pid=gui.Id;
            Store.Log("frontend-started",new{
                pid=pid,auditMs=auditMs,beforeUiMs=beforeUi,existing=existing
            }
            );
            if(existing||!offline){
                Store.Write(Path.Combine(Store.Logs,"last-launch.json"),new{
                    success=true,mode=offline?"offline":"online",stage=existing?"existing-window-requested":"online-process-started",pid=pid,elapsedMs=clock.ElapsedMilliseconds,auditMs=auditMs
                }
                );
                return 0;
            }
            DateTime deadline=DateTime.UtcNow.AddSeconds(readinessSeconds);
            while(DateTime.UtcNow<deadline){
                if(File.Exists(bench)){
                    using(var stream=new FileStream(bench,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                        stream.Position=Math.Min(oldLength,stream.Length);
                        using(var reader=new StreamReader(stream,Encoding.UTF8)){
                            foreach(string line in reader.ReadToEnd().Split('\n')){
                                try{
                                    var entry=Store.Json().Deserialize<Dictionary<string,object>>(line);
                                    if(entry!=null&&Convert.ToInt32(entry["pid"])==pid&&Convert.ToString(entry["stage"])=="device-ui-ready"){
                                        var result=new{
                                            success=true,mode="offline",stage="device-ui-ready",pid=pid,elapsedMs=clock.ElapsedMilliseconds,auditMs=auditMs,frontendReadyMs=entry["ms"]
                                        }
                                        ;
                                        Store.Write(Path.Combine(Store.Logs,"last-launch.json"),result);
                                        Store.Log("device-ui-ready",result);
                                        return 0;
                                    }
                                }
                                catch{
                                }
                            }
                        }
                    }
                }
                Thread.Sleep(100);
            }
            Store.Write(Path.Combine(Store.Logs,"last-launch.json"),new{
                success=false,stage="readiness-not-confirmed",pid=pid,elapsedMs=clock.ElapsedMilliseconds
            }
            );
            throw new InvalidOperationException("G HUB 已启动，但尚未确认设备界面就绪。程序和日志已保留，未强制结束后台。日志位于 Data\\Control\\Logs。");
        }
        static void Replace(string source,string destination){
            Store.NoLinks(source);
            Store.NoLinks(destination);
            string stage=destination+".ghub-mode-"+Guid.NewGuid().ToString("N");
            try{
                File.Copy(source,stage);
                File.Replace(stage,destination,null);
            }
            finally{
                if(File.Exists(stage))File.Delete(stage);
            }
        }
        public static void Switch(bool offline){
            if(!Store.Admin())throw new InvalidOperationException("切换模式需要 Windows 管理员授权。");
            Store.CheckVersion();
            Store.CheckPayloads();
            Processes.CheckService();
            var plan=Rules.Plan();
            dynamic policy=Rules.Policy();
            List<Rule> priorRules=Rules.Read(policy,plan);
            if(offline&&!Rules.ProfilesEnabled(policy))throw new InvalidOperationException("Windows 防火墙尚未全部启用，无法确认严格离线保护。");
            if((int)policy.LocalPolicyModifyState!=0)throw new InvalidOperationException("系统策略不允许修改本机防火墙规则。");
            if(Processes.ForeignAgent())throw new InvalidOperationException("请先退出便携测试版，再切换已安装版模式。");
            var active=Processes.Installed(true);
            try{
                if(active.Any(p=>p.SessionId!=0&&p.SessionId!=Process.GetCurrentProcess().SessionId))throw new InvalidOperationException("其他 Windows 用户正在使用 G HUB，请稍后重试。");
            }
            finally{
                foreach(Process p in active)p.Dispose();
            }
            string tx=Path.Combine(Store.Data,"Transactions",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
            Store.NoLinks(tx);
            Directory.CreateDirectory(tx);
            string beforeAsar=Path.Combine(tx,"app.asar"),beforeUpdate=Path.Combine(tx,"periodic_check.json");
            string oldHash=Store.Hash(Store.Asar);
            File.Copy(Store.Asar,beforeAsar);
            bool updateExists=File.Exists(Store.Update);
            if(updateExists){
                Store.NoLinks(Store.Update);
                File.Copy(Store.Update,beforeUpdate);
            }
            Store.Write(Path.Combine(tx,"before.json"),new{
                hash=oldHash,rules=priorRules,offline=offline
            }
            );
            using(var service=new ServiceController("LGHUBUpdaterService")){
                bool wasRunning=service.Status==ServiceControllerStatus.Running;
                try{
                    if(offline)Rules.Set(policy,plan,true);
                    if(service.Status!=ServiceControllerStatus.Stopped){
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));
                    }
                    var list=Processes.Installed(true);
                    try{
                        foreach(Process p in list.OrderBy(p=>p.ProcessName=="lghub_agent"?1:0)){
                            try{
                                if(!p.HasExited){
                                    p.Kill();
                                    if(!p.WaitForExit(8000))throw new IOException("G HUB 进程尚未退出。");
                                }
                            }
                            catch(InvalidOperationException){
                            }
                        }
                    }
                    finally{
                        foreach(Process p in list)p.Dispose();
                    }
                    Replace(Path.Combine(Store.Root,offline?"app.offline.asar":@"backup\app.original.asar"),Store.Asar);
                    if(updateExists){
                        var settings=Store.Read(Store.Update);
                        settings["checkPeriodically"]=false;
                        settings["downloadAutomatically"]=false;
                        string next=Path.Combine(tx,"periodic_check.new.json");
                        Store.Write(next,settings);
                        Replace(next,Store.Update);
                    }
                    if(!offline)Rules.Set(policy,plan,false);
                    if(Store.Hash(Store.Asar)!=(offline?Store.OfflineHash:Store.OriginalHash))throw new IOException("界面文件切换校验失败。");
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));
                    object audit=Audit();
                    if(Mode(audit)!=(offline?"offline":"online"))throw new IOException("模式回读失败。");
                    Store.Write(Path.Combine(tx,"result.json"),new{
                        success=true,mode=offline?"offline":"online",audit=audit
                    }
                    );
                    Store.Write(Path.Combine(Store.Logs,"status.json"),audit);
                    Store.Log("mode-switched",new{
                        mode=offline?"offline":"online"
                    }
                    );
                }
                catch(Exception error){
                    var rollback=new List<string>();
                    try{
                        if(service.Status!=ServiceControllerStatus.Stopped){
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));
                        }
                    }
                    catch(Exception e){
                        rollback.Add("停止服务："+e.Message);
                    }
                    try{
                        Replace(beforeAsar,Store.Asar);
                        if(Store.Hash(Store.Asar)!=oldHash)throw new IOException("hash mismatch");
                    }
                    catch(Exception e){
                        rollback.Add("恢复界面："+e.Message);
                    }
                    if(updateExists)try{
                        Replace(beforeUpdate,Store.Update);
                    }
                    catch(Exception e){
                        rollback.Add("恢复更新设置："+e.Message);
                    }
                    try{
                        Rules.Restore(policy,plan,priorRules);
                    }
                    catch(Exception e){
                        rollback.Add("恢复规则："+e.Message);
                    }
                    if(wasRunning)try{
                        service.Refresh();
                        if(service.Status==ServiceControllerStatus.Stopped)service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));
                    }
                    catch(Exception e){
                        rollback.Add("恢复服务："+e.Message);
                    }
                    Store.Write(Path.Combine(tx,"result.json"),new{
                        success=false,error=error.Message,rollbackErrors=rollback
                    }
                    );
                    throw new InvalidOperationException(error.Message+(rollback.Count==0?"；模式已回退。":"；部分回退未完成："+String.Join("；",rollback)));
                }
            }
        }
    }
    sealed class SettingsWindow:Form {
        Label status;
        Button offline,online,start;
        bool busy;
        public SettingsWindow(){
            Text="G HUB 本地控制与联网设置";
            ClientSize=new Size(590,330);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10);
            Controls.Add(new Label{
                Text="G HUB，默认离线控制",Font=new Font(Font.FontFamily,17,FontStyle.Bold),Location=new Point(24,22),Size=new Size(540,40)
            }
            );
            status=new Label{
                Text="正在读取当前模式…",Location=new Point(26,78),Size=new Size(535,56)
            }
            ;
            Controls.Add(status);
            Controls.Add(new Label{
                Text="程序、离线资源和当前用户配置位于所选安装目录。\n离线模式保留本机设备通信；允许联网会恢复官方界面。\n两种模式均保留关闭自动检查、自动下载的设置。",Location=new Point(26,143),Size=new Size(538,84)
            }
            );
            offline=new Button{
                Text="应用严格离线",Location=new Point(25,253),Size=new Size(172,42)
            }
            ;
            online=new Button{
                Text="允许官方联网…",Location=new Point(208,253),Size=new Size(172,42)
            }
            ;
            start=new Button{
                Text="按当前模式启动",Location=new Point(391,253),Size=new Size(174,42)
            }
            ;
            Controls.AddRange(new Control[]{
                offline,online,start
            }
            );
            offline.Click+=async(s,e)=>await Change(true);
            online.Click+=async(s,e)=>await Change(false);
            start.Click+=async(s,e)=>await LaunchCurrent();
            Shown+=async(s,e)=>await RefreshStatus();
            FormClosing+=(s,e)=>{
                if(busy)e.Cancel=true;
            }
            ;
        }
        async Task RefreshStatus(){
            try{
                var audit=await Task.Run(()=>Engine.Audit());
                string mode=Engine.Mode(audit);
                status.Text=mode=="offline"?"当前：严格离线。日常打开无需管理员权限。":mode=="online"?"当前：允许官方联网。外部服务可能产生网络请求。":"当前保护不完整，请应用严格离线模式。";
            }
            catch(Exception e){
                status.Text=e.Message;
            }
        }
        void Busy(bool value){
            busy=value;
            offline.Enabled=online.Enabled=start.Enabled=!value;
        }
        async Task Change(bool isOffline){
            if(!isOffline&&MessageBox.Show(this,"这会关闭正在运行的 G HUB，恢复官方界面并解除本工具添加的已安装版断网规则。是否允许 G HUB 联网？","允许官方联网",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            Busy(true);
            status.Text="等待 Windows 管理员授权；完成后会回读验证。";
            try{
                await Task.Run(()=>Program.ElevatedSwitch(isOffline));
                await RefreshStatus();
            }
            catch(Exception e){
                MessageBox.Show(this,e.Message,"模式切换未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            finally{
                Busy(false);
            }
            await RefreshStatus();
        }
        async Task LaunchCurrent(){
            Busy(true);
            try{
                string mode=Engine.Mode(await Task.Run(()=>Engine.Audit()));
                if(mode=="incomplete")throw new InvalidOperationException("请先应用严格离线模式。");
                status.Text="正在打开 G HUB…";
                await Task.Run(()=>Engine.Launch(mode=="offline"));
                CloseAfterLaunch();
            }
            catch(Exception e){
                MessageBox.Show(this,e.Message,"启动未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            finally{
                if(!IsDisposed)Busy(false);
            }
            if(!IsDisposed)await RefreshStatus();
        }
        void CloseAfterLaunch(){
            busy=false;
            Close();
        }
    }
    static class Program {
        [STAThread]public static int Main(string[] args){
            bool quiet=args.Contains("--audit")||args.Contains("--verify-launch")||args.Contains("--self-test")||args.Contains("--plan")||args.Contains("--probe-running")||args.Contains("--helper");
            try{
                if(args.Contains("--self-test")){
                    Directory.CreateDirectory(Store.Logs);
                    SelfTest();
                    return 0;
                }
                if(DownloadGuide.BlockDistributionTool(Store.Root,quiet))return 2;
                Store.Init();
                if(args.Contains("--helper")){
                    Helper(args);
                    return 0;
                }
                if(args.Contains("--probe-running")){
                    var running=Processes.Installed();
                    try{
                        Store.Write(Path.Combine(Store.Logs,"running-check.json"),new{
                            success=true,installedProcesses=running.Select(p=>new{
                                pid=p.Id,name=p.ProcessName
                            }
                            ).ToArray(),foreignAgent=Processes.ForeignAgent()
                        }
                        );
                    }
                    finally{
                        foreach(Process p in running)p.Dispose();
                    }
                    return 0;
                }
                if(args.Contains("--audit")){
                    Store.Write(Path.Combine(Store.Logs,"status.json"),Engine.Audit());
                    return 0;
                }
                if(args.Contains("--plan")){
                    Store.CheckPayloads();
                    Store.Write(Path.Combine(Store.Logs,"mode-plan.json"),new{
                        audit=Engine.Audit(),target=Store.App,offlineHash=Store.OfflineHash,onlineHash=Store.OriginalHash,ruleCount=Rules.Plan().Count,automaticCheck=false,automaticDownload=false,changesApplied=false
                    }
                    );
                    return 0;
                }
                if(args.Contains("--self-test")){
                    SelfTest();
                    return 0;
                }
                if(args.Contains("--settings")){
                    if(Store.Admin())throw new InvalidOperationException("请普通双击打开设置，只有切换时才需要管理员授权。");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new SettingsWindow());
                    return 0;
                }
                using(var mutex=new Mutex(false,@"Local\GHUB-Installed-Offline-Quick")){
                    if(!mutex.WaitOne(0))return 0;
                    try{
                        return Engine.Launch(Store.Hash(Store.Asar)==Store.OfflineHash,args.Contains("--verify-launch")?60:25);
                    }
                    finally{
                        mutex.ReleaseMutex();
                    }
                }
            }
            catch(Exception e){
                Store.Log("error",new{
                    type=e.GetType().Name,message=e.Message
                }
                );
                try{
                    Store.Write(Path.Combine(Store.Logs,"last-error.json"),new{
                        time=DateTime.UtcNow.ToString("o"),error=e.Message
                    }
                    );
                }
                catch{
                }
                if(!quiet)MessageBox.Show(e.Message,"G HUB 离线入口",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
        public static void ElevatedSwitch(bool offline){
            string id=Guid.NewGuid().ToString("N");
            string request=Path.Combine(Store.Data,"Requests",id+".json");
            var self=Process.GetCurrentProcess();
            Store.Write(request,new{
                mode=offline?"offline":"online",parent=self.Id,startTicks=self.StartTime.ToUniversalTime().Ticks,sid=WindowsIdentity.GetCurrent().User.Value
            }
            );
            var info=new ProcessStartInfo(typeof(Program).Assembly.Location,"--helper "+id){
                UseShellExecute=true,Verb="runas",WorkingDirectory=Store.Root,WindowStyle=ProcessWindowStyle.Hidden
            }
            ;
            using(Process p=Process.Start(info)){
                if(!p.WaitForExit(120000))throw new IOException("模式切换仍在执行，请保留窗口和日志。");
                string resultPath=Path.Combine(Store.Data,"Requests",id+".result.json");
                if(!File.Exists(resultPath))throw new IOException("管理员操作没有返回结果。");
                var result=Store.Read(resultPath);
                if(!(bool)result["success"])throw new IOException(Convert.ToString(result["error"]));
            }
        }
        static void Helper(string[] args){
            if(args.Length!=2||args[0]!="--helper"||!Regex.IsMatch(args[1],"^[a-f0-9]{32}$"))throw new ArgumentException("无效的切换请求。");
            string id=args[1],result=Path.Combine(Store.Data,"Requests",id+".result.json");
            try{
                if(!Store.Admin())throw new InvalidOperationException("未取得管理员授权。");
                var request=Store.Read(Path.Combine(Store.Data,"Requests",id+".json"));
                if(Convert.ToString(request["sid"])!=WindowsIdentity.GetCurrent().User.Value)throw new InvalidOperationException("请使用当前 Windows 用户的管理员授权。");
                using(var parent=Process.GetProcessById(Convert.ToInt32(request["parent"]))){
                    if(parent.StartTime.ToUniversalTime().Ticks!=Convert.ToInt64(request["startTicks"])||parent.SessionId!=Process.GetCurrentProcess().SessionId||!Store.Eq(Processes.Image(parent.Id),typeof(Program).Assembly.Location))throw new InvalidOperationException("请求来源不匹配。");
                }
                string mode=Convert.ToString(request["mode"]);
                if(mode!="offline"&&mode!="online")throw new ArgumentException("无效模式。");
                using(var mutex=new Mutex(false,@"Global\GHub-Offline-Mode-Switch")){
                    if(!mutex.WaitOne(0))throw new InvalidOperationException("另一个模式切换正在进行。");
                    try{
                        Engine.Switch(mode=="offline");
                    }
                    finally{
                        mutex.ReleaseMutex();
                    }
                }
                Store.Write(result,new{
                    success=true,mode=mode
                }
                );
            }
            catch(Exception e){
                Store.Write(result,new{
                    success=false,error=e.Message
                }
                );
                throw;
            }
        }
        static void SelfTest(){
            var done=new List<string>();
            Action<bool,string> check=(ok,label)=>{
                if(!ok)throw new Exception("测试失败："+label);
                done.Add(label);
            }
            ;
            Rule expected=new Rule{
                Name="test",Program=@"C:\Program Files\LGHUB\lghub.exe",Group=Rules.Group,Addresses=Rules.Loopback,Protocol=6,Ports=Rules.Ports
            }
            ;
            check(Rules.Same(expected,expected),"exact rule");
            var bad=expected.Copy();
            bad.Enabled=false;
            check(Rules.Same(bad,expected),"enabled state handled separately");
            foreach(string kind in new[]{
                "path","name","group","addresses","ports","protocol","direction","action","profiles","interfaces","localAddress","localPort","interfaceType","service"
            }
            ){
                bad=expected.Copy();
                switch(kind){
                    case "path":bad.Program=@"C:\other.exe";
                    break;
                    case "name":bad.Name="other";
                    break;
                    case "group":bad.Group="other";
                    break;
                    case "addresses":bad.Addresses="*";
                    break;
                    case "ports":bad.Ports="80";
                    break;
                    case "protocol":bad.Protocol=17;
                    break;
                    case "direction":bad.Direction=1;
                    break;
                    case "action":bad.Action=1;
                    break;
                    case "profiles":bad.Profiles=1;
                    break;
                    case "interfaces":bad.NoInterfaces=false;
                    break;
                    case "localAddress":bad.LocalAddresses="127.0.0.1";
                    break;
                    case "localPort":bad.LocalPorts="9010";
                    break;
                    case "interfaceType":bad.InterfaceTypes="LAN";
                    break;
                    case "service":bad.Service="other";
                    break;
                }
                check(!Rules.Same(bad,expected),"reject changed "+kind);
            }
            check(!Rules.Allowed(@"C:\Windows\System32\cmd.exe"),"reject unrelated executable");
            check(!Rules.Allowed(Path.Combine(Store.App,"..","other.exe")),"reject traversal");
            check(!Rules.Allowed(Store.App+"-other\\test.exe"),"reject prefix sibling");
            check(Rules.Allowed(Path.Combine(Store.App,"lghub.exe")),"accept app executable");
            Func<int,bool> blocked=port=>Rules.Ports.Split(',').Any(range=>{
                string[] ends=range.Split('-'); int first=Int32.Parse(ends[0]),last=ends.Length==1?first:Int32.Parse(ends[1]); return port>=first&&port<=last;
            }
            );
            foreach(int port in new[]{
                9010,9180,45654
            }
            )check(!blocked(port),"allow local service "+port);
            foreach(int port in new[]{
                80,443,7890,8080,9009,9011,9179,9181,45653,45655,65535
            }
            )check(blocked(port),"block proxy or unrelated port "+port);
            Store.Write(Path.Combine(Store.Logs,"self-test.json"),new{
                success=true,tests=done.Count,cases=done
            }
            );
        }
    }
}
