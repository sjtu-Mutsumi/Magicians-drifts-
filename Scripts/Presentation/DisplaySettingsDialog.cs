using Godot;
using Magicians.Application;

namespace Magicians.Presentation;

public partial class DisplaySettingsDialog : ConfirmationDialog
{
    private OptionButton _resolution = null!;
    private CheckBox _fullscreen = null!;
    private readonly List<Vector2I> _sizes = new() { new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160) };
    public override void _Ready()
    {
        Title = "显示设置"; GetOkButton().Text = "应用并保存"; GetCancelButton().Text = "取消";
        var column = new VBoxContainer(); AddChild(column);
        // AcceptDialog owns its content rectangle; FullRect anchors here create
        // a minimum-size feedback loop and stretch the dialog vertically.
        column.CustomMinimumSize = new Vector2(480, 160);
        column.Theme = new Theme { DefaultFontSize = 22 };
        column.AddChild(new Label { Text = "窗口分辨率（全屏时使用当前显示器分辨率）" });
        _resolution = new OptionButton { Alignment = HorizontalAlignment.Center }; column.AddChild(_resolution);
        foreach (var size in _sizes) _resolution.AddItem($"{size.X} × {size.Y}");
        _fullscreen = new CheckBox { Text = "全屏" }; column.AddChild(_fullscreen);
        column.AddChild(new Label { Text = "窗口自动适配屏幕；界面保持比例。" });
        var feedback = new Label(); column.AddChild(feedback);
        AboutToPopup += () =>
        {
            var prefs = DisplayPreferences.Load();
            var size = new Vector2I(prefs.Width, prefs.Height);
            if (!_sizes.Contains(size)) { _sizes.Add(size); _resolution.AddItem($"{size.X} × {size.Y}（自定义）"); }
            _resolution.Select(_sizes.IndexOf(size)); _fullscreen.ButtonPressed = prefs.Fullscreen;
        };
        Confirmed += () =>
        {
            var size = _sizes[_resolution.Selected];
            var prefs = new DisplayPreferences(size.X, size.Y, _fullscreen.ButtonPressed);
            prefs.Apply();
            var error = prefs.Save();
            if (error != Error.Ok) { GD.PushWarning("显示设置保存失败：" + error); feedback.Text = "保存失败：" + error; PopupCentered(); }
        };
    }
}
