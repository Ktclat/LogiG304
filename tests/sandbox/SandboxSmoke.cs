// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
// Test-only entry point. Never compiled as a production installer entry point.
using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using GHubLocal;
using GHubSetup;

static class SandboxSmoke {
    [StructLayout(LayoutKind.Sequential)] struct SidAndAttributes {public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {
        public int cb;public string reserved,desktop,title;public int x,y,xSize,ySize,xChars,yChars,fill,flags;
        public short show,reserved2;public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr process,thread;public int pid,tid;}
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint a,out IntPtr t);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool CreateRestrictedToken(IntPtr t,uint f,uint c,[In] SidAndAttributes[] s,uint pc,IntPtr p,uint rc,IntPtr r,out IntPtr n);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool ConvertStringSidToSid(string s,out IntPtr p);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr t,int c,ref SidAndAttributes i,int n);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr t,int c,ref IntPtr i,int n);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetKernelObjectSecurity(IntPtr handle,uint info,byte[] sd);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetSecurityDescriptorDacl(IntPtr sd,out bool present,out IntPtr dacl,out bool defaulted);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr s);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool CreateProcessAsUser(IntPtr t,string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string dir,ref StartupInfo si,out ProcessInfo pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr p,uint ms);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr p,out uint exit);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    static void Check(bool ok){if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
    static int LaunchMedium(string exe,string args) {
        IntPtr token=IntPtr.Zero,restricted=IntPtr.Zero,admin=IntPtr.Zero,medium=IntPtr.Zero;
        try {
            Check(OpenProcessToken(Process.GetCurrentProcess().Handle,0xF01FF,out token));
            Check(ConvertStringSidToSid("S-1-5-32-544",out admin));
            // LUA_TOKEN also normalizes owner/DACL, needed by Chromium's child tokens.
            Check(CreateRestrictedToken(token,5,1,new[]{new SidAndAttributes{Sid=admin,Attributes=0}},0,IntPtr.Zero,0,IntPtr.Zero,out restricted));
            var sd=new System.Security.AccessControl.RawSecurityDescriptor("D:(A;;GA;;;SY)(A;;GA;;;"+WindowsIdentity.GetCurrent().User.Value+")");
            byte[] descriptor=new byte[sd.BinaryLength];sd.GetBinaryForm(descriptor,0);
            // Sandbox's elevated token DACL can otherwise exclude the filtered user.
            Check(SetKernelObjectSecurity(restricted,4,descriptor));
            var pinned=GCHandle.Alloc(descriptor,GCHandleType.Pinned);
            try {
                bool present,defaulted;IntPtr dacl;
                Check(GetSecurityDescriptorDacl(pinned.AddrOfPinnedObject(),out present,out dacl,out defaulted));
                Check(SetTokenInformation(restricted,6,ref dacl,IntPtr.Size));
            } finally {pinned.Free();}
            Check(ConvertStringSidToSid("S-1-16-8192",out medium));
            var label=new SidAndAttributes{Sid=medium,Attributes=0x20};
            Check(SetTokenInformation(restricted,25,ref label,Marshal.SizeOf(label)+GetLengthSid(medium)));
            var si=new StartupInfo{cb=Marshal.SizeOf(typeof(StartupInfo)),desktop=@"winsta0\default"};
            ProcessInfo pi;
            Check(CreateProcessAsUser(restricted,exe,new StringBuilder(Install.Quote(exe)+" "+args),IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,Path.GetDirectoryName(exe),ref si,out pi));
            CloseHandle(pi.thread);
            try {
                if(WaitForSingleObject(pi.process,90000)!=0)throw new IOException("Ordinary-token launch timed out");
                uint exit;Check(GetExitCodeProcess(pi.process,out exit));return (int)exit;
            } finally {CloseHandle(pi.process);}
        } finally {
            if(token!=IntPtr.Zero)CloseHandle(token);if(restricted!=IntPtr.Zero)CloseHandle(restricted);
            if(admin!=IntPtr.Zero)LocalFree(admin);if(medium!=IntPtr.Zero)LocalFree(medium);
        }
    }
    [STAThread] static int Main(string[] args) {
        if(Environment.UserName!="WDAGUtilityAccount" || !File.Exists(@"C:\TestInput\sandbox-only.marker") || !Directory.Exists(@"C:\TestResults"))return 90;
        foreach(var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if(adapter.NetworkInterfaceType!=System.Net.NetworkInformation.NetworkInterfaceType.Loopback && adapter.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)return 89;
        if(args.Length!=1)return 91;
        if(args[0]=="--probe-token") {
            IntPtr t,n;bool opened=OpenProcessToken(Process.GetCurrentProcess().Handle,0xA,out t);
            int error=opened?0:Marshal.GetLastWin32Error();bool derived=false;int createError=0;
            if(opened){derived=CreateRestrictedToken(t,1,0,null,0,IntPtr.Zero,0,IntPtr.Zero,out n);createError=derived?0:Marshal.GetLastWin32Error();if(derived)CloseHandle(n);CloseHandle(t);}
            File.WriteAllText(Path.Combine(Path.GetTempPath(),"g304-token-probe.json"),Store.Json().Serialize(new{admin=Store.Admin(),opened=opened,openError=error,derived=derived,createError=createError}));return 0;
        }
        if(args[0]=="--probe-launch") {
            try {int code=LaunchMedium(System.Reflection.Assembly.GetExecutingAssembly().Location,"--probe-token");File.Copy(Path.Combine(Path.GetTempPath(),"g304-token-probe.json"),@"C:\TestResults\token-probe.json",true);return code;}
            catch(Exception e){File.WriteAllText(@"C:\TestResults\token-probe-error.txt",e.ToString());return 1;}
        }
        string target=args[0],requestDir=null;
        try {
            if(Install.ServiceExists()||Directory.Exists(@"C:\Program Files\LGHUB"))throw new IOException("Not a clean guest");
            Environment.SetEnvironmentVariable("ELECTRON_ENABLE_LOGGING","1");
            Environment.SetEnvironmentVariable("ELECTRON_LOG_FILE",@"C:\TestResults\electron-debug.log");
            string id=Guid.NewGuid().ToString("N");
            requestDir=Path.Combine(Path.GetTempPath(),"GHubOfflineSetup-"+id);
            Directory.CreateDirectory(requestDir);
            string request=Path.Combine(requestDir,"request.json");
            using(var p=Process.GetCurrentProcess())SetupMessages.WriteRequired(request,new{id=id,target=target,importRoot="",parent=p.Id,parentTicks=p.StartTime.ToUniversalTime().Ticks,userSid=WindowsIdentity.GetCurrent().User.Value});
            var install=Task.Run(()=>Install.Run(request));
            bool ran=false;
            while(!install.IsCompleted) {
                Dictionary<string,object> progress;
                if(SetupMessages.TryReadProgress(Path.Combine(requestDir,"progress.json"),out progress)) {
                    Store.Write(@"C:\TestResults\progress.json",progress);
                    if(Convert.ToString(progress["phase"])=="prepared"&&!ran) {
                        ran=true;bool success=false;
                        try {
                            int exit=LaunchMedium(Path.Combine(target,"G HUB Offline.exe"),"--verify-launch");
                            var report=SetupMessages.ReadRequired(Path.Combine(target,@"Data\Control\Logs\last-launch.json"));
                            success=exit==0&&Convert.ToString(report["stage"])=="device-ui-ready";
                            Store.Write(@"C:\TestResults\launch-result.json",new{exit=exit,report=report,success=success});
                        } catch(Exception e){Store.Write(@"C:\TestResults\launch-error.json",new{error=e.ToString()});}
                        SetupMessages.WriteRequired(Path.Combine(requestDir,"confirm.json"),new{id=id,success=success});
                    }
                }
                Thread.Sleep(250);
            }
            install.GetAwaiter().GetResult();
            Store.Write(@"C:\TestResults\smoke-result.json",new{success=true,physicalG304Tested=false,networkDisabled=true,target=target});
            return 0;
        } catch(Exception e) {
            Store.Write(@"C:\TestResults\smoke-result.json",new{success=false,error=e.ToString(),physicalG304Tested=false});
            return 1;
        } finally {
            if(requestDir!=null)foreach(string file in Directory.GetFiles(requestDir))File.Copy(file,Path.Combine(@"C:\TestResults",Path.GetFileName(file)),true);
            Store.Write(@"C:\TestResults\driver-state.json",DriverReadiness.Read());
        }
    }
}
