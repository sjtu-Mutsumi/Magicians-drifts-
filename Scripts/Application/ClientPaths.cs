using Godot;

namespace Magicians.Application;

public static class ClientPaths
{
    public static string DataRoot => OS.HasFeature("editor")
        ? ProjectSettings.GlobalizePath("res://")
        : Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "runtime-data");
}
