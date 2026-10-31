using System.Numerics;
using System.Text.Json;
using Magicians.Native;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var root = Path.GetFullPath(args[0]);
Catalog.Initialize(root);
NativeMatchSession? session = null;
try
{
    while (Console.ReadLine() is { } line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            JsonElement response;
            switch (NativeJson.String(request, "op"))
            {
                case "card_fixture": response = NativeFixtures.Card(request); break;
                case "battle_full": response = NativeFixtures.Battle(request); break;
                case "start":
                    session?.Dispose();
                    session = new NativeMatchSession(root,
                        request.TryGetProperty("seed", out var seed) ? BigInteger.Parse(seed.GetRawText()) : BigInteger.One,
                        request.TryGetProperty("ai_styles", out var styles) ? styles.EnumerateArray().Select(x => x.GetString()!).ToArray() : new[] { "fire", "water", "poison" });
                    response = session.Receive(); break;
                case "review":
                    response = (session ?? throw new ArgumentException("Start a match first")).Review(request.GetProperty("round").GetInt32()); break;
                case "battle":
                case "catalog":
                    response = NativeBridge.Respond(root, request); break;
                case "simulate":
                    var tactical = request.TryGetProperty("ai_styles", out var tacticalStyles)
                        ? tacticalStyles.EnumerateArray().Select(x => (JsonElement?)SixStyles.load_policy(x.GetString()!)).ToArray() : null;
                    response = NativeJson.Element(new Tournament().simulate(request.TryGetProperty("seed", out var tournamentSeed) ? BigInteger.Parse(tournamentSeed.GetRawText()) : BigInteger.One,
                        trace: true, policy_by_seat: tactical)); break;
                default:
                    response = (session ?? throw new ArgumentException("Start a match first")).Send(request); break;
            }
            Console.WriteLine(response.GetRawText());
        }
        catch (Exception error)
        {
            Console.WriteLine(NativeJson.Element(NativeJson.Obj(("status", "error"), ("error", error.ToString()))).GetRawText());
        }
    }
}
finally { session?.Dispose(); }
