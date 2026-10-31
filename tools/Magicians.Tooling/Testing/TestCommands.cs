using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

public static class TestCommands
{
    public static readonly string[] Commands={"test","test-oracle","test-business","test-protocol","capture-oracle","diff-oracle","protocol"};
    private static string Fixture(string root,string name)=>Path.Combine(root,"tools/Magicians.Tooling/Testing/Fixtures",name);
    private static string Option(string[] args,string name,string fallback)
    {var i=Array.IndexOf(args,name);return i<0?fallback:i+1<args.Length?args[i+1]:throw new ArgumentException("Missing "+name);}
    public static int Run(string command,string root,string[] args)
    {
        if(command=="protocol")return Protocol(root);
        if(command=="capture-oracle")return Capture(root,args);
        VerifyFixtures(root);
        var timer=Stopwatch.StartNew();var reports=new Dictionary<string,object?>();
        if(command=="test")
        {FoundationRegression.Run(root);MechanicTests.Run();reports["foundation"]="passed";reports["mechanics"]="passed";reports["static"]=StaticRegression.Run(root);reports["training"]=TrainingCommands.SelfTest(root);}
        if(command is "test" or "test-oracle" or "diff-oracle")reports["oracle"]=Oracle(root,args);
        if(command is "test" or "test-business")reports["business"]=Business(root,args);
        if(command is "test" or "test-protocol")reports["protocol"]=ProtocolRegression.Run(root);
        reports["status"]="passed";reports["python_required"]=false;reports["seconds"]=timer.Elapsed.TotalSeconds;
        var folder=Path.Combine(root,"runtime/csharp-tooling");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"regression-audit.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("CSHARP_REGRESSIONS_OK "+JsonSerializer.Serialize(reports));return 0;
    }

