using Godot;
using Magicians.Presentation;
namespace Magicians.Tests;
public partial class V12DeletionRunner : Node
{
    private static IEnumerable<Node> Walk(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var x in Walk(c)) yield return x; } }
    public override async void _Ready()
    {
        try
        {
            var scene = GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>(); scene.AutoStart=false; AddChild(scene);
            await scene.StartMatchAsync(334011,new[]{"fire","water","earth"});
            for(var step=0;step<60;step++)
            {
                var state=scene.LastState;var d=state.GetProperty("decision");var kind=d.GetProperty("kind").GetString();
                if(kind=="draft" && state.GetProperty("round").GetInt32()>=7 && d.GetProperty("can_delete").GetBoolean())break;
                await scene.SubmitAsync(kind switch {"draft" or "refresh"=>"delegate_round","play" or "select"=>"delegate_battle","round"=>"next_round",_=>throw new Exception("Unexpected phase")});
                if(scene.LastError is not null)throw new Exception(scene.LastError);
            }
            var deleteButton=Walk(scene).OfType<Button>().Single(b=>b.Text=="删牌");
            if (deleteButton.GetThemeStylebox("normal") is not StyleBoxFlat style || style.BorderColor != new Color("ffd166")) throw new Exception("Free deletion is not highlighted");
            if (!Walk(scene).OfType<Label>().Any(l=>l.Text.Contains("上轮赛果："))) throw new Exception("Previous results absent");
            var before=scene.LastState;var revision=before.GetProperty("revision").GetInt64();var count=before.GetProperty("own").GetProperty("deck").GetArrayLength();
            Walk(scene).OfType<Button>().Single(b=>b.Text=="删牌").EmitSignal(Button.SignalName.Pressed);
            var window=scene.GetChildren().OfType<Window>().Single(w=>w.Name=="DeleteCards");
            if(Walk(window).OfType<Button>().Any(b=>b.Text is "上一页" or "下一页"))throw new Exception("Deletion must not paginate");
            if(!Walk(window).OfType<ScrollContainer>().Any(s=>s.Name=="DeletionScroll"))throw new Exception("Missing vertical deletion scroll");
            window.EmitSignal(Window.SignalName.CloseRequested);
            if(scene.LastState.GetProperty("revision").GetInt64()!=revision)throw new Exception("Closing deletion advanced match");
            Walk(scene).OfType<Button>().Single(b=>b.Text=="删牌").EmitSignal(Button.SignalName.Pressed);
            window=scene.GetChildren().OfType<Window>().Single(w=>w.Name=="DeleteCards" && !w.IsQueuedForDeletion());
            Walk(window).OfType<Button>().First(b=>b.Text=="删除此牌" && !b.Disabled).EmitSignal(Button.SignalName.Pressed);
            while(scene.IsBusy)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(scene.LastError is not null || scene.LastState.GetProperty("own").GetProperty("deck").GetArrayLength()!=count-1 || scene.LastState.GetProperty("decision").GetProperty("kind").GetString()!="draft")throw new Exception("Inline deletion failed");
            Walk(window).OfType<Button>().Single(b=>b.Text=="退出删牌").EmitSignal(Button.SignalName.Pressed);
            scene.QueueFree();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("V12_DELETION_OK close-no-command=OK vertical-scroll=OK return-to-market=OK");GetTree().Quit();
        }
        catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}
    }
}
