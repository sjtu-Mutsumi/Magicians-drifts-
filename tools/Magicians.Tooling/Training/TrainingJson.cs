using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

internal static class TJ
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented=true, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static JsonObject O(params (string key,object? value)[] values) => (JsonObject)NativeJson.Node(values.ToDictionary(x=>x.key,x=>x.value))!;
    public static JsonArray A(IEnumerable<object?> values) => (JsonArray)NativeJson.Node(values)!;
    public static double N(this JsonNode? node,string key,double fallback=0)=>node is JsonObject o&&o.TryGetPropertyValue(key,out var value)?Number(value,fallback):fallback;
    public static double Number(JsonNode? n,double fallback=0)
    {
        if(n is null)return fallback;
        if(n is JsonValue v){if(v.TryGetValue<double>(out var d))return d;if(v.TryGetValue<int>(out var i))return i;if(v.TryGetValue<long>(out var l))return l;}
        return double.TryParse(n.ToJsonString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var number)?number:fallback;
    }
    public static int I(this JsonNode? n,string key,int fallback=0)=>(int)n.N(key,fallback);
    public static string S(this JsonNode? n,string key,string fallback="")=>n?[key]?.GetValue<string>()??fallback;
    public static bool B(this JsonNode? n,string key)=>n?[key]?.GetValue<bool>()??false;
    public static IEnumerable<JsonObject> Rows(this JsonNode? node)=>node is JsonArray a?a.OfType<JsonObject>():Enumerable.Empty<JsonObject>();
    public static string[] Strings(this JsonNode? node)=>node is JsonArray a?a.Select(x=>x!.GetValue<string>()).ToArray():Array.Empty<string>();
    public static HashSet<int> Seats(JsonObject room,bool first=true)
    {var seats=room["comparison"]!["first_seats"]!.AsArray().Select(x=>(int)Number(x)).ToHashSet();return first?seats:Enumerable.Range(0,4).Except(seats).ToHashSet();}
    public static IEnumerable<JsonObject> Battles(JsonObject room)=>room["rounds"].Rows().SelectMany(r=>r["battles"].Rows());
    public static IEnumerable<(int seat,JsonObject player)> Players(JsonObject battle)=>battle["participants"]!.AsArray().Select((p,i)=>(i,p!.AsObject())).Where(x=>x.i==0||!battle.B("ghost"));
    public static int? Winner(JsonObject battle)=>battle["winner"] is null?null:(int)Number(battle["winner"]);
    public static double Win(JsonObject battle,int i)=>Winner(battle) is int winner?(winner==i?1:0):.5;
    public static JsonObject Clone(JsonObject source)=>(JsonObject)source.DeepClone();
    public static JsonObject Read(string path)
    {using var f=File.OpenRead(path);if(path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)){using var gzip=new GZipStream(f,CompressionMode.Decompress);return JsonNode.Parse(gzip)!.AsObject();}return JsonNode.Parse(f)!.AsObject();}
    public static void Save(string path,object? value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var node=NativeJson.Node(value);
        if(path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)){using var f=File.Create(path);using var gzip=new GZipStream(f,CompressionLevel.Optimal);JsonSerializer.Serialize(gzip,node);}
        else File.WriteAllText(path,(node?.ToJsonString(Pretty)??"null")+"\n",new System.Text.UTF8Encoding(false));
    }
    public static void Inc(this Dictionary<string,double> book,string key,double value=1)=>book[key]=book.GetValueOrDefault(key)+value;
    public static double Div(double n,double d)=>n/Math.Max(1,d);
    public static object? NullableDiv(double n,double d)=>d!=0?n/d:null;
    public static Dictionary<string,double> Counter()=>new(StringComparer.Ordinal);
    public static Dictionary<string,double> Counter(JsonNode? n)=>n is JsonObject o?o.ToDictionary(x=>x.Key,x=>Number(x.Value),StringComparer.Ordinal):Counter();
    public static string Hash(string file)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
}

internal sealed class TrainingArgs
{
    private readonly string[] args;
    public TrainingArgs(string[] values)=>args=values;
    public string? Get(string key){int i=Array.IndexOf(args,"--"+key);if(i<0)return null;if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("--"+key+" requires a value");return args[i+1];}
    public string Required(string key)=>Get(key)??throw new ArgumentException("Missing --"+key);
    public int Int(string key,int value)=>Get(key) is string s?int.Parse(s,CultureInfo.InvariantCulture):value;
    public bool Has(string key)=>args.Contains("--"+key,StringComparer.Ordinal);
}
