using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using GHubLocal;
namespace GHubSetup {
    static class FailedRetry {
        public static void Validate(Journal j,string target,string user){
            if(j==null||j.FormatVersion!=4||!Store.Eq(j.Root,target)||j.UserSid!=user|| !(j.Phase=="rolled-back"||j.Phase=="rollback-incomplete"||j.Phase=="retry-archiving")|| !j.DriverInstallAttempted||j.DriverPending||j.DriverExitCode!=0||j.ServiceCreated||j.ServiceExisted||j.Registered||j.RootReady) throw new IOException("该目录不是可自动重试的驱动阶段失败安装。请保留文件并使用恢复入口，或选择空目录。");
            var maps=Layout.Maps(target);
            if(j.Maps==null||j.Maps.Count!=maps.Count||j.Maps.Any(m=>m.Existed||m.Linked||m.OriginalLink!=null||m.Source!=null||!maps.Any(e=>e.Key==m.Key&&Store.Eq(e.Alias,m.Alias)&&Store.Eq(e.Target,m.Target)))) throw new IOException("旧安装含有尚需人工核对的目录迁移记录，未移动文件。");
            ParentAccess.ValidateRecords(j);
            LegacyImport.ValidateRules(j.Rules,target);
        }
        public static Journal Inspect(string target,string user){
            SafeFs.NoLinks(target);
            var j=ParentAccess.Find(target,user);
            Validate(j,target,user);
            if(Install.ServiceExists())throw new IOException("G HUB 服务仍存在，不能移动失败安装目录。");
            DriverSetup.CheckNotRunning(j);
            foreach(var map in j.Maps)if(Junction.Read(map.Alias)!=null||Directory.Exists(map.Alias)||File.Exists(map.Alias))throw new IOException("G HUB 兼容入口已被占用，未移动失败安装："+map.Alias);
            return j;
        }
        public static string Archive(string target,string user,Action<string> progress){
            var j=Inspect(target,user);
            SafeFs.Scan(target);
            dynamic policy=Rules.Policy();
            var rules=j.Rules;
            if(rules!=null&&Rules.Read(policy,rules).Count>0)throw new IOException("旧安装仍有断网规则。请先点击恢复中断的安装，再重试。");
            string parent=Path.GetDirectoryName(Path.GetFullPath(target));
            string backup=Path.Combine(parent,Path.GetFileName(target)+".failed-"+j.Id);
            SafeFs.NoLinks(backup);
            if(!SafeFs.Inside(target,parent)||!SafeFs.Inside(backup,parent)||Directory.Exists(backup)||File.Exists(backup))throw new IOException("失败现场备份位置不匹配或已存在，未覆盖文件。");
            progress("正在恢复权限，并保留上次失败的全部文件："+backup);
            Install.RestorePermissions(j);
            j.RetryBackup=backup;
            j.Phase="retry-archiving";
            ParentAccess.SaveState(j);
            // Both absolute sibling paths have been validated; keep all contents for diagnosis.
            Directory.Move(target,backup);
            j.Phase="retry-files-preserved";
            ParentAccess.SaveState(j);
            return backup;
        }
    }
}
