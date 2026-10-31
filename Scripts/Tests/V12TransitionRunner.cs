using Godot;
using Magicians.Presentation;
namespace Magicians.Tests;
public partial class V12TransitionRunner : Node
{
    private static IEnumerable<Node> Walk(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach(var x in Walk(c)) yield return x; } }
    private async Task Check(V12MatchController scene, Task action)
    {
        var frames = 0;
        while (!action.IsCompleted)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            foreach (var cover in Walk(scene).OfType<TextureRect>().Where(c => c.GetParent() is CanvasLayer && c.Visible))
            {
                if (cover.ExpandMode != TextureRect.ExpandModeEnum.IgnoreSize || cover.GetGlobalRect().Size.DistanceTo(scene.GetViewportRect().Size) > 1)
                    throw new Exception("Transition overlay distorts viewport geometry");
                frames++;
            }
        }
        await action;
        if (scene.LastError is not null || frames == 0) throw new Exception("Graphical transition failed or was not exercised");
    }
    public override async void _Ready()
    {
        try
        {
            var scene=GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>(); scene.AutoStart=false; AddChild(scene);
            for(var i=0;i<5;i++) await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await Check(scene,scene.StartMatchAsync(334020,new[]{"fire","water","earth"}));
            var offer=scene.LastState.GetProperty("decision").GetProperty("offers").EnumerateArray().First(c=>c.GetProperty("affordable").GetBoolean());
            await Check(scene,scene.SubmitAsync("buy",slot:offer.GetProperty("slot").GetInt32()));
            var transition = (Task)typeof(V12MatchController).GetMethod("DraftTransition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(scene, new object[] { "finish" })!;
            await Check(scene, transition);
            if (scene.LastState.GetProperty("decision").GetProperty("kind").GetString() != "play") throw new Exception("Market did not switch directly to battle");
            var card=scene.LastState.GetProperty("decision").GetProperty("hand").EnumerateArray().First(c=>c.GetProperty("payable_cost").GetInt32()<=3);
            await Check(scene,scene.SubmitAsync("play",uid:card.GetProperty("uid").GetString()));
            GD.Print("V12_TRANSITION_OK graphical-buy-play overlay-viewport-size=OK"); GetTree().Quit();
        }
        catch(Exception e) { GD.PrintErr(e);GetTree().Quit(1); }
    }
}
