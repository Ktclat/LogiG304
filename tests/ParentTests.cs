using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using GHubLocal;
namespace GHubSetup {
    static class ParentTests {
        static FileSystemAccessRule Rule(string sid,FileSystemRights rights,InheritanceFlags inheritance,PropagationFlags propagation,AccessControlType type){
            return new FileSystemAccessRule(new SecurityIdentifier(sid),rights,inheritance,propagation,type);
        }
        static FileSystemRights Self(DirectorySecurity acl,string sid,AccessControlType type){
            FileSystemRights value=0;
            foreach(FileSystemAccessRule r in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(r.IdentityReference.Value==sid&&r.AccessControlType==type&&(r.PropagationFlags&PropagationFlags.InheritOnly)==0)value|=r.FileSystemRights;
            return value;
        }
        static string Templates(DirectorySecurity acl){
            return String.Join(";",acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r=>r.InheritanceFlags!=InheritanceFlags.None).Select(r=>r.IdentityReference.Value+":"+r.AccessControlType+":"+(long)SafeFs.ExpandedRights(r.FileSystemRights)+":"+r.InheritanceFlags+":"+(r.PropagationFlags&PropagationFlags.NoPropagateInherit)).OrderBy(x=>x));
        }
        static string OnVolume(string path,string fixture){
            return path.StartsWith(@"D:\",StringComparison.OrdinalIgnoreCase)?Path.GetPathRoot(fixture)+path.Substring(3):path;
        }
        public static void Run(Action<bool,string> check,string fixture){
            string user=WindowsIdentity.GetCurrent().User.Value;
            var original=new DirectorySecurity();
            original.SetOwner(new SecurityIdentifier(user));
            original.AddAccessRule(Rule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            original.AddAccessRule(Rule("S-1-5-11",FileSystemRights.Modify,InheritanceFlags.ContainerInherit,PropagationFlags.NoPropagateInherit,AccessControlType.Allow));
            original.AddAccessRule(Rule("S-1-1-0",FileSystemRights.ExecuteFile,InheritanceFlags.ContainerInherit,PropagationFlags.InheritOnly,AccessControlType.Deny));
            original.AddAccessRule(Rule("S-1-3-0",FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.InheritOnly,AccessControlType.Allow));
            string before=ParentAccess.Sddl(original);
            var secured=ParentAccess.Restricted(original,false);
            check(!ParentAccess.Secure(original,false)&&ParentAccess.Secure(secured,false),"user writable parent becomes service safe");
            var rights=Self(secured,user,AccessControlType.Allow);
            check((rights&(FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))==0,"parent replacement rights removed");
            check((rights&(FileSystemRights.WriteData|FileSystemRights.CreateDirectories|FileSystemRights.ReadData))==(FileSystemRights.WriteData|FileSystemRights.CreateDirectories|FileSystemRights.ReadData),"normal read write create rights retained");
            check(Templates(original)==Templates(secured),"sibling and descendant inheritance templates unchanged");
            check(ParentAccess.Sddl(original)==before,"planning does not mutate original ACL");
            check(ParentAccess.Fingerprint(ParentAccess.Sddl(ParentAccess.From(before)))==ParentAccess.Fingerprint(before),"rollback ACL snapshot roundtrip");
            check(secured.AreAccessRulesProtected,"protected parent permissions");
            check(((SecurityIdentifier)secured.GetOwner(typeof(SecurityIdentifier))).Value=="S-1-5-32-544","parent ownership assigned to administrators");
            check(secured.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.AccessControlType==AccessControlType.Deny&&r.IdentityReference.Value=="S-1-1-0"),"deny inheritance preserved");
            check(ParentAccess.Fingerprint(ParentAccess.Sddl(ParentAccess.Restricted(secured,false)))==ParentAccess.Fingerprint(ParentAccess.Sddl(secured)),"permission planning is idempotent");
            var root=ParentAccess.Restricted(original,true);
            check((Self(root,user,AccessControlType.Allow)&FileSystemRights.Delete)!=0&&(Self(root,user,AccessControlType.Allow)&FileSystemRights.DeleteSubdirectoriesAndFiles)==0,"volume rights distinguished from folder rights");
            foreach(string sample in new[]{
                @"D:\Software\Logitech\GHub",@"D:\My Applications\Logitech G Hub",@"D:\软件\外设工具\罗技 G HUB",@"D:\Software\"+new string('a',91)
            }
            ){
                string path=OnVolume(sample,fixture);
                check(Store.Eq(SafeFs.Target(path),path),"accept nested target "+path);
                check(ParentAccess.Preview(path)!=null,"read-only parent plan "+path);
            }
            foreach(string bad in new[]{
                @"relative\GHub",@"D:GHub",@"\\server\share\GHub",@"D:\",@"D:\Software\CON",@"D:\Software\trailing.",@"D:\Software\"+new string('b',121)
            }
            ){
                bool rejected=false;
                try{
                    SafeFs.Target(bad);
                }
                catch{
                    rejected=true;
                }
                check(rejected,"reject invalid install target "+bad);
            }
            var generic=ParentAccess.From("O:"+user+"D:(A;OICI;GA;;;AU)(A;OICIIO;GRGX;;;BU)");
            var genericSecured=ParentAccess.Restricted(generic,false);
            check(!ParentAccess.Secure(generic,false)&&ParentAccess.Secure(genericSecured,false),"generic all rights cannot bypass parent protection");
            check(Templates(generic)==Templates(genericSecured),"generic inherited permissions preserve effective rights");
            check(ParentAccess.Secure(ParentAccess.Restricted(Directory.GetAccessControl(fixture),false),false),"actual Software directory ACL can be planned without elevation");
            check(InstallPath.Resolve(OnVolume(@"D:\Toolkit\Software",fixture),"GHUB")==OnVolume(@"D:\Toolkit\Software\GHUB",fixture),"shared Software folder stays outside the installation target");
            check(InstallPath.Resolve(OnVolume(@"D:\软件\外设工具",fixture),"罗技 G HUB")==OnVolume(@"D:\软件\外设工具\罗技 G HUB",fixture),"independent folder supports Chinese and spaces");
            foreach(string badFolder in new[]{
                "", "..",@"Other\GHUB","X:Y","GHUB "
            }
            ){
                bool invalid=false;
                try{
                    InstallPath.Resolve(@"D:\Toolkit\Software",badFolder);
                }
                catch{
                    invalid=true;
                }
                check(invalid,"reject invalid application folder name "+badFolder);
            }
            string snapshotFolder=Path.Combine(fixture,"target-acl-snapshot");
            Directory.CreateDirectory(snapshotFolder);
            string snapshot=ParentAccess.Sddl(Directory.GetAccessControl(snapshotFolder));
            var captured=new Journal{
                Root=snapshotFolder,UserSid=user
            }
            ;
            TargetPermissions.Capture(captured);
            check(captured.TargetCaptured&&captured.TargetExisted&&ParentAccess.Fingerprint(captured.TargetBefore)==ParentAccess.Fingerprint(snapshot),"capture installation directory ACL before protection");
            check(ParentAccess.Fingerprint(ParentAccess.Sddl(Directory.GetAccessControl(snapshotFolder)))==ParentAccess.Fingerprint(snapshot),"capturing permissions leaves directory unchanged");
            TargetPermissions.MarkPrepared(captured);
            check(captured.TargetPermissionsTouched,"permission change recorded before protection");
            var loaded=Store.Json().Deserialize<Journal>(Store.Json().Serialize(captured));
            check(loaded.TargetCaptured&&loaded.TargetExisted&&loaded.TargetBefore==snapshot&&loaded.TargetPermissionsTouched,"target ACL snapshot survives interrupted-install journal");
            var newTarget=new Journal{
                Root=Path.Combine(fixture,"not-created-yet")
            }
            ;
            TargetPermissions.Capture(newTarget);
            check(newTarget.TargetCaptured&&!newTarget.TargetExisted&&newTarget.TargetBefore==null&&!Directory.Exists(newTarget.Root),"new target recorded without creating it");
            check(!TargetPermissions.OwnedTrees.Contains("MIUI")&&!TargetPermissions.OwnedTrees.Contains("Software"),"rollback permissions exclude unrelated sibling software");
            var ancestors=ParentAccess.Ancestors(@"D:\Software\Logitech\GHub");
            check(ancestors.SequenceEqual(new[]{
                @"D:\",@"D:\Software",@"D:\Software\Logitech"
            }
            ),"parents prepared from volume towards target");
            string real=Path.Combine(fixture,"junction-real"),link=Path.Combine(fixture,"junction-link");
            Directory.CreateDirectory(real);
            Junction.Create(link,real);
            bool refused=false;
            try{
                ParentAccess.Preview(Path.Combine(link,"new-target"));
            }
            catch{
                refused=true;
            }
            check(refused,"reject nested target beneath junction");
            Junction.Remove(link,real);
            var journal=new Journal{
                Root=@"D:\Software\Logitech\GHub",Parents=new List<ParentPermission>{
                    new ParentPermission{
                        Path=@"D:\Unrelated"
                    }
                }
            }
            ;
            bool outside=false;
            try{
                ParentAccess.ValidateRecords(journal);
            }
            catch{
                outside=true;
            }
            check(outside,"reject parent restore outside selected ancestry");
            journal.Parents=new List<ParentPermission>{
                new ParentPermission{
                    Path=@"D:\Software"
                }
                ,new ParentPermission{
                    Path=@"D:\Software"
                }
            }
            ;
            bool repeated=false;
            try{
                ParentAccess.ValidateRecords(journal);
            }
            catch{
                repeated=true;
            }
            check(repeated,"reject duplicate parent restore records");
        }
    }
}
