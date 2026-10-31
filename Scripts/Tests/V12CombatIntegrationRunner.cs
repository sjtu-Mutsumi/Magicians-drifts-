using Godot;
using Magicians.Application;
using System.Text.Json;

namespace Magicians.Tests;

public partial class V12CombatIntegrationRunner : Node
{
    public override async void _Ready()
    {
        try
        {
            var root = ProjectSettings.GlobalizePath("res://");
            var catalog = await V12CombatRuntime.Request(root, new { op = "catalog" });
            if (catalog.GetProperty("cards").GetArrayLength() != 183) throw new Exception("Wrong catalog");
            var routes = catalog.GetProperty("routes").EnumerateObject().ToArray();
            var decks = new[] { routes[0], routes[^1] }.Select(r => r.Value.GetProperty("entries").EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
            var commands = new List<object>();
            for (var step = 0; step < 500; step++)
            {
                var result = await V12CombatRuntime.Request(root, new { decks, commands, seed = 12001, ai_style = "flexible" });
                if (result.GetProperty("status").GetString() == "finished")
                {
                    if (!result.GetProperty("executed").EnumerateObject().Any()) throw new Exception("No cards executed");
                    GD.Print($"V12_CLIENT_INTEGRATION_OK cards=183 commands={commands.Count} winner={result.GetProperty("winner")}");
                    GetTree().Quit(); return;
                }
                if (result.GetProperty("public")[1].TryGetProperty("hand", out _)) throw new Exception("Opponent hand leaked");
                var decision = result.GetProperty("decision");
                if (decision.GetProperty("kind").GetString() == "select")
                    commands.Add(new { kind = "select", uids = decision.GetProperty("candidates").EnumerateArray().Take(decision.GetProperty("count").GetInt32()).Select(x => x.GetProperty("uid").GetString()).ToArray() });
                else
                {
                    var energy = decision.GetProperty("energy").GetInt32();
                    var candidates = decision.GetProperty("hand").EnumerateArray().Where(x => x.GetProperty("payable_cost").GetInt32() <= energy).ToArray();
                    commands.Add(new { kind = "play", uid = candidates.Length == 0 ? null : candidates[0].GetProperty("uid").GetString() });
                }
            }
            throw new Exception("Battle did not terminate");
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
