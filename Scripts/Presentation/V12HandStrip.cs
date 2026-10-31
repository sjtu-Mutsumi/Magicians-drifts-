using Godot;
namespace Magicians.Presentation;

// Translate the usual wheel gesture to horizontal hand navigation, including
// when the pointer is over a card's rule text rather than over empty space.
public partial class V12HandStrip : ScrollContainer
{
    public override void _Input(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: true } wheel) return;
        var direction = wheel.ButtonIndex switch { MouseButton.WheelDown or MouseButton.WheelRight => 1, MouseButton.WheelUp or MouseButton.WheelLeft => -1, _ => 0 };
        if (direction == 0) return;
        var hover = GetViewport().GuiGetHoveredControl();
        if (hover is null || (hover != this && !IsAncestorOf(hover))) return;
        ScrollHorizontal += direction * 140;
        GetViewport().SetInputAsHandled();
    }
}
