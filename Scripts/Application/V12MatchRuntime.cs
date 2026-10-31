using System.Text.Json;
using Magicians.Native;

namespace Magicians.Application;

// Same UI protocol; the authoritative match now lives in native C#.
public sealed class V12MatchRuntime : IDisposable
{
    private readonly string _root;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private NativeMatchSession? _session;
    private bool _disposed;
    public long Revision { get; private set; }

    public V12MatchRuntime(string root) => _root = root;
    public Task<JsonElement> Start(int seed, string[] styles) => Request(new { op = "start", seed, ai_styles = styles });
    public Task<JsonElement> Review(int round) => Request(new { op = "review", round });
    public Task<JsonElement> Submit(string kind, int? slot = null, int? index = null, string? uid = null, string[]? uids = null) =>
        Request(new { kind, revision = Revision, slot, index, uid, uids });

    private async Task<JsonElement> Request(object request)
    {
        await _requests.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var command = JsonSerializer.SerializeToElement(request);
            var response = await Task.Run(() =>
            {
                switch (NativeJson.String(command, "op"))
                {
                    case "start":
                        NativeJson.Require(_session is null, "A match is already running");
                        var styles = command.GetProperty("ai_styles").EnumerateArray().Select(x => x.GetString()!).ToArray();
                        _session = new NativeMatchSession(_root, command.GetProperty("seed").GetInt32(), styles);
                        return _session.Receive();
                    case "review":
                        NativeJson.Require(_session is not null, "Start a match first");
                        return _session!.Review(command.GetProperty("round").GetInt32());
                    default:
                        NativeJson.Require(_session is not null, "Start a match first");
                        return _session!.Send(command);
                }
            }).WaitAsync(TimeSpan.FromSeconds(120));
            if (response.TryGetProperty("status", out var status) && status.GetString() == "error")
                throw new InvalidDataException(response.GetProperty("error").GetString());
            Revision = response.GetProperty("revision").GetInt64();
            return response;
        }
        catch (TimeoutException)
        {
            Dispose();
            throw new TimeoutException("v1.2 赛事响应超时，请重新开始赛事。");
        }
        finally { _requests.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _session?.Dispose();
    }
}
