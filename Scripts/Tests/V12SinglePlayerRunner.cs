using Godot;
using Magicians.Presentation;
using System.Text.Json;

namespace Magicians.Tests;

public partial class V12SinglePlayerRunner : Node
{
    public override async void _Ready()
    {
        try
        {
            var scene = GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>();
            scene.AutoStart = false; AddChild(scene);
            await scene.StartMatchAsync(334004, new[] { "fire", "water", "earth" });
            var kinds = new HashSet<string>(); var steps = 0;
            while (scene.LastState.ValueKind != JsonValueKind.Undefined && scene.LastState.GetProperty("status").GetString() == "decision")
            {
                if (++steps > 1000) throw new Exception("Nonterminating single player");
                var state = scene.LastState; var d = state.GetProperty("decision");
                var kind = d.GetProperty("kind").GetString()!; kinds.Add(kind);
                switch (kind)
                {
                    case "draft":
                        var cards = d.GetProperty("offers").EnumerateArray().Where(c => c.GetProperty("affordable").GetBoolean()).ToArray();
                        if (cards.Length > 0 && state.GetProperty("round").GetInt32() != 1)
                            await scene.SubmitAsync("buy", slot: cards[0].GetProperty("slot").GetInt32());
                        else await scene.SubmitAsync("end_face"); break;
                    case "refresh": await scene.SubmitAsync("refresh"); break;
                    case "delete": await scene.SubmitAsync("delete", index: d.GetProperty("legal")[0].GetInt32()); break;
                    case "play":
                        var hand = d.GetProperty("hand").EnumerateArray().Where(c => c.GetProperty("payable_cost").GetInt32() <= d.GetProperty("energy").GetInt32()).ToArray();
                        await scene.SubmitAsync("play", uid: hand.Length == 0 ? null : hand[0].GetProperty("uid").GetString()); break;
                    case "select":
                        await scene.SubmitAsync("select", uids: d.GetProperty("candidates").EnumerateArray().Take(d.GetProperty("count").GetInt32()).Select(c => c.GetProperty("uid").GetString()!).ToArray()); break;
                    case "round":
                        await scene.SubmitAsync(state.GetProperty("round").GetInt32() >= 8 ? "delegate_match" : "next_round"); break;
                    default: throw new Exception("Unknown decision " + kind);
                }
                if (scene.LastError is not null) throw new Exception(scene.LastError);
            }
            if (scene.LastState.GetProperty("status").GetString() != "finished" || scene.LastState.GetProperty("placements").GetArrayLength() != 4)
                throw new Exception("Missing final rankings");
            if (!new[] { "draft", "refresh", "play", "select", "round" }.All(kinds.Contains))
                throw new Exception("Missing UI phase: " + string.Join(',', kinds));
            var path = ProjectSettings.GlobalizePath("res://runtime/tests"); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "godot-single-player-result.json"), scene.LastState.GetRawText());
            GD.Print($"V12_SINGLE_PLAYER_OK manual_steps={steps} phases={string.Join(',', kinds)} ranks=4");
            scene.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
