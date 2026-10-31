using Godot;

namespace Magicians.Application;

// Local presentation preferences, never part of a match snapshot or its deterministic hash.
public sealed record DisplayPreferences(int Width = 1920, int Height = 1080, bool Fullscreen = false)
{
    public const string SettingsPath = "user://display.cfg";
    public DisplayPreferences Validated() => Width is >= 960 and <= 7680 && Height is >= 540 and <= 4320
        ? this : new DisplayPreferences(Fullscreen: Fullscreen);
    public static DisplayPreferences Load(string path = SettingsPath)
    {
        var config = new ConfigFile();
        if (config.Load(path) != Error.Ok) return new();
        // Migrate old playtest defaults once. Explicit settings saved by this
        // version remain authoritative on subsequent launches.
        try {
            if ((int)config.GetValue("display", "version", 0) < 2) return new();
            return new DisplayPreferences((int)config.GetValue("display", "width", 1920),
            (int)config.GetValue("display", "height", 1080), (bool)config.GetValue("display", "fullscreen", false)).Validated(); }
        catch (Exception) { return new(); }
    }
    public Error Save(string path = SettingsPath)
    {
        var value = Validated(); var config = new ConfigFile();
        config.SetValue("display", "version", 2);
        config.SetValue("display", "width", value.Width); config.SetValue("display", "height", value.Height);
        config.SetValue("display", "fullscreen", value.Fullscreen); return config.Save(path);
    }
    public void Apply()
    {
        if (DisplayServer.GetName() == "headless") return;
        var value = Validated();
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.ResizeDisabled, false);
        DisplayServer.WindowSetMinSize(new Vector2I(960, 540));
        var screen = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var scale = Math.Min(1.0, Math.Min((double)usable.Size.X / value.Width, (double)Math.Max(540, usable.Size.Y - 40) / value.Height));
        var size = new Vector2I((int)(value.Width * scale), (int)(value.Height * scale));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
        if (value.Fullscreen) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
    }
}
