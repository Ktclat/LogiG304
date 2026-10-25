using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using GHubLocal;
namespace GHubSetup {
    static class SetupMessages {
        const int MaxMessageBytes=1024*1024;
        public static FileStream OpenSnapshot(string file){
            SafeFs.NoLinks(file);
            return new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        }
        static Dictionary<string,object> ReadOnce(string file){
            using(var stream=OpenSnapshot(file)){
                if(stream.Length>MaxMessageBytes)throw new InvalidDataException("安装通信文件过大。");
                using(var reader=new StreamReader(stream,Encoding.UTF8)){
                    var value=Store.Json().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                    if(value==null)throw new InvalidDataException("安装通信文件为空。");
                    return value;
                }
            }
        }
        static bool Sharing(IOException error){
            int code=error.HResult&0xffff;
            return code==32||code==33;
        }
        public static bool TryReadProgress(string file,out Dictionary<string,object> value){
            value=null;
            try{
                value=ReadOnce(file);
                return true;
            }
            catch(IOException){
                return false;
            }
            catch(UnauthorizedAccessException){
                return false;
            }
            catch(ArgumentException){
                return false;
            }
            catch(InvalidOperationException){
                return false;
            }
        }
        public static Dictionary<string,object> ReadRequired(string file){
            var timer=Stopwatch.StartNew();
            while(true){
                try{
                    return ReadOnce(file);
                }
                catch(IOException error){
                    if(!Sharing(error)||timer.ElapsedMilliseconds>=3000)throw;
                    Thread.Sleep(25);
                }
            }
        }
        public static void WriteRequired(string file,object value,int timeoutMs=3000){
            var timer=Stopwatch.StartNew();
            while(true){
                try{
                    Store.Write(file,value);
                    return;
                }
                catch(IOException error){
                    if(!Sharing(error)||timer.ElapsedMilliseconds>=timeoutMs)throw;
                    Thread.Sleep(25);
                }
            }
        }
        public static bool TryWriteProgress(string file,object value){
            try{
                WriteRequired(file,value,150);
                return true;
            }
            catch(IOException error){
                if(!Sharing(error))throw;
                return false;
            }
        }
    }
    static class MessageTests {
        public static void Run(Action<bool,string> check,string fixture){
            string file=Path.Combine(fixture,"progress.json");
            Dictionary<string,object> value;
            SetupMessages.WriteRequired(file,new{
                phase="extracting",count=1,mirror=1
            }
            );
            using(var snapshot=SetupMessages.OpenSnapshot(file)){
                SetupMessages.WriteRequired(file,new{
                    phase="extracting",count=2,mirror=2
                }
                );
                using(var reader=new StreamReader(snapshot,Encoding.UTF8)){
                    var before=Store.Json().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                    check(Convert.ToInt32(before["count"])==1,"an open shared snapshot remains consistent during atomic replacement");
                }
            }
            check(Convert.ToInt32(SetupMessages.ReadRequired(file)["count"])==2,"writer can replace progress while a snapshot is open");
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
                bool reproduced=false;
                try{
                    Store.Read(file);
                }
                catch(IOException){
                    reproduced=true;
                }
                check(reproduced,"reproduce the former progress sharing violation");
                check(!SetupMessages.TryReadProgress(file,out value),"a locked progress file does not abort the installer monitor");
                check(!SetupMessages.TryWriteProgress(file,new{
                    phase="locked"
                }
                ),"a locked progress destination does not abort installation");
            }
            check(SetupMessages.TryReadProgress(file,out value)&&Convert.ToInt32(value["count"])==2,"monitor resumes after a temporary file lock");
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
                var release=new Thread(()=>{
                    Thread.Sleep(120); locked.Dispose();
                }
                );
                release.Start();
                check(Convert.ToInt32(SetupMessages.ReadRequired(file)["count"])==2,"mandatory result reads retry a transient sharing violation");
                release.Join();
            }
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
                var release=new Thread(()=>{
                    Thread.Sleep(120); locked.Dispose();
                }
                );
                release.Start();
                SetupMessages.WriteRequired(file,new{
                    phase="resumed",count=3,mirror=3
                }
                );
                release.Join();
            }
            check(Convert.ToInt32(SetupMessages.ReadRequired(file)["count"])==3,"mandatory result writes retry a transient sharing violation");
            File.WriteAllText(file,"{\"phase\":",Encoding.UTF8);
            check(!SetupMessages.TryReadProgress(file,out value),"incomplete optional progress is skipped");
            bool rejected=false;
            try{
                SetupMessages.ReadRequired(file);
            }
            catch{
                rejected=true;
            }
            check(rejected,"invalid mandatory messages still fail validation");
            string missing=Path.Combine(fixture,"not-published.json");
            check(!SetupMessages.TryReadProgress(missing,out value),"not-yet-published progress is harmless");
            SetupMessages.WriteRequired(file,new{
                phase="stress",count=0,mirror=0
            }
            );
            Exception writerError=null;
            var finished=new ManualResetEvent(false);
            var writer=new Thread(()=>{
                try{
                    for(int i=1; i<=300; i++)SetupMessages.WriteRequired(file,new{
                        phase="stress",count=i,mirror=i
                    }
                    );
                }
                catch(Exception e){
                    writerError=e;
                }
                finally{
                    finished.Set();
                }
            }
            );
            writer.Start();
            int samples=0;
            bool coherent=true;
            var watch=Stopwatch.StartNew();
            while(!finished.WaitOne(0)&&watch.ElapsedMilliseconds<15000){
                if(SetupMessages.TryReadProgress(file,out value)){
                    coherent&=Convert.ToInt32(value["count"])==Convert.ToInt32(value["mirror"]);
                    samples++;
                }
                Thread.Yield();
            }
            bool joined=writer.Join(5000);
            finished.Dispose();
            check(joined&&writerError==null&&coherent&&samples>0,"concurrent progress updates never expose partial JSON or sharing failures");
            check(Convert.ToInt32(SetupMessages.ReadRequired(file)["count"])==300,"the final progress revision is visible after concurrent writes");
        }
    }
}
