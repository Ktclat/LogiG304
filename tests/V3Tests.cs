// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using GHubLocal;
namespace GHubSetup {
    static class V3Tests {
        static bool Reject(Action action){
            try{
                action();
                return false;
            }
            catch(IOException){
                return true;
            }
        }
        static List<DriverState> Snapshot(bool good,long length){
            return DriverReadiness.Services.Select(s=>new DriverState{
                service=s,resolvedPath=@"C:\Windows\System32\drivers\"+s+".sys",filePresent=good,length=good?length:0
            }
            ).ToList();
        }
        public static void Run(Action<bool,string> check,string fixture){
            string windows=@"C:\Windows",driver=@"C:\Windows\system32\drivers\test.sys";
            check(Store.Eq(DriverReadiness.Resolve(@"\SystemRoot\system32\drivers\test.sys",windows),driver),"kernel SystemRoot image path resolved");
            check(Store.Eq(DriverReadiness.Resolve(@"\??\C:\Windows\system32\drivers\test.sys",windows),driver),"kernel DOS prefix image path resolved");
            check(Store.Eq(DriverReadiness.Resolve(@"System32\drivers\test.sys",windows),driver),"relative System32 driver path resolved");
            List<DriverState> last;
            int calls=0,pauses=0;
            check(DriverReadiness.Wait(()=>Snapshot(++calls>=4,64),()=>pauses++,10,(i,r)=>{
            }
            ,out last)&&calls==6&&pauses==5,"delayed PnP file appearance is awaited until stable");
            calls=0;
            check(!DriverReadiness.Wait(()=>{
                calls++; return Snapshot(false,0);
            }
            ,()=>{
            }
            ,7,(i,r)=>{
            }
            ,out last)&&calls==7,"missing driver remains a bounded failure");
            calls=0;
            check(DriverReadiness.Wait(()=>Snapshot(++calls!=3,64),()=>{
            }
            ,9,(i,r)=>{
            }
            ,out last)&&calls==6,"driver replacement gap resets stability streak");
            calls=0;
            check(DriverReadiness.Wait(()=>Snapshot(true,++calls<3?32:64),()=>{
            }
            ,9,(i,r)=>{
            }
            ,out last)&&calls==5,"changed driver file size restarts settling");
            check(!DriverReadiness.Wait(()=>Snapshot(true,64).Take(2).ToList(),()=>{
            }
            ,5,(i,r)=>{
            }
            ,out last),"all three required driver files must be present");
            check(!DriverReadiness.Wait(()=>{
                var r=Snapshot(true,64); r[0].error="sharing violation"; return r;
            }
            ,()=>{
            }
            ,5,(i,r)=>{
            }
            ,out last),"unreadable driver is not treated as ready");
            string owner=WindowsIdentity.GetCurrent().User.Value,prefix="O:"+owner+"D:P";
            string combined=prefix+"(A;OICI;0x1301bf;;;AU)",split=prefix+"(A;;0x1301bf;;;AU)(A;OICIIO;0x1301bf;;;AU)";
            check(ParentAccess.Fingerprint(combined)==ParentAccess.Fingerprint(split),"equivalent combined and split inherited ACEs compare equal");
            check(ParentAccess.Fingerprint(prefix+"(A;OICI;GA;;;AU)")==ParentAccess.Fingerprint(prefix+"(A;;FA;;;AU)(A;OICIIO;FA;;;AU)"),"generic and concrete full control compare equal across inheritance layout");
            check(ParentAccess.Fingerprint(combined)!=ParentAccess.Fingerprint(prefix+"(A;OICINP;0x1301bf;;;AU)"),"no-propagate ACL remains distinct");
            check(ParentAccess.Fingerprint(combined)!=ParentAccess.Fingerprint(prefix+"(D;OICI;0x1301bf;;;AU)"),"deny ACL remains distinct");
            check(ParentAccess.Fingerprint(combined)!=ParentAccess.Fingerprint(prefix+"(A;OICI;FA;;;AU)"),"additional permission remains distinct");
            check(ParentAccess.Fingerprint(combined)!=ParentAccess.Fingerprint("O:"+owner+"D:(A;OICI;0x1301bf;;;AU)"),"inheritance protection remains distinct");
            string parent=Path.Combine(fixture,"acl-parent"),target=Path.Combine(parent,"target");
            Directory.CreateDirectory(parent);
            var original=new DirectorySecurity();
            original.SetOwner(new SecurityIdentifier(owner));
            original.SetAccessRuleProtection(true,false);
            original.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            original.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-11"),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(parent,original);
            Directory.CreateDirectory(target);
            var journal=new Journal{
                Root=target,UserSid=owner,Parents=new List<ParentPermission>()
            }
            ;
            TargetPermissions.Capture(journal);
            TargetPermissions.MarkPrepared(journal);
            string before=ParentAccess.Sddl(Directory.GetAccessControl(parent));
            var changed=ParentAccess.From(before);
            changed.RemoveAccessRuleAll(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-11"),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(parent,changed);
            string after=ParentAccess.Sddl(Directory.GetAccessControl(parent));
            journal.Parents.Add(new ParentPermission{
                Path=parent,Before=before,Planned=after,After=after
            }
            );
            Directory.SetAccessControl(target,ParentAccess.From(journal.TargetBefore));
            check(ParentAccess.Fingerprint(ParentAccess.Sddl(Directory.GetAccessControl(target)))!=ParentAccess.Fingerprint(journal.TargetBefore),"reproduce child-first rollback mismatch against changed parent");
            Install.RestorePermissions(journal);
            check(!journal.TargetPermissionsTouched&&ParentAccess.Fingerprint(ParentAccess.Sddl(Directory.GetAccessControl(target)))==ParentAccess.Fingerprint(journal.TargetBefore),"parent-first rollback restores inherited target ACL");
            Install.RestorePermissions(journal);
            check(true,"permission recovery is repeatable");
            string failed=Path.Combine(fixture,"failed-app");
            var j=new Journal{
                Root=failed,UserSid=owner,FormatVersion=4,Phase="rollback-incomplete",DriverInstallAttempted=true,DriverExitCode=0,Maps=Layout.Maps(failed)
            }
            ;
            FailedRetry.Validate(j,failed,owner);
            check(true,"known driver-stage rollback can be preserved and retried");
            j.Phase="complete";
            check(Reject(()=>FailedRetry.Validate(j,failed,owner)),"completed install cannot be archived as a failed retry");
            j.Phase="rolled-back";
            j.DriverPending=true;
            check(Reject(()=>FailedRetry.Validate(j,failed,owner)),"active driver installation blocks failed retry");
            j.DriverPending=false;
            j.Maps[0].Linked=true;
            check(Reject(()=>FailedRetry.Validate(j,failed,owner)),"active directory mapping blocks failed retry");
            j.Maps[0].Linked=false;
            check(Reject(()=>FailedRetry.Validate(j,failed,owner+"-wrong")),"another user transaction cannot authorize a retry");
            j.Maps[0].Target=fixture;
            check(Reject(()=>FailedRetry.Validate(j,failed,owner)),"unexpected target mapping blocks failed retry");
        }
    }
}
