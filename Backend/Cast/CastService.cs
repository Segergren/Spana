using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LibVLCSharp.Shared;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Player;

namespace Spana.Backend.Cast;

public sealed class CastDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Model { get; set; }
}

public sealed class CastState
{
    public List<CastDevice> Devices { get; set; } = new();
    public CastDevice? Active { get; set; }
    // idle, ready, connecting, casting
    public string Status { get; set; } = "idle";
    public string? Error { get; set; }
    public bool Scanning { get; set; }
}

// Plays on DLNA TVs: VLC restreams MPEG-TS on loopback, the stream server relays it, and the TV is told to play it.
public static class CastService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DlnaRenderer> Renderers = new();
    private static readonly Dictionary<string, long> LastSeen = new();
    private static long _lastScan = -60000;
    private static bool _scanning;

    private static DlnaRenderer? _target;
    private static string _status = "idle";
    private static string? _error;
    private static int _generation;
    private static volatile bool _paused;

    public static bool Active
    {
        get { lock (Gate) return _target != null; }
    }

    public static CastState GetState()
    {
        lock (Gate)
        {
            var devices = Renderers.Values.Select(DeviceOf).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var active = _target != null ? DeviceOf(_target) : null;
            if (active != null && devices.All(d => d.Id != active.Id)) devices.Insert(0, active);
            return new CastState { Devices = devices, Active = active, Status = _status, Error = _error, Scanning = _scanning };
        }
    }

    private static CastDevice DeviceOf(DlnaRenderer r) => new() { Id = r.Id, Name = r.Name, Model = r.Model };

    // Searches once at startup so the menu has TVs to show right away.
    public static void Initialize() => Scan();

    // Looks for TVs again if the last look is old.
    public static void Scan()
    {
        lock (Gate)
        {
            if (_scanning || Environment.TickCount64 - _lastScan < 8000) return;
            _scanning = true;
        }
        Broadcast();
        _ = Task.Run(async () =>
        {
            try
            {
                var found = await Dlna.Discover(TimeSpan.FromSeconds(2.5));
                lock (Gate)
                {
                    long now = Environment.TickCount64;
                    foreach (var r in found)
                    {
                        Renderers[r.Id] = r;
                        LastSeen[r.Id] = now;
                    }
                    // TVs miss a search now and then, so they stay listed until gone for a while.
                    foreach (var id in LastSeen.Where(e => now - e.Value > 600_000).Select(e => e.Key).ToList())
                    {
                        Renderers.Remove(id);
                        LastSeen.Remove(id);
                    }
                }
                Log.Information("Found {Count} DLNA renderers with video", found.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("DLNA discovery failed: {Message}", ex.Message);
            }
            finally
            {
                lock (Gate)
                {
                    _scanning = false;
                    _lastScan = Environment.TickCount64;
                }
                Broadcast();
            }
        });
    }

    // Picks where playback goes; null means this computer. Whatever is playing moves over at its current position.
    public static void Select(string? id)
    {
        lock (Gate)
        {
            if (_target?.Id == id) return;
            DlnaRenderer? target = null;
            if (id != null && !Renderers.TryGetValue(id, out target)) throw new CastException("That device is no longer available.");
            EndSession(true);
            _target = target;
            _status = target == null ? "idle" : "ready";
            _error = null;
            Log.Information("Casting to {Device}", target?.Name ?? "this computer");
        }
        Broadcast();
        PlayerService.Restart();
    }

    public static void DismissError()
    {
        lock (Gate) _error = null;
        Broadcast();
    }

    // Called by the player for every new stream, after the previous one stopped.
    internal static void Prepare(Media media, PlayRequest req)
    {
        lock (Gate)
        {
            StreamServer.Clear();
            if (_target is not { } target) return;

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            int port = FreeLoopbackPort();
            // Audio becomes AAC, which every TV plays; video passes through untouched. An HTTP output can't take
            // data faster than it is sent, which holds files to real time.
            media.AddOption($":sout=#transcode{{acodec=mp4a,ab=192,channels=2,samplerate=48000}}:std{{access=http,mux=ts,dst=127.0.0.1:{port}/stream.ts}}");
            media.AddOption(":no-sout-all");
            media.AddOption(":no-sout-spu");
            StreamServer.Serve(token, $"http://127.0.0.1:{port}/stream.ts");
            string url = $"http://{LocalAddressFor(target.Address)}:{StreamServer.Port}/dlna/{token}/stream.ts";
            string title = req.SeriesName is { } series ? $"{series} {req.Subtitle}".Trim() : req.Name;
            _status = "connecting";
            _paused = false;
            int generation = ++_generation;
            _ = Task.Run(() => Start(generation, target, url, port, title));
        }
        Broadcast();
    }

    // Most TVs can't pause a live DLNA stream (LG offers only Stop), so pausing stops the TV and resuming starts
    // a fresh stream.
    internal static void OnPause()
    {
        DlnaRenderer? target;
        lock (Gate)
        {
            target = _target;
            if (target == null) return;
            _paused = true;
        }
        _ = Task.Run(async () =>
        {
            try { await Dlna.Stop(target, CancellationToken.None); }
            catch (Exception ex) { Log.Debug("DLNA stop on pause failed: {Message}", ex.Message); }
        });
    }

    internal static void OnStop()
    {
        lock (Gate)
        {
            if (_target == null) return;
            EndSession(true);
            _status = "ready";
        }
        Broadcast();
    }

    private static async Task Start(int generation, DlnaRenderer target, string url, int port, string title)
    {
        try
        {
            long deadline = Environment.TickCount64 + 20000;
            while (!await Listening(port))
            {
                if (generation != _generation) return;
                if (Environment.TickCount64 > deadline) throw new CastException("The stream did not start in time.");
                await Task.Delay(300);
            }
            if (generation != _generation) return;
            // TVs often answer only once fully started, well after the picture is up. Pulling the stream is
            // the earlier sign that it plays.
            var play = Dlna.Play(target, url, title, CancellationToken.None);
            while (!play.IsCompleted && StreamServer.ActiveRelays == 0) await Task.WhenAny(play, Task.Delay(200));
            if (play.IsCompleted) await play;
            else _ = play.ContinueWith(t => Log.Debug("DLNA play reply from {Device}: {Error}", target.Name, t.Exception?.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);
            lock (Gate)
            {
                if (generation != _generation) return;
                _status = "casting";
            }
            Log.Information("DLNA playing on {Device}", target.Name);
            Broadcast();
            _ = Task.Run(() => Watch(generation, target));
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Warning("DLNA control of {Device} failed: {Error}", target.Name, ex.Message);
                ex = new CastException($"{target.Name} did not respond.");
            }
            Fail(generation, ex);
        }
    }

    private static async Task<bool> Listening(int port)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // Follows the TV so a stop there, or a stream it never fetches, ends the session here too.
    private static async Task Watch(int generation, DlnaRenderer target)
    {
        bool seen = false;
        int idle = 0, errors = 0;
        long started = Environment.TickCount64;
        while (generation == _generation)
        {
            await Task.Delay(3000);
            if (generation != _generation || _paused) continue;
            string state;
            try
            {
                state = await Dlna.State(target, CancellationToken.None);
                errors = 0;
            }
            catch (Exception)
            {
                if (++errors < 3) continue;
                Fail(generation, new CastException($"Lost the connection to {target.Name}."));
                return;
            }
            if (generation != _generation || _paused) continue;
            // LG stays in TRANSITIONING for the whole of an endless stream, so the TV pulling from us counts too.
            bool stopped = state is "STOPPED" or "NO_MEDIA_PRESENT";
            if (!stopped && (state is "PLAYING" or "PAUSED_PLAYBACK" || StreamServer.ActiveRelays > 0))
            {
                seen = true;
                idle = 0;
            }
            else if (stopped && seen && ++idle >= 2)
            {
                Log.Information("Playback stopped on {Device}", target.Name);
                lock (Gate)
                {
                    if (generation != _generation) return;
                    EndSession(false);
                    _target = null;
                    _status = "idle";
                }
                Broadcast();
                PlayerService.Stop();
                return;
            }
            if (!seen && Environment.TickCount64 - started > 30000)
            {
                Fail(generation, new CastException(Interlocked.Read(ref StreamServer.LastRequestTicks) != 0
                    ? $"{target.Name} could not play this stream."
                    : $"{target.Name} could not reach this PC. Allow Spana through Windows Firewall on private networks."));
                return;
            }
        }
    }

    private static void Fail(int generation, Exception ex)
    {
        lock (Gate)
        {
            if (generation != _generation) return;
            Log.Warning("Cast failed: {Message}", ex.Message);
            EndSession(false);
            _target = null;
            _status = "idle";
            _error = ex is CastException ? ex.Message : $"Casting failed: {ex.Message}";
        }
        Broadcast();
        PlayerService.Restart();
    }

    // Must hold Gate.
    private static void EndSession(bool sendStop)
    {
        _generation++;
        StreamServer.Clear();
        if (sendStop && _target is { } target)
        {
            _ = Task.Run(async () =>
            {
                try { await Dlna.Stop(target, CancellationToken.None); }
                catch (Exception) { }
            });
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // The address of this PC on the route to the TV, which is where it fetches the stream from.
    private static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(remote, 1900);
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }

    public static void Broadcast() => _ = MessageHub.Send("cast", GetState());

    public static void Shutdown()
    {
        DlnaRenderer? target;
        lock (Gate)
        {
            target = _target;
            StreamServer.Clear();
        }
        try { if (target != null) Dlna.Stop(target, CancellationToken.None).Wait(1500); } catch { }
    }
}
