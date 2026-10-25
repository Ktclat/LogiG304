// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using GHubLocal;
namespace GHubSetup {
    public sealed class ParentPermission {
        public string Path,Before,Planned,After;
        public bool Created;
    }
    static class ParentAccess {
        const AccessControlSections Sections=AccessControlSections.Access|AccessControlSections.Owner;
        static string StateRoot {
            get{
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"GHubOfflineSetup");
            }
        }
        static string Sid(IdentityReference id){
            return ((SecurityIdentifier)id.Translate(typeof(SecurityIdentifier))).Value;
        }
        static bool Volume(string path){
            return Store.Eq(Path.GetFullPath(path).TrimEnd('\\'),Path.GetPathRoot(path).TrimEnd('\\'));
        }
        public static List<string> Ancestors(string target){
            var list=new List<string>();
            for(string p=Path.GetDirectoryName(Path.GetFullPath(target)); !String.IsNullOrEmpty(p); p=Path.GetDirectoryName(p))list.Add(p);
            list.Reverse();
            return list;
        }
        public static string Sddl(DirectorySecurity acl){
            return acl.GetSecurityDescriptorSddlForm(Sections);
        }
        public static DirectorySecurity From(string sddl){
            var acl=new DirectorySecurity();
            acl.SetSecurityDescriptorSddlForm(sddl,Sections);
            return acl;
        }
        public static string Fingerprint(string sddl){
            var acl=From(sddl);
            var scopes=new Dictionary<string,long>();
            foreach(FileSystemAccessRule r in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))){
                var applies=new List<string>();
                if((r.PropagationFlags&PropagationFlags.InheritOnly)==0)applies.Add("self");
                bool descend=(r.PropagationFlags&PropagationFlags.NoPropagateInherit)==0;
                if((r.InheritanceFlags&InheritanceFlags.ObjectInherit)!=0){
                    applies.Add("child-file");
                    if(descend)applies.Add("descendant-file");
                }
                if((r.InheritanceFlags&InheritanceFlags.ContainerInherit)!=0){
                    applies.Add("child-directory");
                    if(descend)applies.Add("descendant-directory");
                }
                foreach(string scope in applies){
                    string key=Sid(r.IdentityReference)+":"+(int)r.AccessControlType+":"+scope;
                    long current;
                    scopes.TryGetValue(key,out current);
                    scopes[key]=current|(long)(uint)SafeFs.ExpandedRights(r.FileSystemRights);
                }
            }
            return Sid(acl.GetOwner(typeof(SecurityIdentifier)))+"|"+acl.AreAccessRulesProtected+"|"+String.Join(";",scopes.OrderBy(p=>p.Key).Select(p=>p.Key+":"+p.Value));
        }
        static FileSystemRights Dangerous(bool volume){
            return FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles|(volume?0:FileSystemRights.Delete);
        }
        public static bool Secure(FileSystemSecurity acl,bool volume){
            if(!SafeFs.Trusted(Sid(acl.GetOwner(typeof(SecurityIdentifier)))))return false;
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!SafeFs.Trusted(Sid(rule.IdentityReference))&&(SafeFs.ExpandedRights(rule.FileSystemRights)&Dangerous(volume))!=0)return false;
            return true;
        }
        public static DirectorySecurity Restricted(DirectorySecurity original,bool volume){
            var acl=new DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            string owner=Sid(original.GetOwner(typeof(SecurityIdentifier)));
            acl.SetOwner(new SecurityIdentifier(SafeFs.Trusted(owner)?owner:"S-1-5-32-544"));
            foreach(FileSystemAccessRule rule in original.GetAccessRules(true,true,typeof(SecurityIdentifier))){
                bool trim=rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!SafeFs.Trusted(Sid(rule.IdentityReference))&&(SafeFs.ExpandedRights(rule.FileSystemRights)&Dangerous(volume))!=0;
                if(!trim){
                    acl.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference,SafeFs.ExpandedRights(rule.FileSystemRights),rule.InheritanceFlags,rule.PropagationFlags,rule.AccessControlType));
                    continue;
                }
                var self=SafeFs.ExpandedRights(rule.FileSystemRights)&~Dangerous(volume);
                if(self!=0)acl.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference,self,InheritanceFlags.None,PropagationFlags.None,rule.AccessControlType));
                if(rule.InheritanceFlags!=InheritanceFlags.None)acl.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference,SafeFs.ExpandedRights(rule.FileSystemRights),rule.InheritanceFlags,rule.PropagationFlags|PropagationFlags.InheritOnly,rule.AccessControlType));
            }
            foreach(string sid in new[]{
                "S-1-5-18","S-1-5-32-544"
            }
            )acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
            if(!Secure(acl,volume))throw new IOException("无法为安装路径生成安全的目录权限。");
            return acl;
        }
        static bool Sensitive(string path){
            string p=Path.GetFullPath(path).TrimEnd('\\');
            string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows),profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var special=new[]{
                windows,Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),profile,Path.GetDirectoryName(profile),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            }
            ;
            return special.Where(x=>!String.IsNullOrEmpty(x)).Any(x=>Store.Eq(p,x.TrimEnd('\\')))||SafeFs.Inside(p,windows);
        }
        public static object Preview(string target){
            SafeFs.NoLinks(target);
            var changes=new List<object>();
            foreach(string p in Ancestors(target)){
                if(File.Exists(p))throw new IOException("父路径被同名文件占用："+p);
                if(!Directory.Exists(p)){
                    changes.Add(new{
                        path=p,action="create"
                    }
                    );
                    continue;
                }
                var acl=Directory.GetAccessControl(p,Sections);
                if(Secure(acl,Volume(p)))continue;
                if(Volume(p))throw new IOException("磁盘根目录权限允许替换系统服务。请选择具有正常 NTFS 权限的磁盘。");
                if(Sensitive(p))throw new IOException("该位置需要改变 Windows 系统或用户主目录的权限。请选择普通软件目录，例如 D:\\Software\\Logitech\\GHub。");
                Restricted(acl,false);
                changes.Add(new{
                    path=p,action="protect-directory-only"
                }
                );
            }
            return new{
                success=true,target=target,parents=changes
            }
            ;
        }
        public static void InitializeState(string user){
            SafeFs.CheckParents(StateRoot);
            SafeFs.NoLinks(StateRoot);
            if(Directory.Exists(StateRoot)&&!Secure(Directory.GetAccessControl(StateRoot,Sections),false))throw new IOException("安装恢复目录的权限异常，已停止操作。");
            SafeFs.Protect(StateRoot,user,false,false);
            string transactions=Path.Combine(StateRoot,"Transactions");
            if(Directory.Exists(transactions)&&!Secure(Directory.GetAccessControl(transactions,Sections),false))throw new IOException("安装事务目录的权限异常。");
            SafeFs.Protect(transactions,user,false,false);
        }
        static string StatePath(Journal j){
            if(!System.Text.RegularExpressions.Regex.IsMatch(j.Id??"","^[a-f0-9]{32}$"))throw new IOException("恢复编号无效。");
            return Path.Combine(StateRoot,"Transactions",j.Id+".json");
        }
        public static void SaveState(Journal j){
            if(j.FormatVersion<2)return;
            string path=StatePath(j);
            SafeFs.CheckParents(path);
            Store.Write(path,j);
        }
        public static Journal Find(string target,string user){
            string dir=Path.Combine(StateRoot,"Transactions");
            if(!Directory.Exists(dir))return null;
            SafeFs.NoLinks(dir);
            SafeFs.CheckParents(Path.Combine(dir,"state.json"));
            var matching=new List<Journal>();
            foreach(string file in Directory.GetFiles(dir,"*.json")){
                SafeFs.NoLinks(file);
                var acl=File.GetAccessControl(file,Sections);
                if(!Secure(acl,false))throw new IOException("恢复记录权限不匹配。");
                Journal j;
                try{
                    j=Store.Json().Deserialize<Journal>(File.ReadAllText(file,System.Text.Encoding.UTF8));
                }
                catch{
                    continue;
                }
                if(j!=null&&j.FormatVersion>=2&&j.FormatVersion<=4&&Store.Eq(j.Root,target)&&j.UserSid==user&&Store.Eq(file,StatePath(j)))matching.Add(j);
            }
            return matching.OrderByDescending(j=>File.GetLastWriteTimeUtc(StatePath(j))).FirstOrDefault();
        }
        public static void Prepare(Journal j,Action save){
            Preview(j.Root);
            foreach(string p in Ancestors(j.Root)){
                if(Volume(p))continue;
                SafeFs.NoLinks(p);
                bool created=!Directory.Exists(p);
                if(created){
                    var pending=new ParentPermission{
                        Path=p,Created=true
                    }
                    ;
                    j.Parents.Add(pending);
                    save();
                    Directory.CreateDirectory(p);
                    pending.Before=Sddl(Directory.GetAccessControl(p,Sections));
                    pending.Planned=Sddl(Restricted(From(pending.Before),false));
                    save();
                    Directory.SetAccessControl(p,From(pending.Planned));
                    pending.After=Sddl(Directory.GetAccessControl(p,Sections));
                    save();
                }
                else{
                    var acl=Directory.GetAccessControl(p,Sections);
                    if(Secure(acl,false))continue;
                    var entry=new ParentPermission{
                        Path=p,Before=Sddl(acl),Planned=Sddl(Restricted(acl,false))
                    }
                    ;
                    j.Parents.Add(entry);
                    save();
                    Directory.SetAccessControl(p,From(entry.Planned));
                    entry.After=Sddl(Directory.GetAccessControl(p,Sections));
                    save();
                }
            }
            SafeFs.CheckParents(j.Root);
        }
        public static void ValidateRecords(Journal j){
            if(j.Parents==null)return;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowed=Ancestors(j.Root);
            foreach(var p in j.Parents)if(!seen.Add(p.Path)||!allowed.Any(x=>Store.Eq(x,p.Path))||Volume(p.Path)||Sensitive(p.Path))throw new IOException("父目录权限恢复范围不匹配。");
        }
        public static void Restore(Journal j){
            ValidateRecords(j);
            if(j.Parents==null)return;
            var errors=new List<string>();
            foreach(var entry in j.Parents.AsEnumerable().Reverse()){
                try{
                    SafeFs.NoLinks(entry.Path);
                    if(!Directory.Exists(entry.Path))continue;
                    if(String.IsNullOrEmpty(entry.Before)){
                        if(entry.Created&&!Directory.EnumerateFileSystemEntries(entry.Path).Any())Directory.Delete(entry.Path,false);
                        continue;
                    }
                    string current=Sddl(Directory.GetAccessControl(entry.Path,Sections));
                    if(Fingerprint(current)==Fingerprint(entry.Before))continue;
                    string expected=entry.After??entry.Planned;
                    if(String.IsNullOrEmpty(expected)||Fingerprint(current)!=Fingerprint(expected))throw new IOException("目录权限已被另外修改，已保留现状。");
                    Directory.SetAccessControl(entry.Path,From(entry.Before));
                    if(Fingerprint(Sddl(Directory.GetAccessControl(entry.Path,Sections)))!=Fingerprint(entry.Before))throw new IOException("原目录权限回读不一致。");
                    if(entry.Created&&!Directory.EnumerateFileSystemEntries(entry.Path).Any())Directory.Delete(entry.Path,false);
                }
                catch(Exception e){
                    errors.Add(entry.Path+"："+e.Message);
                }
            }
            if(errors.Count>0)throw new IOException(String.Join("；",errors));
        }
    }
}
