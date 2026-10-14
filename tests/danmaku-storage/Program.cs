using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MpvNet;
using MpvNet.Native;

string native=Path.GetFullPath(args[0]),source=Path.GetFullPath(args[1]);
NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly,(name,_,_)=>File.Exists(Path.Combine(native,name))?NativeLibrary.Load(Path.Combine(native,name)):IntPtr.Zero);
int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
Check(Native.GetACP()==65001,"Run the test executable with the player's actual UTF-8 manifest, rather than dotnet.exe");
string folder=Path.GetFullPath(args[2]);
Check(Environment.GetEnvironmentVariable("LOCALAPPDATA")==folder,"The parent process must isolate the native CRT environment before startup");
nint core=0,client=0;
try
{
 string data=Path.Combine(folder,"mpv-AnimeFusion","Danmaku");
 string settings=Path.Combine(data,"设置");Directory.CreateDirectory(settings);
 File.WriteAllText(Path.Combine(settings,"mpv-AnimeFusion-danmaku.conf"),"api_servers=http://127.0.0.1:9|隔离回归\n",new UTF8Encoding(false));
 File.WriteAllText(Path.Combine(settings,"mpv-AnimeFusion-DanmakuFactory.json"),"{\"fps\":90}",new UTF8Encoding(false));
 string episode=Path.Combine(data,"示例动漫","第 2 季","第 1 集.xml");Directory.CreateDirectory(Path.GetDirectoryName(episode)!);
 string renamed=Path.ChangeExtension(episode,"ass");
 string Quote(string value)=>"[["+value.Replace('\\','/')+"]]";
 string driver=Path.Combine(folder,"driver.lua");
 File.WriteAllText(driver,"dofile("+Quote(source)+")\nlocal f=assert(io.open("+Quote(episode)+",'wb'))\nf:write('UTF-8 弹幕');f:close()\nassert(os.rename("+Quote(episode)+","+Quote(renamed)+"))\nf=assert(io.open("+Quote(renamed)+",'rb'))\nlocal text=f:read('*a');f:close()\nassert(os.remove("+Quote(renamed)+"))\nmp.set_property_native('user-data/storage-check',{text=text})\n",new UTF8Encoding(false));
 core=LibMpv.mpv_create();
 byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value+"\0");
 foreach(var option in new Dictionary<string,string>{{"config","no"},{"load-scripts","no"},{"vo","null"},{"ao","null"},{"idle","yes"},{"force-window","no"},{"scripts",driver}})
  Check(LibMpv.mpv_set_option_string(core,Bytes(option.Key),Bytes(option.Value))>=0,"Native option accepted: "+option.Key);
 Check((int)LibMpv.mpv_initialize(core)>=0,"Installed libmpv initialized without a desktop window");
 client=LibMpv.mpv_create_client(core,"danmaku-storage-check");
 var player=Global.Player;player.MainHandle=core;player.Handle=client;
 string result="";var deadline=DateTime.UtcNow.AddSeconds(5);
 while(DateTime.UtcNow<deadline){result=player.GetPropertyString("user-data/storage-check");if(result.Length>0)break;Thread.Sleep(20);}
 Check(result.Length>0,"Lua completed Unicode file I/O in the installed native runtime");
 Check(JsonDocument.Parse(result).RootElement.GetProperty("text").GetString()=="UTF-8 弹幕","Write, rename, read and remove preserve Chinese file paths and contents");
 var state=JsonDocument.Parse(player.GetPropertyString("user-data/player_ui/danmaku")).RootElement;
 Check(state.GetProperty("servers").GetArrayLength()==1,"Production autoloader reads its route configuration from the Chinese settings directory");
 Check(state.GetProperty("settings").GetProperty("fps").GetInt32()==90,"Production renderer reads settings from the same directory");
 Check(state.GetProperty("autoload_state").GetString()=="idle","Storage checks do not start network matching or request a video");
 Check(!File.Exists(episode)&&!File.Exists(renamed),"Native cleanup removed the exact generated fixture files");
 Console.WriteLine($"Native danmaku storage checks passed: {checks}; installed libmpv and production Lua; isolated data; no desktop or service requests.");
}
finally
{
 if(client!=0)LibMpv.mpv_destroy(client);
 if(core!=0)LibMpv.mpv_destroy(core);
 if(Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(folder).StartsWith("AnimeFusion-DanmakuStorage-"))Directory.Delete(folder,true);
}
static class Native{[DllImport("kernel32.dll")]internal static extern uint GetACP();}
