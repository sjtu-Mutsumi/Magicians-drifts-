using System.Text.Json;
namespace Magicians.Native;

public class Player
{
    public int pid;
    public string preference;
    public List<(string name,string? token)> deck = Catalog.START.Select(n => (n,(string?)null)).ToList();
    public string spec = "";
    public int hp=100, streak, bank, points, income, carry, bonus;
    public bool free_used;
    public string style="balanced";
    public JsonElement? policy;
    public HashSet<string> bought_elements=new(StringComparer.Ordinal);
    public List<Dictionary<string,object?>> actions=new();
    public Player(int pid,string preference) { this.pid=pid;this.preference=preference; }
    public string route => deck.Select(x=>x.name).FirstOrDefault(n=>Catalog.CARDS[n].kind=="route_legendary") ?? "";
    public Player Clone() => new(pid,preference) { deck=new(deck),spec=spec,hp=hp,streak=streak,bank=bank,points=points,income=income,carry=carry,bonus=bonus,free_used=free_used,style=style,policy=policy,bought_elements=new(bought_elements,StringComparer.Ordinal),actions=actions.Select(a=>new Dictionary<string,object?>(a)).ToList() };
}

internal static class NJson
{
    internal static JsonElement Read(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Catalog.ROOT,relative))).RootElement.Clone();
    internal static double Num(JsonElement obj,string key,double fallback=0) => obj.ValueKind==JsonValueKind.Object && obj.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.Number ? value.GetDouble() : fallback;
    internal static string Str(JsonElement obj,string key,string fallback="") => obj.ValueKind==JsonValueKind.Object && obj.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString()! : fallback;
    internal static JsonElement Obj(JsonElement obj,string key) => obj.ValueKind==JsonValueKind.Object && obj.TryGetProperty(key,out var value) ? value : default;
    internal static bool Has(JsonElement obj,string key) => obj.ValueKind==JsonValueKind.Object && obj.TryGetProperty(key,out var value) && value.ValueKind!=JsonValueKind.Null && value.ValueKind!=JsonValueKind.False;
    internal static Dictionary<string,object?> D(params (string key,object? value)[] pairs) => pairs.ToDictionary(x=>x.key,x=>x.value,StringComparer.Ordinal);
    internal static int Count<TKey>(IDictionary<TKey,int> source,TKey key) where TKey:notnull => source.TryGetValue(key,out var value)?value:0;
    internal static void Inc(IDictionary<string,int> source,string key,int value=1) => source[key]=Count(source,key)+value;
    internal static string Focus(IEnumerable<string> names,string preference,bool enchantmentsOnly=false)
    {
        var counts=names.Where(n=>!enchantmentsOnly||Catalog.CARDS[n].enchantment).GroupBy(n=>Catalog.CARDS[n].element).ToDictionary(g=>g.Key,g=>g.Count());
        return Catalog.ELEMENTS.OrderByDescending(e=>counts.GetValueOrDefault(e)).ThenByDescending(e=>e==preference).First();
    }
}
