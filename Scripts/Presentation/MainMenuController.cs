using Godot;

namespace Magicians.Presentation;

public partial class MainMenuController : Control
{
    public event Action? StartRequested;
    private Control _center = null!;
    public override void _Ready()
    {
        var background = new ColorRect { Color = new Color("0e1420") };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); AddChild(background); MoveChild(background, 0);
        var center = new VBoxContainer { Name = "Center", Alignment = BoxContainer.AlignmentMode.Center,
            CustomMinimumSize = new Vector2(620, 360) };
        _center = center;
        center.AddChild(new Label { Text = "Magicians!", HorizontalAlignment = HorizontalAlignment.Center,
            Theme = new Theme { DefaultFontSize = 56 } });
        center.AddChild(new Label { Text = "魔法集会 · 魔法师大赛", HorizontalAlignment = HorizontalAlignment.Center });
        center.AddChild(new Label { Text = "四名魔法师在共享牌池中构筑咒语书，完成一轮轮同步战斗。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center });
        var start = new Button { Name = "Start", Text = "开始单人赛事", CustomMinimumSize = new Vector2(0, 64) };
        var exit = new Button { Name = "Exit", Text = "退出游戏", CustomMinimumSize = new Vector2(0, 48) };
        var settings = new Button { Text = "显示设置", CustomMinimumSize = new Vector2(0, 48) };
        settings.Pressed += () => { var dialog = new DisplaySettingsDialog(); AddChild(dialog); dialog.PopupCentered(new Vector2I(560, 300)); };
        center.AddChild(start); center.AddChild(settings); center.AddChild(exit); AddChild(center);
        start.Pressed += () =>
        {
            StartRequested?.Invoke();
            if (StartRequested is null) GetTree().ChangeSceneToFile("res://Scenes/V12/SinglePlayer.tscn");
        };
        exit.Pressed += () => GetTree().Quit();
        Resized += CenterMenu;
        CenterMenu();
    }

    private void CenterMenu()
    {
        if (_center is null || !IsInstanceValid(_center)) return;
        var viewport = GetViewportRect().Size;
        var size = _center.GetCombinedMinimumSize();
        _center.Size = size;
        _center.Position = (viewport - size) * 0.5f;
    }
}
