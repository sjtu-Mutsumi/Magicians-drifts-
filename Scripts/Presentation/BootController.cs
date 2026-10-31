using Godot;
using Magicians.Application;

namespace Magicians.Presentation;

public partial class BootController : Node
{
    private Control? _menu;
    public override void _Ready()
    {
        try
        {
            if (!OS.HasFeature("editor"))
                ReleaseIntegrity.Verify(Path.GetDirectoryName(OS.GetExecutablePath())!);
            DisplayPreferences.Load().Apply();
            ShowMainMenu();
            if (OS.GetCmdlineUserArgs().Contains("--release-smoke"))
            {
                Callable.From(() => GetTree().ChangeSceneToFile("res://Scenes/Tests/V12PackagedRunner.tscn")).CallDeferred();
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr($"BOOT_FAILED {exception}");
            if (!DisplayServer.GetName().Equals("headless", StringComparison.Ordinal))
                OS.Alert(exception.Message, "Magicians! 文件校验失败");
            GetTree().Quit(1);
        }
    }

    private void ShowMainMenu()
    {
        _menu?.QueueFree();
        var scene = GD.Load<PackedScene>("res://Scenes/MainMenu/MainMenu.tscn");
        _menu = scene.Instantiate<Control>();
        AddChild(_menu);
        if (_menu is MainMenuController menu)
            menu.StartRequested += () => GetTree().ChangeSceneToFile("res://Scenes/V12/SinglePlayer.tscn");
    }

    public void ExitGame() => GetTree().Quit();
}
