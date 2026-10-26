// SPDX-FileCopyrightText: 2026 Ktclat
// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GHubLocal;

namespace GHubSetup {
    static class DownloadGuideTests {
        static bool Reject(Action action) {
            try { action();return false; } catch(IOException) { return true; }
        }
        public static void Run(Action<bool,string> check,string fixture) {
            string root=Path.Combine(fixture,"下载包 中文");
            Directory.CreateDirectory(Path.Combine(root,"tools"));
            foreach(string name in DownloadGuide.PackageFiles)
                File.WriteAllText(SafeFs.Child(root,name),"fixture-only "+name,Encoding.UTF8);
            var hashes=DownloadGuide.PackageFiles.ToDictionary(name=>name,name=>(object)Store.Hash(SafeFs.Child(root,name)));
            var manifest=new Dictionary<string,object> {
                {"schemaVersion",1},{"packageKind","project-tools-only"},{"supportedCoreVersion","2025.9.814156"},
                {"sourceCommit",new string('a',40)},{"containsOfficialCore",false},{"containsDrivers",false},
                {"isCompleteInstaller",false},{"files",hashes}
            };
            Action save=()=>File.WriteAllText(Path.Combine(root,"package-manifest.json"),Store.Json().Serialize(manifest),Encoding.UTF8);
            save();
            string before=Store.Hash(Path.Combine(root,"package-manifest.json"));
            check(DownloadGuide.CheckPackage(root).Contains("校验通过"),"download guide verifies complete unicode-path package");
            check(Store.Hash(Path.Combine(root,"package-manifest.json"))==before&&!Directory.Exists(Path.Combine(root,"Data")),"package inspection leaves files and logs untouched");
            string tool=SafeFs.Child(root,"tools/G HUB Offline.exe");
            File.AppendAllText(tool,"tampered");
            check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide rejects changed tool");
            File.WriteAllText(tool,"fixture-only tools/G HUB Offline.exe",Encoding.UTF8);
            string missing=SafeFs.Child(root,"开始使用.exe.config");
            File.Delete(missing);
            check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide rejects partial extraction");
            File.WriteAllText(missing,"fixture-only 开始使用.exe.config",Encoding.UTF8);
            string extra=Path.Combine(root,"settings.db");
            File.WriteAllText(extra,"fixture-private-sentinel");
            check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide rejects mixed download and profile directory");
            check(File.ReadAllText(extra)=="fixture-private-sentinel","download guide never removes unexpected files");
            File.Delete(extra);
            foreach(string flag in new[]{"containsOfficialCore","containsDrivers","isCompleteInstaller"}) {
                manifest[flag]=true;save();
                check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide rejects incorrect scope flag "+flag);
                manifest[flag]=false;
            }
            manifest["sourceCommit"]="unknown";save();
            check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide requires source commit identity");
            manifest["sourceCommit"]=new string('a',40);
            hashes.Add("../outside",new string('0',64));save();
            check(Reject(()=>DownloadGuide.CheckPackage(root)),"download guide rejects path outside package");
            hashes.Remove("../outside");save();
            string alias=Path.Combine(fixture,"download-alias");
            Junction.Create(alias,root);
            check(Reject(()=>DownloadGuide.CheckPackage(alias)),"download guide rejects junction input");
            Junction.Remove(alias,root);
            check(Reject(()=>DownloadGuide.LocalFolder(@"\\example.invalid\share")),"download guide refuses network paths before file access");
            check(Reject(()=>DownloadGuide.LocalFolder("relative")),"download guide refuses relative input");
            string tools=Path.Combine(root,"tools");
            check(DownloadGuide.IsDistributionTool(tools),"loose packaged helpers are detected");
            check(DownloadGuide.BlockDistributionTool(tools,true),"quiet helper guard stops before installed initialization");
            check(!Directory.Exists(Path.Combine(tools,"Data")),"blocked helper does not create profile or logs");
            string recorded=Path.Combine(tools,"install-state.json");
            File.WriteAllText(recorded,"fixture-only");
            check(!DownloadGuide.IsDistributionTool(tools),"guard leaves an existing installation named tools to normal validation");
            File.Delete(recorded);
            check(!DownloadGuide.IsDistributionTool(root),"guard does not block arbitrary install root");
            string summary;
            check(Reject(()=>DownloadGuide.CheckInstallation(root,out summary)),"existing install check requires installation record");
            check(DownloadGuide.CheckPackage(root).Contains("校验通过"),"rejected inspections leave valid package intact");
        }
    }
}
