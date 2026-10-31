using Godot;
using Magicians.Application;
using System.Text.Json;
namespace Magicians.Tests;
public partial class V12PackagedRunner : Node
{
    public override async void _Ready()
    {
        try
        {
            using var runtime = new V12MatchRuntime(ClientPaths.DataRoot);
            var state = await runtime.Start(334030, new[] { "wind", "poison", "flexible" });
            if (state.GetProperty("version").GetString() != "1.2" || state.GetProperty("own").GetProperty("deck").GetArrayLength() != 8) throw new Exception("Wrong packaged rules");
            state = await runtime.Submit("delegate_match");
            if (state.GetProperty("status").GetString() != "finished" || state.GetProperty("placements").GetArrayLength() != 4) throw new Exception("Packaged match failed");
            var review = await runtime.Review(1);
            if (review.GetProperty("status").GetString() != "review") throw new Exception("Packaged review failed");
            GD.Print("V12_PACKAGED_OK version=1.2 ranks=4 native-csharp=OK review=OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
