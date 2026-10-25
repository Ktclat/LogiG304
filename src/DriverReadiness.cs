// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.ServiceProcess;
using Microsoft.Win32;
using GHubLocal;
namespace GHubSetup {
    sealed class DriverState {
        public string service,imagePath,resolvedPath,error,status;
        public bool filePresent;
        public long length;
    }
    static class DriverReadiness {
        public static readonly string[] Services={
            "logi_joy_vir_hid","logi_joy_bus_enum","logi_joy_xlcore"
        }
        ;
        public static string Resolve(string value,string windows){
            string path=Environment.ExpandEnvironmentVariables(value??"").Trim().Trim('"');
            const string systemRoot=@"\SystemRoot\";
            if(path.StartsWith(systemRoot,StringComparison.OrdinalIgnoreCase))path=Path.Combine(windows,path.Substring(systemRoot.Length));
            if(path.StartsWith(@"\??\",StringComparison.OrdinalIgnoreCase))path=path.Substring(4);
            if(path.StartsWith(@"System32\",StringComparison.OrdinalIgnoreCase))path=Path.Combine(windows,path);
            return path;
        }
        public static List<DriverState> Read(){
            var rows=new List<DriverState>();
            foreach(string service in Services){
                var row=new DriverState{
                    service=service,status="unknown"
                }
                ;
                rows.Add(row);
                try{
                    using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+service)){
                        if(key==null){
                            row.error="service-registration-missing";
                            continue;
                        }
                        row.imagePath=Convert.ToString(key.GetValue("ImagePath","",RegistryValueOptions.DoNotExpandEnvironmentNames));
                    }
                    row.resolvedPath=Resolve(row.imagePath,Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    if(String.IsNullOrEmpty(row.resolvedPath)||!Path.IsPathRooted(row.resolvedPath)){
                        row.error="invalid-image-path";
                        continue;
                    }
                    using(var stream=new FileStream(row.resolvedPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                        row.length=stream.Length;
                        row.filePresent=row.length>0;
                    }
                    using(var controller=new ServiceController(service))row.status=controller.Status.ToString();
                }
                catch(Exception e){
                    row.error=e.GetType().Name+": "+e.Message;
                }
            }
            return rows;
        }
        // PnP may finish replacing driver files after the vendor helper has exited.
        // Require three matching readable snapshots; a transient absence resets the streak.
        public static bool Wait(Func<List<DriverState>> probe,Action pause,int attempts,Action<int,List<DriverState>> report,out List<DriverState> last){
            last=null;
            string previous=null;
            int stable=0;
            for(int i=0; i<attempts; i++){
                var snapshot=probe();
                last=snapshot;
                bool present=snapshot!=null&&snapshot.Count==Services.Length&&Services.All(s=>snapshot.Count(r=>r.service==s&&r.filePresent&&r.length>0&&String.IsNullOrEmpty(r.error))==1);
                string signature=present?String.Join("|",last.OrderBy(r=>r.service).Select(r=>r.service+":"+r.resolvedPath+":"+r.length)):null;
                stable=present?(signature==previous?stable+1:1):0;
                previous=signature;
                report(i,last);
                if(stable>=3)return true;
                if(i+1<attempts)pause();
            }
            return false;
        }
        public static bool Wait(Action<string> progress,out List<DriverState> last){
            return Wait(Read,()=>Thread.Sleep(500),61,(i,rows)=>{
                if(i%4==0)progress("等待 Windows 完成驱动配置并确认文件稳定（"+(i/2)+" 秒）…");
            }
            ,out last);
        }
    }
}
