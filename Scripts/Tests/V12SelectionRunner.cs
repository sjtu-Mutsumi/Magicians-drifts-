using Godot;
using Magicians.Presentation;
namespace Magicians.Tests;
public partial class V12SelectionRunner : Node
{
    private static IEnumerable<Node> Walk(Node n) { foreach(var c in n.GetChildren()){yield return c;foreach(var x in Walk(c))yield return x;} }
    public override async void _Ready()
    {
        try
        {
            var scene=GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>();scene.AutoStart=false;AddChild(scene);
            await scene.StartMatchAsync(334004,new[]{"fire","water","earth"});
            var found=false;
            for(var i=0;i<250;i++)
            {
                var d=scene.LastState.GetProperty("decision");var kind=d.GetProperty("kind").GetString();
                if(kind=="select")
                {
                    if(d.GetProperty("candidates").GetArrayLength()<2)throw new Exception("Trivial choice should auto-complete");
                    var button=Walk(scene).OfType<Button>().First(b=>b.Text=="选择 / 取消");button.EmitSignal(Button.SignalName.Pressed);
                    var selected=Walk(scene).OfType<CardView>().Where(c=>((StyleBoxFlat)c.GetThemeStylebox("panel")).BorderColor==new Color("ffd166")).ToArray();
                    if(selected.Length!=1)throw new Exception("Selected border not highlighted");
                    button.EmitSignal(Button.SignalName.Pressed);
                    if(Walk(scene).OfType<CardView>().Any(c=>((StyleBoxFlat)c.GetThemeStylebox("panel")).BorderColor==new Color("ffd166")))throw new Exception("Border did not clear");
                    found=true;break;
                }
                if(kind is "draft" or "refresh")await scene.SubmitAsync("delegate_round");
                else if(kind=="round")await scene.SubmitAsync("next_round");
                else if(kind=="play")
                {
                    var hand=d.GetProperty("hand").EnumerateArray().Where(c=>c.GetProperty("payable_cost").GetInt32()<=d.GetProperty("energy").GetInt32()).ToArray();
                    await scene.SubmitAsync("play",uid:hand.Length==0?null:hand[0].GetProperty("uid").GetString());
                }
                if(scene.LastError is not null)throw new Exception(scene.LastError);
            }
            if(!found)throw new Exception("No multi-target selection exercised");
            scene.QueueFree();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("V12_SELECTION_OK highlight=OK deselect=OK trivial-choices-skipped=OK");GetTree().Quit();
        }
        catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}
    }
}
