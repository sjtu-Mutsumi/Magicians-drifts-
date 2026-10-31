using System.Text.Json;
using Magicians.Native;
namespace Magicians.Tooling;

internal static class ProtocolRegression
{
    public static object Run(string root)
    {
        using var worker=new ReferenceWorker(root);var count=0;
        foreach(var row in TestCommands.Lines(Path.Combine(root,"tools/Magicians.Tooling/Testing/Fixtures/protocol.jsonl.gz")))
        {
            var actual=worker.Request(row.GetProperty("request"));var expected=row.GetProperty("response");
            if(row.GetProperty("outer_error").GetBoolean())
            {
                if(actual.GetProperty("status").GetString()!="error"||!actual.GetProperty("error").GetString()!.Contains(expected.GetProperty("error").GetString()!,StringComparison.Ordinal))
                    throw new InvalidDataException("Protocol rejection mismatch: "+row.GetProperty("label").GetString());
            }
            else TestCommands.Compare(expected,actual,"protocol."+row.GetProperty("label").GetString());
            if(actual.TryGetProperty("standings",out var standings))foreach(var player in standings.EnumerateArray())
                if(player.TryGetProperty("deck",out _)||player.TryGetProperty("policy",out _))throw new InvalidDataException("Opponent private data leak");
            if(actual.TryGetProperty("review",out var review))foreach(var battle in review.GetProperty("battles").EnumerateArray())
            {
                if(battle.TryGetProperty("participants",out _))throw new InvalidDataException("Private review participants leak");
                foreach(var e in battle.GetProperty("log").EnumerateArray())
                    if(new[]{"draw","sequences_locked","random_insert"}.Contains(e.GetProperty("kind").GetString()))throw new InvalidDataException("Private review event leak");
            }
            count++;
        }
        if(count!=27)throw new InvalidDataException("Expected all 27 reference protocol requests");
        var catalog=NativeBridge.Respond(root,JsonSerializer.SerializeToElement(new{op="catalog"}));
        if(catalog.GetProperty("cards").GetArrayLength()!=183||catalog.GetProperty("routes").EnumerateObject().Count()!=15)throw new InvalidDataException("Protocol catalog incomplete");
        var request=JsonSerializer.SerializeToElement(new{decks=new[]{Catalog.START,Catalog.START},seed=17});
        var first=NativeBridge.Respond(root,request);var second=NativeBridge.Respond(root,request);
        TestCommands.Compare(first,second,"deterministic-client-decision");
        foreach(var e in first.GetProperty("log").EnumerateArray())if(new[]{"draw","sequences_locked"}.Contains(e.GetProperty("kind").GetString()))throw new InvalidDataException("Private battle event leak");
        return new{requests=count,catalog_cards=183,routes=15,privacy="passed",deterministic_decision="passed"};
    }
}
