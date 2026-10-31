using Godot;
using Magicians.Application;
using Magicians.Presentation;

namespace Magicians.Tests;

public partial class MainMenuFlowRunner : Node
{
    public override async void _Ready()
    {
        try
        {
            // Keep the test host alive while the real menu changes current scene.
            GetTree().CurrentScene = null;
            var boot = GD.Load<PackedScene>("res://Scenes/Boot/Boot.tscn").Instantiate<BootController>();
            AddChild(boot);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var menu = boot.GetNode<MainMenuController>("MainMenu");
            Require(menu.GetNode<Button>("Center/Start").Text == "开始单人赛事", "Main menu start control missing");
            Require(boot.GetChildCount() == 1, "Boot started a match before the start button.");
            menu.GetNode<Button>("Center/Start").EmitSignal(Button.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(GetTree().CurrentScene is V12MatchController,
                "Default start must enter v1.2 without creating a legacy v1.1 session.");
            var match = (V12MatchController)GetTree().CurrentScene;
            while (match.IsBusy) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (match.LastState.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new Exception("Start did not automatically launch a match");
            Require(match.LastError is null && match.LastState.GetProperty("version").GetString() == "1.2", "v1.2 failed to start");
            await match.SubmitAsync("delegate_match");
            Require(match.LastError is null && match.LastState.GetProperty("status").GetString() == "finished", "AI spectator flow did not produce a champion");
            boot.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("MAIN_MENU_FLOW_OK start-gated=OK v12-default=OK spectator-to-champion=OK");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PrintErr("MAIN_MENU_FLOW_FAILED " + error); GetTree().Quit(1); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
