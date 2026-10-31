using System.Text.Json;
using Magicians.Native;

namespace Magicians.Application;

public static class V12CombatRuntime
{
    public static Task<JsonElement> Request(string root, object request) =>
        Task.Run(() => NativeBridge.Respond(root, JsonSerializer.SerializeToElement(request)));
}
