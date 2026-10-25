using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using GHubLocal;
namespace GHubSetup {
    static class TargetPermissions {
        const AccessControlSections Sections=AccessControlSections.Access|AccessControlSections.Owner;
        public static readonly string[] OwnedTrees={
            "App","Data","Seed","backup","Backups"
        }
        ;
        public static void Capture(Journal j){
            SafeFs.NoLinks(j.Root);
            j.TargetExisted=Directory.Exists(j.Root);
            j.TargetBefore=j.TargetExisted?ParentAccess.Sddl(Directory.GetAccessControl(j.Root,Sections)):null;
            j.TargetCaptured=true;
        }
        public static void MarkPrepared(Journal j){
            j.TargetPermissionsTouched=true;
        }
        static void Inherit(string path,string user){
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"icacls.exe"),Install.Quote(path)+" /reset"){
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
            }
            ;
            using(var p=Process.Start(start)){
                string output=p.StandardOutput.ReadToEnd(),error=p.StandardError.ReadToEnd();
                if(!p.WaitForExit(15000)||p.ExitCode!=0)throw new IOException("恢复安装目录继承权限失败："+output+error);
            }
            var acl=Directory.GetAccessControl(path);
            acl.SetOwner(new SecurityIdentifier(user));
            Directory.SetAccessControl(path,acl);
        }
        static void ReturnCopies(string root,string user){
            SafeFs.NoLinks(root);
            foreach(string sub in Directory.GetDirectories(root))ReturnCopies(sub,user);
            var acl=Directory.GetAccessControl(root);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(user),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(root,acl);
        }
        public static void CheckDetached(Journal j){
            SafeFs.NoLinks(j.Root);
            if(Install.ServiceExists()&&(j.ServiceCreated||Junction.Matches(Store.AliasApp,Path.Combine(j.Root,"App"))))throw new IOException("G HUB 系统服务仍存在，暂未放宽服务路径权限。");
            foreach(var map in Layout.Maps(j.Root)){
                string linked=Junction.Read(map.Alias);
                if(linked!=null&&(String.Equals(linked,j.Root,StringComparison.OrdinalIgnoreCase)||SafeFs.Inside(linked,j.Root)))throw new IOException("G HUB 目录映射尚未恢复，暂未放宽安装目录权限。");
            }
        }
        public static void Restore(Journal j){
            if(!j.TargetCaptured||!j.TargetPermissionsTouched||!Directory.Exists(j.Root))return;
            CheckDetached(j);
            foreach(string name in OwnedTrees){
                string path=Path.Combine(j.Root,name);
                if(Directory.Exists(path))ReturnCopies(path,j.UserSid);
            }
            if(j.TargetExisted){
                if(String.IsNullOrEmpty(j.TargetBefore))throw new IOException("安装目录原权限快照缺失。");
                Directory.SetAccessControl(j.Root,ParentAccess.From(j.TargetBefore));
                if(ParentAccess.Fingerprint(ParentAccess.Sddl(Directory.GetAccessControl(j.Root,Sections)))!=ParentAccess.Fingerprint(j.TargetBefore))throw new IOException("安装目录原权限回读不一致。");
            }
            else Inherit(j.Root,j.UserSid);
            j.TargetPermissionsTouched=false;
        }
    }
    static class InstallPath {
        public static string Resolve(string parent,string folder){
            if(String.IsNullOrWhiteSpace(folder)||folder!=folder.Trim()||folder.IndexOfAny(new[]{
                '\\','/',':'
            }
            )>=0||folder=="."||folder=="..")throw new IOException("软件文件夹名称无效，请使用 GHUB 这样的单个名称。");
            if(String.IsNullOrWhiteSpace(parent)||!System.Text.RegularExpressions.Regex.IsMatch(parent,@"^[A-Za-z]:[\\/]"))throw new IOException("请输入完整的上级目录，例如 D:\\Toolkit\\Software。");
            return SafeFs.Target(Path.Combine(parent,folder));
        }
    }
}