    private static void VerifyFixtures(string root)
    {
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(Fixture(root,"manifest.json")));
        foreach(var row in manifest.RootElement.GetProperty("sha256").EnumerateObject())
        {using var input=File.OpenRead(Fixture(root,row.Name));if(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!=row.Value.GetString())throw new InvalidDataException("Frozen reference fixture changed: "+row.Name);}
    }
    internal static IEnumerable<JsonElement> Lines(string file)
    {
        using var input=File.OpenRead(file);
        using Stream stream=file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?new GZipStream(input,CompressionMode.Decompress):input;
        using var reader=new StreamReader(stream,Encoding.UTF8);
        while(reader.ReadLine() is {} line){using var json=JsonDocument.Parse(line);yield return json.RootElement.Clone();}
    }
    private static object Oracle(string root,string[] args)
    {
        var kind=Option(args,"--kind","all");var limit=int.Parse(Option(args,"--limit","0"));
        var file=Option(args,"--expected",Fixture(root,"reference-oracle.jsonl.gz"));
        var counts=new Dictionary<string,int>();using var worker=new ReferenceWorker(root);
        foreach(var row in Lines(file))
        {
            var k=row.GetProperty("kind").GetString()!;if(kind!="all"&&kind!=k)continue;
            if(limit>0&&counts.Values.Sum()>=limit)break;
            try {ReplayOracle(worker,row);}
            catch(Exception e){throw new InvalidDataException($"Reference {k} {row.GetRawText()[..Math.Min(90,row.GetRawText().Length)]}: {e.Message}",e);}
            counts[k]=counts.GetValueOrDefault(k)+1;
            if(counts.Values.Sum()%100==0)Console.WriteLine("ORACLE_PROGRESS "+counts.Values.Sum());
        }
        if(counts.Count==0)throw new InvalidOperationException("No oracle cases selected");
        return counts;
    }
    private static void ReplayOracle(ReferenceWorker worker,JsonElement row)
    {
        switch(row.GetProperty("kind").GetString())
        {
            case "card":
                var expected=JsonNode.Parse(row.GetRawText())!.AsObject();expected.Remove("producer");
                Compare(JsonSerializer.SerializeToElement(expected),worker.Request(new {op="card_fixture",name=row.GetProperty("name").GetString(),rich=row.GetProperty("rich").GetBoolean()}),"card");break;
            case "battle":Compare(row.GetProperty("result"),worker.Request(new {op="battle_full",decks=row.GetProperty("decks"),specializations=row.GetProperty("specializations"),seed=row.GetProperty("seed")}),"battle");break;
            case "session":
                foreach(var exchange in row.GetProperty("exchanges").EnumerateArray())Compare(exchange.GetProperty("response"),worker.Request(exchange.GetProperty("request")),"session");
                foreach(var review in row.GetProperty("reviews").EnumerateArray())Compare(review,worker.Request(new {op="review",round=review.GetProperty("review").GetProperty("round")}),"review");
                break;
            default:throw new ArgumentException("Unknown fixture kind");
        }
    }
    private static object Business(string root,string[] args)
    {
        var limit=int.Parse(Option(args,"--limit","0"));var count=0;var tests=new HashSet<string>();
        foreach(var row in Lines(Fixture(root,"business-operations.jsonl.gz")))
        {
            if(limit>0&&count>=limit)break;
            try {BusinessReplay.Execute(row);}
            catch(Exception e){throw new InvalidDataException($"Business case {count}: {row.GetProperty("test").GetString()} / {row.GetProperty("method").GetString()}: {e.Message}",e);}
            count++;tests.Add(row.GetProperty("test").GetString()!);
        }
        return new {operations=count,original_test_names=tests.Count};
    }
    public static void Compare(JsonElement expected,JsonElement actual,string path,bool ignoreStateMetadata=false)
    {
        if(expected.ValueKind==JsonValueKind.Object&&actual.ValueKind==JsonValueKind.Object)
        {
            var keys=expected.EnumerateObject().Select(x=>x.Name).Where(k=>!ignoreStateMetadata||k is not ("styles" or "policy_configs" or "phase" or "trace" or "projecting")).ToHashSet(StringComparer.Ordinal);
            var actualKeys=actual.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal);
            if(!keys.SetEquals(actualKeys))throw new InvalidDataException(path+" keys differ: missing="+string.Join(',',keys.Except(actualKeys))+" extra="+string.Join(',',actualKeys.Except(keys)));
            foreach(var key in keys)Compare(expected.GetProperty(key),actual.GetProperty(key),path+"."+key);
            return;
        }
        if(expected.ValueKind==JsonValueKind.Array&&actual.ValueKind==JsonValueKind.Array)
        {
            if(expected.GetArrayLength()!=actual.GetArrayLength())throw new InvalidDataException(path+" lengths "+expected.GetArrayLength()+" != "+actual.GetArrayLength());
            for(var i=0;i<expected.GetArrayLength();i++)Compare(expected[i],actual[i],path+"["+i+"]");return;
        }
        if(expected.ValueKind==JsonValueKind.Number&&actual.ValueKind==JsonValueKind.Number)
        {if(Q.Parse(expected.GetRawText())==Q.Parse(actual.GetRawText()))return;}
        else if(expected.ValueKind==actual.ValueKind&&expected.GetRawText()==actual.GetRawText())return;
        else if(expected.ValueKind==JsonValueKind.String&&actual.ValueKind==JsonValueKind.String&&expected.GetString()==actual.GetString())return;
        throw new InvalidDataException(path+" expected "+expected.GetRawText()+" actual "+actual.GetRawText());
    }
    private static int Capture(string root,string[] args)
    {
        var output=Path.GetFullPath(Option(args,"--out",Path.Combine(root,"runtime/csharp-tooling/native-capture.jsonl")));
        var fixtureFolder=Path.GetFullPath(Fixture(root,""));
        if(output.StartsWith(fixtureFolder,StringComparison.OrdinalIgnoreCase)||File.Exists(output))throw new ArgumentException("Capture requires a NEW output outside frozen reference fixtures");
        VerifyFixtures(root);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var worker=new ReferenceWorker(root);using var file=new StreamWriter(output,false,new UTF8Encoding(false));
        var limit=int.Parse(Option(args,"--limit","0"));var count=0;
        foreach(var original in Lines(Fixture(root,"reference-oracle.jsonl.gz")))
        {
            if(limit>0&&count>=limit)break;count++;
            var row=JsonNode.Parse(original.GetRawText())!.AsObject();var kind=row["kind"]!.GetValue<string>();
            if(kind=="card")row=JsonNode.Parse(worker.Request(new {op="card_fixture",name=row["name"]!.GetValue<string>(),rich=row["rich"]!.GetValue<bool>()}).GetRawText())!.AsObject();
            else if(kind=="battle")row["result"]=JsonNode.Parse(worker.Request(new {op="battle_full",decks=original.GetProperty("decks"),specializations=original.GetProperty("specializations"),seed=original.GetProperty("seed")}).GetRawText());
            else
            {
                foreach(var exchange in row["exchanges"]!.AsArray())exchange!["response"]=JsonNode.Parse(worker.Request(JsonSerializer.SerializeToElement(exchange["request"])).GetRawText());
                var reviews=new JsonArray();foreach(var review in original.GetProperty("reviews").EnumerateArray())reviews.Add(JsonNode.Parse(worker.Request(new {op="review",round=review.GetProperty("review").GetProperty("round")}).GetRawText()));row["reviews"]=reviews;
            }
            row["producer"]="native-csharp-current";file.WriteLine(row.ToJsonString());
        }
        Console.WriteLine("Captured current C# output; this does NOT replace or regenerate the original reference authority: "+output);return 0;
    }
    private static int Protocol(string root)
    {
        using var worker=new ReferenceWorker(root);
        while(Console.ReadLine() is {} line){using var input=JsonDocument.Parse(line);Console.WriteLine(worker.Request(input.RootElement).GetRawText());}
        return 0;
    }
}

internal sealed class ReferenceWorker(string root):IDisposable
{
    private NativeMatchSession? session;
    public JsonElement Request(object request)
    {
        var r=request is JsonElement element?element:JsonSerializer.SerializeToElement(request);
        try
        {
            switch(NativeJson.String(r,"op"))
            {
                case "card_fixture":return ReferenceFixtures.Card(r);
                case "battle_full":return ReferenceFixtures.Battle(r);
                case "start":session?.Dispose();session=new NativeMatchSession(root,r.TryGetProperty("seed",out var seed)?BigInteger.Parse(seed.GetRawText()):BigInteger.One,r.TryGetProperty("ai_styles",out var styles)?styles.EnumerateArray().Select(x=>x.GetString()!).ToArray():new[]{"fire","water","poison"});return session.Receive();
                case "review":return (session??throw new ArgumentException("Start a match first")).Review(r.GetProperty("round").GetInt32());
                case "catalog":case "battle":return NativeBridge.Respond(root,r);
                case "simulate":
                    var configs=r.TryGetProperty("ai_styles",out var ai)?ai.EnumerateArray().Select(x=>(JsonElement?)SixStyles.load_policy(x.GetString()!)).ToArray():null;
                    return NativeJson.Element(new Tournament().simulate(r.TryGetProperty("seed",out var tournamentSeed)?BigInteger.Parse(tournamentSeed.GetRawText()):BigInteger.One,trace:true,policy_by_seat:configs));
                default:return (session??throw new ArgumentException("Start a match first")).Send(r);
            }
        }
        catch(Exception ex){return NativeJson.Element(NativeJson.Obj(("status","error"),("error",ex.Message)));}
    }
    public void Dispose()=>session?.Dispose();
}
