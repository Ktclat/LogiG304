// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using GHubLocal;
namespace GHubSetup {
    static class StandaloneTests {
        static bool Reject(Action run){
            try{
                run();
                return false;
            }
            catch(IOException){
                return true;
            }
        }
        public static void Run(Action<bool,string> check,string fixture){
            string oldRoot=Path.Combine(fixture,"旧配置"),newRoot=Path.Combine(fixture,"新 程序"),other=Path.Combine(fixture,"other");
            var prior=Rules.Expected(new[]{
                Path.Combine(oldRoot,@"App\lghub.exe"),Path.Combine(Store.AliasApp,"lghub.exe")
            }
            );
            var next=Rules.Expected(new[]{
                Path.Combine(newRoot,@"App\lghub.exe"),Path.Combine(Store.AliasApp,"lghub.exe")
            }
            );
            LegacyImport.ValidateRules(prior,oldRoot);
            check(true,"canonical legacy rules accepted");
            var combined=LegacyImport.Combined(next,prior);
            check(combined.Count==9,"shared compatibility paths merge without duplicate rules");
            var changed=prior.Select(r=>r.Copy()).ToList();
            changed[0].Action=1;
            check(!Rules.Same(prior[0],changed[0])&&!Rules.Same(changed[0],prior[0]),"rule validation rejects action changes in either argument order");
            check(Reject(()=>LegacyImport.ValidateRules(changed,oldRoot)),"legacy rule action tampering rejected");
            check(Reject(()=>LegacyImport.Combined(prior,changed)),"rule merge rejects conflicting same-name properties");
            check(Reject(()=>LegacyImport.ValidateRules(Rules.Expected(new[]{
                Path.Combine(other,"unrelated.exe")
            }
            ),oldRoot)),"legacy firewall scope cannot include unrelated executables");
            check(Reject(()=>LegacyImport.ValidateRules(prior.Concat(new[]{
                prior[0]
            }
            ).ToList(),oldRoot)),"duplicate legacy rules rejected");
            check(Reject(()=>LegacyImport.ValidateRules(prior.Take(prior.Count-1).ToList(),oldRoot)),"incomplete legacy rules rejected");
            var original=new Journal{
                Root=oldRoot,Id=new string('1',32),Rules=prior,Parents=new List<ParentPermission>()
            }
            ;
            var current=new Journal{
                Root=newRoot,Maps=Layout.Maps(newRoot)
            }
            ;
            LegacyImport.Attach(current,original);
            check(Store.Eq(current.ImportRoot,oldRoot)&&current.LegacyRules.Count==prior.Count,"separate old configuration source recorded");
            check(Reject(()=>LegacyImport.Attach(new Journal{
                Root=oldRoot
            }
            ,original)),"migration cannot overwrite the original installation");
            check(Reject(()=>LegacyImport.Attach(new Journal{
                Root=Path.Combine(oldRoot,"inside")
            }
            ,original)),"migration cannot nest beneath the original installation");
            check(Reject(()=>LegacyImport.Attach(new Journal{
                Root=fixture
            }
            ,original)),"migration cannot contain the original installation");
            var local=current.Maps.Single(m=>m.Key=="local");
            local.OriginalLink=Layout.Maps(oldRoot).Single(m=>m.Key=="local").Target;
            local.Source=local.OriginalLink;
            LegacyImport.ValidateRecovery(current);
            check(true,"known old configuration junction has a valid recovery record");
            local.OriginalLink=other;
            check(Reject(()=>LegacyImport.ValidateRecovery(current)),"recovery rejects an unrelated original junction");
            local.OriginalLink=local.Source;
            local.Source=other;
            check(Reject(()=>LegacyImport.ValidateRecovery(current)),"recovery rejects an unrelated configuration source");
            string source=Path.Combine(fixture,"profile-source"),copy=Path.Combine(fixture,"profile-copy");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source,"Cache"));
            Directory.CreateDirectory(Path.Combine(source,"integrations_config"));
            File.WriteAllText(Path.Combine(source,"settings.db"),"local-profile");
            File.WriteAllText(Path.Combine(source,"Cache","stale"),"omit");
            File.WriteAllText(Path.Combine(source,"integrations_config","user.json"),"settings");
            string before=Store.Hash(Path.Combine(source,"settings.db"));
            SafeFs.Copy(source,copy,p=>LegacyImport.Skip("local",source,p));
            check(Store.Hash(Path.Combine(copy,"settings.db"))==before&&Store.Hash(Path.Combine(source,"settings.db"))==before,"profile import preserves both configuration bytes and source");
            check(!Directory.Exists(Path.Combine(copy,"Cache"))&&File.Exists(Path.Combine(copy,"integrations_config","user.json")),"profile import discards cache while keeping user integration settings");
            check(!LegacyImport.Skip("machine",source,Path.Combine(source,"keys.json"))&&LegacyImport.Skip("machine",source,Path.Combine(source,"unrelated.exe")),"machine configuration import excludes executables");
            using(var process=Process.GetCurrentProcess()){
                var pending=new Journal{
                    Root=newRoot,DriverPending=true,DriverPid=process.Id,DriverTicks=process.StartTime.ToUniversalTime().Ticks
                }
                ;
                check(Reject(()=>DriverSetup.CheckNotRunning(pending)),"recovery cannot overlook an active mismatched driver process");
            }
            check(Install.Quote(Path.Combine(newRoot,"卸载 G HUB 本地版.exe")).StartsWith("\"")&&Install.Quote(Path.Combine(newRoot,"卸载 G HUB 本地版.exe")).EndsWith("\""),"uninstall executable paths with spaces are quoted");
        }
    }
}
