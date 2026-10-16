using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

string native=Environment.GetEnvironmentVariable("ANIMEFUSION_NATIVE_DIRECTORY")??throw new Exception("Native runtime path required");
string root=Path.GetFullPath(args.Length>0?args[0]:Environment.CurrentDirectory);
string script=Path.Combine(root,"tests","test_subtitle_menu_native.lua");
string temp=Path.Combine(Path.GetTempPath(),"AnimeFusion-SubtitleMenuLayout-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string report=Path.Combine(temp,"report.json");
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,(name,_,_)=>File.Exists(Path.Combine(native,name))?NativeLibrary.Load(Path.Combine(native,name)):IntPtr.Zero);
nint core=Native.mpv_create();
if(core==0)throw new Exception("Unable to create native instance");
try
{
    foreach(var option in new Dictionary<string,string>{{"config","no"},{"load-scripts","no"},{"vo","null"},{"ao","null"},{"idle","yes"},{"force-window","no"},{"terminal","yes"},{"msg-level","all=warn"},{"script-opts","subtitle-layout-root="+root.Replace('\\','/')+",subtitle-layout-report="+report.Replace('\\','/')}})
        if(Native.mpv_set_option_string(core,option.Key,option.Value)<0)throw new Exception("Native option rejected: "+option.Key);
    if(Native.mpv_initialize(core)<0)throw new Exception("Native initialization failed");
    if(Native.mpv_command_string(core,"load-script \""+script.Replace('\\','/')+"\"")<0)throw new Exception("Native test script rejected");
    var elapsed=Stopwatch.StartNew();
    while(!File.Exists(report)&&elapsed.Elapsed<TimeSpan.FromSeconds(30))Native.mpv_wait_event(core,.05);
    if(!File.Exists(report))throw new Exception("Native layout checks did not return a report");
    string result=File.ReadAllText(report);
    Console.WriteLine(result);
    using var json=JsonDocument.Parse(result);
    if(!json.RootElement.GetProperty("ok").GetBoolean())throw new Exception(json.RootElement.GetProperty("error").GetString());
    Console.WriteLine("Native subtitle menu layout passed without a desktop window or media request.");
}
finally
{
    Native.mpv_terminate_destroy(core);
    File.Delete(report);
    Directory.Delete(temp);
}

static class Native
{
    const string Dll="libmpv-2.dll";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern nint mpv_create();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern int mpv_initialize(nint core);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern int mpv_set_option_string(nint core,[MarshalAs(UnmanagedType.LPUTF8Str)] string name,[MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern int mpv_command_string(nint core,[MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern nint mpv_wait_event(nint core,double timeout);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(nint core);
}
