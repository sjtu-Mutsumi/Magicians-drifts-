using Godot;
using Magicians.Presentation;
using System.Text.Json;

namespace Magicians.Tests;

public partial class V12LifecycleRunner : Node
{
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren()) { yield return child; foreach (var node in Descendants(child)) yield return node; }
    }
    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://runtime/tests"));
            var prefsPath = ProjectSettings.GlobalizePath("res://runtime/tests/display-migration-test.cfg");
            var oldPrefs = new ConfigFile(); oldPrefs.SetValue("display", "width", 1280); oldPrefs.SetValue("display", "height", 720); oldPrefs.Save(prefsPath);
            if (Magicians.Application.DisplayPreferences.Load(prefsPath).Width != 1920) throw new Exception("Legacy resolution was not migrated to 1080p default");
            new Magicians.Application.DisplayPreferences(1600,900).Save(prefsPath);
            if (Magicians.Application.DisplayPreferences.Load(prefsPath).Width != 1600) throw new Exception("Explicit resolution choice was not preserved");
            var scene = GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>(); scene.AutoStart = false; AddChild(scene);
            await scene.StartMatchAsync(334011, new[] { "fire", "water", "earth" });
            if (scene.LastError is not null) throw new Exception(scene.LastError);
            Descendants(scene).OfType<Button>().Single(b => b.Text == "重新开始").EmitSignal(Button.SignalName.Pressed);
            while (scene.IsBusy) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (scene.LastError is not null || scene.LastState.GetProperty("round").GetInt32() != 1 || scene.LastState.GetProperty("seed").GetInt32() == 334011) throw new Exception("Restart failed to launch a fresh random match");
            await scene.StartMatchAsync(334012, new[] { "wind", "poison", "flexible" });
            if (scene.LastState.GetProperty("seed").GetInt32() != 334012 || scene.LastState.GetProperty("round").GetInt32() != 1)
                throw new Exception("Restart failed to replace authority");
            await scene.SubmitAsync("delegate_match");
            if (scene.LastError is not null || scene.LastState.GetProperty("status").GetString() != "finished")
                throw new Exception(scene.LastError ?? "No champion");
            Descendants(scene).OfType<Button>().Single(b => b.Text == "查看本轮复盘").EmitSignal(Button.SignalName.Pressed);
            while (scene.IsBusy) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!scene.GetChildren().OfType<Window>().Any(w => w.Name == "RoundReview") || Descendants(scene).OfType<OptionButton>().Any())
                throw new Exception("Review UI failed");
            scene.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("V12_LIFECYCLE_OK restart=OK new-authority=OK public-review=OK exit=OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
