using Magicians.Native;
using System.Text.Json;

Catalog.Initialize(args.Length>0?args[0]:Path.GetFullPath("../.."));
if(args.Contains("--mechanics")){MechanicTests.Run();return;}
foreach(var route in Policy.TARGETS.Keys)
{
    var own=((System.Collections.IEnumerable)Policy.TARGETS[route]["entries"]!).Cast<string>().ToArray();
    var opponent=Catalog.START.Concat(new[]{"火花","猛火弹","引火"}).ToArray();
    var b=new StrictBattle(new[]{own,opponent},new[]{Catalog.CARDS[route].element,"fire"},123,trace:true,styles:new[]{"balanced","balanced"});
    var result=b.run();Console.WriteLine(JsonSerializer.Serialize(new {route,result}));
}
