using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
namespace GHubLocal {
    public sealed class FolderMap {
        public string Key,Alias,Target,Role,Backup,OriginalLink,Source;
        public bool Existed,Linked;
    }
    public static class Junction {
        const uint Mount=0xA0000003;
        static readonly IntPtr Invalid=new IntPtr(-1);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateFile(string p,uint a,uint share,IntPtr sec,uint create,uint flags,IntPtr temp);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(IntPtr h,uint ctl,byte[] input,int inputLen,byte[] output,int outputLen,out int used,IntPtr overlap);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
        static IntPtr Open(string p,bool write){
            IntPtr h=CreateFile(p,write?0x40000000u:0u,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(h==Invalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return h;
        }
        public static string Read(string p){
            if(!Directory.Exists(p))return null;
            if((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0)return null;
            IntPtr h=Open(p,false);
            try{
                byte[] b=new byte[16384];
                int used;
                if(!DeviceIoControl(h,0x900a8,null,0,b,b.Length,out used,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if(used<16||BitConverter.ToUInt32(b,0)!=Mount)throw new IOException("不是可管理的目录联接。");
                int offset=BitConverter.ToUInt16(b,8),length=BitConverter.ToUInt16(b,10);
                if(length%2!=0||16+offset+length>used)throw new IOException("目录联接格式无效。");
                string result=Encoding.Unicode.GetString(b,16+offset,length);
                if(!result.StartsWith(@"\??\",StringComparison.Ordinal)||result.StartsWith(@"\??\UNC\",StringComparison.OrdinalIgnoreCase))throw new IOException("仅支持本机目录联接。");
                return Path.GetFullPath(result.Substring(4)).TrimEnd('\\');
            }
            finally{
                CloseHandle(h);
            }
        }
        public static bool Matches(string p,string target){
            return String.Equals(Read(p),Path.GetFullPath(target).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
        }
        public static void Create(string alias,string target){
            SafeFs.NoLinks(target);
            if(!Directory.Exists(target))throw new IOException("目标数据目录不存在。");
            SafeFs.NoLinks(Path.GetDirectoryName(alias));
            if(Directory.Exists(alias)||File.Exists(alias))throw new IOException("兼容目录已存在。");
            Directory.CreateDirectory(alias);
            IntPtr h=Invalid;
            try{
                string substitute=@"\??\"+Path.GetFullPath(target).TrimEnd('\\'),print=Path.GetFullPath(target).TrimEnd('\\');
                byte[] s=Encoding.Unicode.GetBytes(substitute),p=Encoding.Unicode.GetBytes(print);
                byte[] b=new byte[16+s.Length+2+p.Length+2];
                Array.Copy(BitConverter.GetBytes(Mount),0,b,0,4);
                Array.Copy(BitConverter.GetBytes((ushort)(b.Length-8)),0,b,4,2);
                Array.Copy(BitConverter.GetBytes((ushort)s.Length),0,b,10,2);
                Array.Copy(BitConverter.GetBytes((ushort)(s.Length+2)),0,b,12,2);
                Array.Copy(BitConverter.GetBytes((ushort)p.Length),0,b,14,2);
                Array.Copy(s,0,b,16,s.Length);
                Array.Copy(p,0,b,18+s.Length,p.Length);
                h=Open(alias,true);
                int used;
                if(!DeviceIoControl(h,0x900a4,b,b.Length,null,0,out used,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch{
                if(h!=Invalid){
                    CloseHandle(h);
                    h=Invalid;
                }
                if(Directory.Exists(alias)&&Read(alias)==null)Directory.Delete(alias,false);
                throw;
            }
            finally{
                if(h!=Invalid)CloseHandle(h);
            }
            if(!Matches(alias,target))throw new IOException("目录联接回读失败。");
        }
        public static void Remove(string alias,string expected){
            if(!Matches(alias,expected))throw new IOException("目录联接目标发生变化，未执行移除。");
            Directory.Delete(alias,false);
        }
    }
    public static class Layout {
        public static List<FolderMap> Maps(string root){
            string program=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),common=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new List<FolderMap>{
                new FolderMap{
                    Key="app",Alias=Path.Combine(program,"LGHUB"),Target=Path.Combine(root,"App"),Role="system"
                }
                , new FolderMap{
                    Key="machine",Alias=Path.Combine(common,"LGHUB"),Target=Path.Combine(root,@"Data\System\LGHUB"),Role="system"
                }
                , new FolderMap{
                    Key="local",Alias=Path.Combine(local,"LGHUB"),Target=Path.Combine(root,@"Data\Profile\Local\LGHUB"),Role="private"
                }
                , new FolderMap{
                    Key="roaming",Alias=Path.Combine(roaming,"lghub"),Target=Path.Combine(root,@"Data\Profile\Roaming\lghub"),Role="private"
                }
                , new FolderMap{
                    Key="images",Alias=Path.Combine(common,"LGHUBData"),Target=Path.Combine(root,@"Data\ImageCache"),Role="private"
                }
                , new FolderMap{
                    Key="telemetry",Alias=Path.Combine(common,@"Logishrd\LGHUB"),Target=Path.Combine(root,@"Data\SharedLogs"),Role="private"
                }
            }
            ;
        }
        public static void Verify(string root){
            foreach(FolderMap m in Maps(root)){
                SafeFs.NoLinks(m.Target);
                if(!Junction.Matches(m.Alias,m.Target))throw new IOException("G HUB 的目录映射不完整："+m.Key+"。请用安装包恢复或修复。");
            }
        }
    }
    public static class SafeFs {
        public static bool Inside(string path,string root){
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
        }
        public static void NoLinks(string file){
            for(string p=Path.GetFullPath(file); !String.IsNullOrEmpty(p); p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("路径含链接，操作已停止："+Path.GetFileName(file));
        }
        public static string Child(string root,string rel){
            if(String.IsNullOrEmpty(rel)||Path.IsPathRooted(rel)||rel.IndexOf(':')>=0||rel.IndexOf('\\')>=0)throw new IOException("包内路径无效。");
            foreach(string part in rel.Split('/'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".")||part.EndsWith(" ")||Regex.IsMatch(part,@"^(CON|NUL|AUX|PRN|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase))throw new IOException("包内路径无效。");
            string p=Path.GetFullPath(Path.Combine(root,rel.Replace('/','\\')));
            if(!Inside(p,root))throw new IOException("包内路径越界。");
            NoLinks(p);
            return p;
        }
        public static string Target(string requested){
            if(String.IsNullOrWhiteSpace(requested)||!Regex.IsMatch(requested,@"^[A-Za-z]:[\\/]"))throw new IOException("请输入完整的本机安装路径，例如 D:\\Software\\Logitech\\GHub。");
            Child(requested.Substring(0,3),requested.Substring(3).TrimEnd('\\','/').Replace('\\','/'));
            string result=Path.GetFullPath(requested).TrimEnd('\\');
            if(result.Length<4||result.Length>120)throw new IOException("安装路径应为 4 至 120 个字符，请缩短目录名称。");
            Child(Path.GetPathRoot(result),result.Substring(3).Replace('\\','/'));
            NoLinks(result);
            var drive=new DriveInfo(Path.GetPathRoot(result));
            if(drive.DriveType!=DriveType.Fixed||!String.Equals(drive.DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase))throw new IOException("请选择本机 NTFS 磁盘。");
            foreach(var m in Layout.Maps(result))if(Inside(result,m.Alias)||Inside(m.Alias,result)||String.Equals(result,m.Alias,StringComparison.OrdinalIgnoreCase))throw new IOException("不能安装到原 G HUB 数据目录内部或其父目录。");
            return result;
        }
        public static FileSystemRights ExpandedRights(FileSystemRights value){
            uint rights=unchecked((uint)value),mapped=rights&0x0fffffff;
            if((rights&0x80000000)!=0)mapped|=(uint)(FileSystemRights.Read|FileSystemRights.Synchronize);
            if((rights&0x40000000)!=0)mapped|=(uint)(FileSystemRights.Write|FileSystemRights.ReadPermissions|FileSystemRights.Synchronize);
            if((rights&0x20000000)!=0)mapped|=(uint)(FileSystemRights.ExecuteFile|FileSystemRights.ReadAttributes|FileSystemRights.ReadPermissions|FileSystemRights.Synchronize);
            if((rights&0x10000000)!=0)mapped|=(uint)FileSystemRights.FullControl;
            return (FileSystemRights)mapped;
        }
        public static bool Trusted(string sid){
            if(sid=="S-1-5-18"||sid=="S-1-5-32-544")return true;
            try{
                return sid==((SecurityIdentifier)new NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value;
            }
            catch{
                return false;
            }
        }
        public static void CheckParents(string target){
            string drive=Path.GetPathRoot(target).TrimEnd('\\');
            for(string p=Path.GetDirectoryName(target); !String.IsNullOrEmpty(p); p=Path.GetDirectoryName(p)){
                if(!Directory.Exists(p))continue;
                var acl=Directory.GetAccessControl(p);
                if(!Trusted(((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))).Value))throw new IOException("父目录可由普通用户替换，不能安全存放系统服务。请使用完整安装包自动设置父目录权限。");
                bool volume=String.Equals(p.TrimEnd('\\'),drive,StringComparison.OrdinalIgnoreCase);
                FileSystemRights dangerous=FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;
                if(!volume)dangerous|=FileSystemRights.Delete;
                foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))){
                    if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!Trusted(((SecurityIdentifier)rule.IdentityReference).Value)&&(ExpandedRights(rule.FileSystemRights)&dangerous)!=0)throw new IOException("父目录权限不适合系统服务。请使用完整安装包自动设置父目录权限。");
                }
            }
        }
        static FileSystemAccessRule Ace(string sid,FileSystemRights rights){
            return new FileSystemAccessRule(new SecurityIdentifier(sid),rights,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow);
        }
        public static void Protect(string dir,string user,bool privateData,bool recurse){
            NoLinks(dir);
            Directory.CreateDirectory(dir);
            var acl=new DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            acl.AddAccessRule(Ace("S-1-5-18",FileSystemRights.FullControl));
            acl.AddAccessRule(Ace("S-1-5-32-544",FileSystemRights.FullControl));
            if(privateData)acl.AddAccessRule(Ace(user,FileSystemRights.Modify));
            else{
                foreach(string sid in new[]{
                    "S-1-5-32-545","S-1-15-2-1","S-1-15-2-2"
                }
                )acl.AddAccessRule(Ace(sid,FileSystemRights.ReadAndExecute));
            }
            Directory.SetAccessControl(dir,acl);
            if(recurse){
                foreach(string child in Directory.GetDirectories(dir)){
                    NoLinks(child);
                    Protect(child,user,privateData,true);
                }
                foreach(string file in Directory.GetFiles(dir)){
                    NoLinks(file);
                    var f=new FileSecurity();
                    f.SetAccessRuleProtection(false,false);
                    f.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                    File.SetAccessControl(file,f);
                }
            }
        }
        public static void Scan(string root){
            NoLinks(root);
            foreach(string file in Directory.GetFiles(root))NoLinks(file);
            foreach(string child in Directory.GetDirectories(root))Scan(child);
        }
        public static void Copy(string source,string destination,Func<string,bool> skip){
            NoLinks(source);
            NoLinks(destination);
            Directory.CreateDirectory(destination);
            foreach(string file in Directory.GetFiles(source)){
                NoLinks(file);
                if(skip!=null&&skip(file))continue;
                File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),true);
            }
            foreach(string child in Directory.GetDirectories(source)){
                NoLinks(child);
                if(skip!=null&&skip(child))continue;
                Copy(child,Path.Combine(destination,Path.GetFileName(child)),skip);
            }
        }
        public static void DeleteOwnedTree(string path,string container){
            if(!Inside(path,container))throw new IOException("删除目录越界。");
            NoLinks(path);
            if(!Directory.Exists(path))return;
            foreach(string child in Directory.GetDirectories(path))DeleteOwnedTree(child,container);
            foreach(string file in Directory.GetFiles(path)){
                NoLinks(file);
                File.SetAttributes(file,FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(path,false);
        }
    }
}
