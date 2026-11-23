using System.IO;
using System.Reflection;
using System.Windows;
using TeklaBackupManager;
class Program {
static int failures, passed;
static string root = Path.Combine(Path.GetTempPath(), "TeklaBackupManager-tests-" + Guid.NewGuid().ToString("N"));
static object? Call(string name, object? instance, params object?[] args) {
 try { return typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.Invoke(instance,args); }
 catch(TargetInvocationException e) { throw e.InnerException!; }
}
static void Check(bool value,string message) { if(!value) throw new Exception(message); }
static void Test(string name,Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch(Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); } }
static void Reject(Action action) { try { action(); } catch(InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
static BackupRequest Request(string name,BackupMode mode) {
 var src=Path.Combine(root,name,"source");var dest=Path.Combine(root,name,"backup"); Directory.CreateDirectory(src);
 var target=Path.Combine(dest,mode==BackupMode.FullMirror?"TEKLASTRUCTUREMODEL":"BACKUPDB1","TEST-USER");
 if(mode==BackupMode.Db1Timestamped) target=Path.Combine(target,DateTime.Now.ToString("yyyy-dd-MM"));
 return new(new(),"TEST-USER","WORKSTATION-01",src,dest,target,"Fictitious test",true,mode,false,30);
}
static void Run(BackupRequest r) => Call("RunBackup",null,r,new Progress<BackupProgressInfo>(),CancellationToken.None);
[STAThread] static int Main(string[] args) {
 if(args.Length>0 && args[0]=="--safety-worker") return SafetyTests.Worker(args);
 Directory.CreateDirectory(root);
 Test("DB1 copies only db1 and preserves content",()=>{ var r=Request("db1",BackupMode.Db1Timestamped); File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"test-db1");File.WriteAllText(Path.Combine(r.SourcePath,"other.txt"),"ignore");Run(r);Check(File.ReadAllText(Path.Combine(r.TargetPath,"model.db1"))=="test-db1","content");Check(!File.Exists(Path.Combine(r.TargetPath,"other.txt")),"filter");Check(File.Exists(Path.Combine(r.DestinationRoot,".tekla-backup-manifests",r.EmployeeName,r.Mode.ToString(),DateTime.Now.ToString("yyyy-dd-MM")+".txt")),"manifest"); });
 Test("Full mirror copies and removes stale destination only",()=>{var r=Request("mirror",BackupMode.FullMirror);File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"fresh");Run(r);Directory.CreateDirectory(r.TargetPath);File.WriteAllText(Path.Combine(r.TargetPath,"stale.txt"),"stale");var outside=Path.Combine(root,"outside.txt");File.WriteAllText(outside,"keep");Run(r);Check(!File.Exists(Path.Combine(r.TargetPath,"stale.txt")),"stale remains");Check(File.Exists(outside),"outside deleted");Check(File.ReadAllText(Path.Combine(r.TargetPath,"model.db1"))=="fresh","content");});
 Test("Reject overlapping source and backup root",()=>{var r=Request("overlap",BackupMode.FullMirror);File.WriteAllText(Path.Combine(r.SourcePath,"file.db1"),"x");r=r with{DestinationRoot=r.SourcePath,TargetPath=Path.Combine(r.SourcePath,"nested")};Reject(()=>Run(r));});
 Test("Reject empty mirror without deleting old backup",()=>{var r=Request("empty",BackupMode.FullMirror);var seed=Path.Combine(r.SourcePath,"seed.db1");File.WriteAllText(seed,"seed");Run(r);File.Delete(seed);Directory.CreateDirectory(r.TargetPath);var sentinel=Path.Combine(r.TargetPath,"keep.db1");File.WriteAllText(sentinel,"keep");Reject(()=>Run(r));Check(File.Exists(sentinel),"backup deleted");});
 Test("Reject dot folder components",()=>Reject(()=>Call("SanitizeFolderName",null,"..")));
 Test("Reject out of day schedule",()=>Reject(()=>Call("ParseTime",null,"25:00","test")));
 Test("Retention preserves unrelated directories",()=>{var path=Path.Combine(root,"retention","notes");Directory.CreateDirectory(path);Directory.SetCreationTime(path,DateTime.Now.AddDays(-100));Directory.SetLastWriteTime(path,DateTime.Now.AddDays(-100));Call("PurgeOldBackups",null,Path.GetDirectoryName(path)!,30,null);Check(Directory.Exists(path),"unrelated folder purged");});
 Test("Missing source fails safely",()=>{var r=Request("missing",BackupMode.FullMirror);Directory.Delete(r.SourcePath);try{Run(r);throw new Exception("expected failure");}catch(DirectoryNotFoundException){}Check(!Directory.Exists(r.TargetPath),"destination created");});

 Test("Retention removes old dated backups and keeps recent",()=>{var path=Path.Combine(root,"dated-retention");Directory.CreateDirectory(path);var old=Path.Combine(path,DateTime.Now.AddDays(-100).ToString("yyyy-dd-MM"));Directory.CreateDirectory(old);Directory.SetCreationTime(old,DateTime.Now.AddDays(-100));Directory.SetLastWriteTime(old,DateTime.Now.AddDays(-100));var recent=Path.Combine(path,DateTime.Now.ToString("yyyy-dd-MM"));Directory.CreateDirectory(recent);Call("PurgeOldBackups",null,path,30,null);Check(!Directory.Exists(old),"old backup retained");Check(Directory.Exists(recent),"recent backup removed");});
 Test("Canceled backup stops copying",()=>{var r=Request("canceled",BackupMode.Db1Timestamped);File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"x");try{Call("RunBackup",null,r,new Progress<BackupProgressInfo>(),new CancellationToken(true));throw new Exception("expected cancellation");}catch(OperationCanceledException){}Check(!File.Exists(Path.Combine(r.TargetPath,"model.db1")),"copied despite cancellation");});
 Test("Mirror updates modified file on subsequent run",()=>{var r=Request("update",BackupMode.FullMirror);var src=Path.Combine(r.SourcePath,"model.db1");File.WriteAllText(src,"first");Run(r);File.WriteAllText(src,"second and longer");Run(r);Check(File.ReadAllText(Path.Combine(r.TargetPath,"model.db1"))=="second and longer","not updated");});
 Test("DB1 history retains previous day",()=>{var r=Request("history",BackupMode.Db1Timestamped);File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"initial");Run(r);var previous=Path.Combine(Path.GetDirectoryName(r.TargetPath)!,DateTime.Now.AddDays(-1).ToString("yyyy-dd-MM"));Directory.CreateDirectory(previous);File.WriteAllText(Path.Combine(previous,"old.db1"),"old");File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"new");Run(r);Check(File.Exists(Path.Combine(previous,"old.db1")),"history lost");});
 Test("Strict schedule rejects negative time",()=>Reject(()=>Call("ParseTime",null,"-01:00","test")));
 Test("Junction to outside tree is rejected",()=>{var r=Request("junction",BackupMode.FullMirror);var external=Path.Combine(root,"junction-external");Directory.CreateDirectory(external);File.WriteAllText(Path.Combine(external,"keep.db1"),"keep");var link=Path.Combine(r.SourcePath,"link");var psi=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("/c");psi.ArgumentList.Add("mklink");psi.ArgumentList.Add("/J");psi.ArgumentList.Add(link);psi.ArgumentList.Add(external);using var process=System.Diagnostics.Process.Start(psi)!;process.WaitForExit();Check(process.ExitCode==0,"junction setup failed");Reject(()=>Run(r));Check(File.Exists(Path.Combine(external,"keep.db1")),"outside data changed");Directory.Delete(link);});
 Test("WPF load, local config, language, users and schedule",()=>{
 var storage=Path.Combine(root,"ui-profile");
 var window=(MainWindow)Activator.CreateInstance(typeof(MainWindow),BindingFlags.NonPublic|BindingFlags.Instance,null,new object?[]{storage,false},null)!;
 object Get(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(window)!;
 try {
 Call("SaveSettings",window);Check(File.Exists(Path.Combine(storage,"settings.json")),"configuration missing");
 Call("ApplyLanguage",window,"en");Check(((System.Windows.Controls.Button)Get("Db1BackupButton")).Content.ToString()=="Run DB1 now","English");
 Call("ApplyLanguage",window,"fr");Check(((System.Windows.Controls.Button)Get("Db1BackupButton")).Content.ToString()=="Lancer DB1 maintenant","French");
 var employees=(System.Collections.ObjectModel.ObservableCollection<EmployeeBackupConfig>)Get("_employees");int initial=employees.Count;
 ((System.Windows.Controls.TextBox)Get("EmployeeNameBox")).Text="TEST-USER";
 ((System.Windows.Controls.TextBox)Get("MachineNameBox")).Text="WORKSTATION-01";
 ((System.Windows.Controls.TextBox)Get("SourcePathBox")).Text=Path.Combine(root,"fake-models");
 Call("AddEmployee_Click",window,null,new RoutedEventArgs());Check(employees.Count==initial+1,"add");
 Call("RemoveEmployee_Click",window,null,new RoutedEventArgs());Check(employees.Count==initial,"remove");
 var employee=new EmployeeBackupConfig{EmployeeName="TEST",MachineName="WORKSTATION-01",SourcePath=root,DailyDb1Time="12:15"};
 Check(!(bool)Call("IsEmployeeDue",window,employee,BackupMode.Db1Timestamped,new TimeSpan(12,14,0),DateTime.Today)!,"early schedule");
 Check((bool)Call("IsEmployeeDue",window,employee,BackupMode.Db1Timestamped,new TimeSpan(12,15,0),DateTime.Today)!,"due schedule");
 var key=(string)Call("BuildAutomaticRunKey",null,employee,BackupMode.Db1Timestamped,DateTime.Today)!;
 ((HashSet<string>)Get("_automaticDb1RunKeys")).Add(key);
 Check(!(bool)Call("IsEmployeeDue",window,employee,BackupMode.Db1Timestamped,new TimeSpan(13,0,0),DateTime.Today)!,"duplicate schedule");
 using var icon=(System.Windows.Forms.NotifyIcon)Call("CreateNotifyIcon",window)!;Check(icon.Visible,"tray icon");icon.Visible=false;
 var closing=new System.ComponentModel.CancelEventArgs();Call("OnClosing",window,closing);Check(closing.Cancel,"close must hide");
 } finally {typeof(MainWindow).GetField("_isExitRequested",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);window.Close();}
 });

 Test("DB1 preserves nested models and uppercase extension",()=>{var r=Request("nested",BackupMode.Db1Timestamped);Directory.CreateDirectory(Path.Combine(r.SourcePath,"Project-A"));Directory.CreateDirectory(Path.Combine(r.SourcePath,"Project-B"));File.WriteAllText(Path.Combine(r.SourcePath,"Project-A","model.DB1"),"alpha");File.WriteAllText(Path.Combine(r.SourcePath,"Project-B","model.db1"),"beta");File.WriteAllText(Path.Combine(r.SourcePath,"Project-B","notes.txt"),"ignore");Run(r);Check(File.ReadAllText(Path.Combine(r.TargetPath,"Project-A","model.DB1"))=="alpha","uppercase file");Check(File.ReadAllText(Path.Combine(r.TargetPath,"Project-B","model.db1"))=="beta","nested model");Check(!File.Exists(Path.Combine(r.TargetPath,"Project-B","notes.txt")),"non-db1 copied");});
 Test("Repeated same-day DB1 updates current backup",()=>{var r=Request("same-day",BackupMode.Db1Timestamped);var file=Path.Combine(r.SourcePath,"model.db1");File.WriteAllText(file,"first");Run(r);File.WriteAllText(file,"second");Run(r);Check(File.ReadAllText(Path.Combine(r.TargetPath,"model.db1"))=="second","same-day update");});
 Test("Copy failure preserves stale mirror files",()=>{var r=Request("locked",BackupMode.FullMirror);var file=Path.Combine(r.SourcePath,"model.db1");File.WriteAllText(file,"initial");Run(r);File.WriteAllText(file,"locked and changed");Directory.CreateDirectory(r.TargetPath);var stale=Path.Combine(r.TargetPath,"keep.db1");File.WriteAllText(stale,"keep");using(var held=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){try{Run(r);throw new Exception("Expected IO failure");}catch(IOException){}}Check(File.ReadAllText(stale)=="keep","deletion after failed copy");});
 Test("Reject source located inside backup root",()=>{var r=Request("reverse-overlap",BackupMode.FullMirror);r=r with{DestinationRoot=Path.GetDirectoryName(r.SourcePath)!,TargetPath=Path.Combine(Path.GetDirectoryName(r.SourcePath)!,"TEKLASTRUCTUREMODEL","TEST-USER")};File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"keep");Reject(()=>Run(r));Check(!Directory.Exists(r.TargetPath),"destination created");});
 Test("Destination junction cannot delete external data",()=>{var r=Request("destination-junction",BackupMode.FullMirror);File.WriteAllText(Path.Combine(r.SourcePath,"model.db1"),"source");Run(r);var external=Path.Combine(root,"protected-external");Directory.CreateDirectory(external);var sentinel=Path.Combine(external,"keep.db1");File.WriteAllText(sentinel,"keep");Directory.CreateDirectory(r.TargetPath);var link=Path.Combine(r.TargetPath,"link");var psi=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("/c");psi.ArgumentList.Add("mklink");psi.ArgumentList.Add("/J");psi.ArgumentList.Add(link);psi.ArgumentList.Add(external);using var process=System.Diagnostics.Process.Start(psi)!;process.WaitForExit();Check(process.ExitCode==0,"junction setup");try{Reject(()=>Run(r));Check(File.ReadAllText(sentinel)=="keep","external data changed");}finally{Directory.Delete(link);}});
 Test("Fictitious full backup can be copied back with identical hashes",()=>{var r=Request("restore",BackupMode.FullMirror);Directory.CreateDirectory(Path.Combine(r.SourcePath,"Model-A"));File.WriteAllBytes(Path.Combine(r.SourcePath,"Model-A","model.db1"),Enumerable.Range(0,4096).Select(i=>(byte)(i%251)).ToArray());File.WriteAllText(Path.Combine(r.SourcePath,"Model-A","attributes.txt"),"fictitious attributes");Run(r);var restored=Path.Combine(root,"restored-fictitious-model");foreach(var src in Directory.EnumerateFiles(r.SourcePath,"*",SearchOption.AllDirectories)){var relative=Path.GetRelativePath(r.SourcePath,src);var copy=Path.Combine(restored,relative);Directory.CreateDirectory(Path.GetDirectoryName(copy)!);File.Copy(Path.Combine(r.TargetPath,relative),copy);Check(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(src)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copy))),"restored hash mismatch");}});
 var safety=SafetyTests.Run(); passed+=safety.Passed; failures+=safety.Failed;
 Console.WriteLine($"RESULT {passed} passed, {failures} failed");
 return failures==0?0:1;
}
}
