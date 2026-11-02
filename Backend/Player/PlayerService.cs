using LibVLCSharp.Shared;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Cast;
using Spana.Backend.Core;
using Spana.Backend.Iptv;

namespace Spana.Backend.Player;

public sealed class PlayRequest
{
    public MediaKind Kind { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Ext { get; set; }
    public string? Url { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public string? Subtitle { get; set; }
    public long StartMs { get; set; }
}

public sealed class TrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class PlayerState
{
    public string Status { get; set; } = "idle";
    public PlayRequest? Item { get; set; }
    public long TimeMs { get; set; }
    public long LengthMs { get; set; }
    public bool Seekable { get; set; }
    public float Buffering { get; set; }
    public int Volume { get; set; }
    public bool Muted { get; set; }
    public float Rate { get; set; } = 1;
    public string? Error { get; set; }
    public List<TrackInfo> AudioTracks { get; set; } = new();
    public List<TrackInfo> SubtitleTracks { get; set; } = new();
    public int AudioTrack { get; set; } = -1;
    public int SubtitleTrack { get; set; } = -1;
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    // Live streams resume where they were paused; this is how far behind the broadcast that leaves us.
    public long BehindLiveMs { get; set; }
    public long? PausedAt { get; set; }
}

public static class PlayerService
{
    private static LibVLC? _vlc;
    private static MediaPlayer? _mp;
    private static readonly FrameSink Sink = new();
    private static readonly PlayerState State = new();
    private static readonly object Gate = new();
    private static int _retries;
    private static long _lastBroadcast;
    private static long _lastProgressSave;
    private static long _lastProgressPersist;
    private static string? _accountId;
    private static volatile bool _switching;

    // Strong references so the GC never collects delegates libvlc still calls.
    private static MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private static MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private static MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private static MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private static MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    public static FrameSink Frames => Sink;

    private const int WatchTickSeconds = 30;
    private static Timer? _watchTimer;

    // Every tick of live viewing is credited to whatever programme is on, which feeds suggestions.
    private static void TrackLiveWatch()
    {
        try
        {
            var item = State.Item;
            if (item?.Kind != MediaKind.Live || State.Status != "playing" || _accountId == null) return;
            var account = SettingsService.Current.Accounts.FirstOrDefault(a => a.Id == _accountId);
            if (account == null) return;
            var channel = CatalogService.Find(account, MediaKind.Live, item.Id);
            string? epgId = channel?.EpgId;
            var programme = GuideService.Get(account)?.Current(epgId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            MatchContext? ctx = null;
            string? sport = null;
            if (programme != null)
            {
                ctx = MatchDataService.Context(programme.Parsed, programme.Title, programme.Start);
                sport = SportService.Classify(programme.Parsed.Category, programme.NormalizedTitle);
            }
            LibraryService.AddLiveWatch(account.Id, item.Id, channel?.Name ?? item.Name, epgId, programme?.Title,
                WatchTickSeconds / 60.0, ctx, sport);
        }
        catch (Exception ex)
        {
            Log.Debug("Live watch tracking failed: {Message}", ex.Message);
        }
    }

    public static void Initialize()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _vlc = new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--quiet");
        _vlc.Log += (_, e) =>
        {
            if (e.Level >= LogLevel.Warning)
                Log.Debug("vlc {Module}: {Message}", e.Module, e.Message);
        };
        _mp = new MediaPlayer(_vlc) { EnableHardwareDecoding = SettingsService.Current.HardwareDecoding };
        State.Volume = SettingsService.Current.Volume;

        _watchTimer = new Timer(_ => TrackLiveWatch(), null, TimeSpan.FromSeconds(WatchTickSeconds), TimeSpan.FromSeconds(WatchTickSeconds));

        _formatCb = Sink.Format;
        _cleanupCb = Sink.Cleanup;
        _lockCb = Sink.Lock;
        _unlockCb = Sink.Unlock;
        _displayCb = Sink.Display;
        Sink.TrackInfoProvider = CurrentTrackInfo;
        _mp.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _mp.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        _mp.Opening += (_, _) => SetStatus("opening");
        _mp.Buffering += (_, e) =>
        {
            State.Buffering = e.Cache;
            if (e.Cache < 100 && State.Status != "paused") SetStatus("buffering", throttle: true);
            else if (e.Cache >= 100 && State.Status == "buffering") SetStatus("playing");
        };
        _mp.Playing += (_, _) =>
        {
            _retries = 0;
            if (State.Status != "paused") State.Status = "playing";
            Broadcast();
            Task.Run(ApplyVolume);
            Task.Run(() =>
            {
                Thread.Sleep(300);
                RefreshTracks();
                ChooseAudioTrack();
                Broadcast();
            });
        };
        _mp.Paused += (_, _) => SetStatus("paused");
        _mp.Stopped += (_, _) =>
        {
            if (!_switching && State.Status is not ("error" or "ended" or "opening")) SetStatus("idle");
        };
        _mp.EndReached += (_, _) => Task.Run(OnEnded);
        _mp.EncounteredError += (_, _) => Task.Run(() => OnError("Playback failed. The stream may be offline or your connection limit is reached."));
        _mp.TimeChanged += (_, e) =>
        {
            State.TimeMs = e.Time;
            SaveProgress(false);
            Broadcast(throttle: true);
        };
        _mp.LengthChanged += (_, e) => { State.LengthMs = e.Length; Broadcast(); };
        _mp.SeekableChanged += (_, e) => { State.Seekable = e.Seekable != 0; Broadcast(); };
        _mp.ESAdded += (_, _) => Task.Run(() => { RefreshTracks(); ChooseAudioTrack(); Broadcast(); });
    }

    private static (int, int, int, int) CurrentTrackInfo()
    {
        try
        {
            var track = _mp?.Media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            if (track is { } t)
            {
                State.VideoWidth = (int)t.Data.Video.Width;
                State.VideoHeight = (int)t.Data.Video.Height;
                return ((int)t.Data.Video.SarNum, (int)t.Data.Video.SarDen, State.VideoWidth, State.VideoHeight);
            }
        }
        catch { }
        return (1, 1, 0, 0);
    }

    public static PlayerState GetState() => State;

    public static void Play(PlayRequest req)
    {
        var account = SettingsService.ActiveAccount;
        if (_mp == null || _vlc == null || account == null) return;

        lock (Gate)
        {
            SaveProgress(true);
            _accountId = account.Id;
            var source = account.Kind == AccountKind.M3u ? CatalogService.Find(account, req.Kind, req.Id) : null;
            string url = req.Url ?? (account.Kind == AccountKind.Xtream
                ? XtreamClient.StreamUrl(account, req.Kind, req.Id, req.Ext)
                : source?.Url ?? "");
            if (string.IsNullOrEmpty(url))
            {
                OnError("This item has no playable address.");
                return;
            }

            var settings = SettingsService.Current;
            var media = new Media(_vlc, new Uri(url));
            // Playlists name the user agent and referrer some streams insist on.
            media.AddOption(":http-user-agent=" + (source?.UserAgent is { Length: > 0 } agent ? agent : settings.UserAgent));
            if (source?.Referrer is { Length: > 0 } referrer) media.AddOption(":http-referrer=" + referrer);
            media.AddOption(":http-reconnect");
            media.AddOption(":network-caching=" + settings.NetworkCachingMs);
            if (req.Kind == MediaKind.Live) media.AddOption(":live-caching=" + settings.NetworkCachingMs);
            if (req.StartMs > 0) media.AddOption(":start-time=" + (req.StartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            media.AddOption(":audio-desync=" + AudioDelayMs);
            if (!string.IsNullOrWhiteSpace(settings.PreferredAudioLanguage)) media.AddOption(":audio-language=" + settings.PreferredAudioLanguage);
            if (!string.IsNullOrWhiteSpace(settings.PreferredSubtitleLanguage)) media.AddOption(":sub-language=" + settings.PreferredSubtitleLanguage);

            State.Item = req;
            _playCount++;
            State.TimeMs = req.StartMs;
            State.LengthMs = 0;
            State.Error = null;
            State.AudioTracks.Clear();
            State.SubtitleTracks.Clear();
            State.Buffering = 0;
            State.BehindLiveMs = 0;
            State.PausedAt = null;
            State.VideoWidth = 0;
            State.VideoHeight = 0;
            State.Status = "opening";
            _retries = 0;

            _switching = true;
            if (_mp.Media != null) _mp.Stop();
            Sink.Clear();
            _switching = false;
            CastService.Prepare(media, req);
            _mp.EnableHardwareDecoding = settings.HardwareDecoding;
            _mp.Media = media;
            media.Dispose();
            _mp.Play();
            ApplyVolume();
            WatchSessions();

            LibraryService.RecordPlayback(account.Id, new LibraryEntry
            {
                Kind = req.Kind,
                Id = req.Id,
                Name = req.Name,
                Icon = req.Icon,
                Ext = req.Ext,
                SeriesId = req.SeriesId,
                SeriesName = req.SeriesName,
                Subtitle = req.Subtitle,
                PositionMs = req.StartMs
            });
            _ = MessageHub.Send("libraryChanged", new { });
        }
        Log.Information("Playing {Kind} {Name}", req.Kind, req.Name);
        Broadcast();
    }

    public static void TogglePause()
    {
        if (_mp == null) return;
        if (State.Status is "ended" or "error" && State.Item != null)
        {
            var again = State.Item;
            again.StartMs = 0;
            Play(again);
            return;
        }
        if (!_mp.CanPause) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool live = State.Item?.Kind == MediaKind.Live;
        if (State.Status != "paused")
        {
            State.Status = "paused";
            if (live) State.PausedAt = now;
            ApplyVolume();
            _mp.SetPause(true);
            CastService.OnPause();
        }
        else
        {
            // VLC's stream output doesn't survive a pause, so a cast resumes with a fresh stream.
            if (CastService.Active && State.Item is { } item)
            {
                State.PausedAt = null;
                item.StartMs = live ? 0 : State.TimeMs;
                Task.Run(() => Play(item));
                return;
            }
            _mp.SetPause(false);
            if (live && State.PausedAt is long since) State.BehindLiveMs += now - since;
            State.PausedAt = null;
            State.Status = "playing";
            ApplyVolume();
        }
        Broadcast();
    }

    // Reopens a paused-and-resumed live stream at the broadcast's current position.
    public static void GoLive()
    {
        if (State.Item is not { Kind: MediaKind.Live } item) return;
        item.StartMs = 0;
        Play(item);
    }

    public static void Stop()
    {
        if (_mp == null) return;
        SaveProgress(true);
        State.Status = "idle";
        State.Item = null;
        CastService.OnStop();
        Task.Run(() =>
        {
            _mp.Stop();
            Sink.Clear();
            Broadcast();
        });
        Broadcast();
    }

    // Opens the current item again where it is, after the cast target changed.
    public static void Restart()
    {
        var item = State.Item;
        if (item == null || State.Status is "idle" or "ended") return;
        item.StartMs = item.Kind == MediaKind.Live ? 0 : State.TimeMs;
        Task.Run(() => Play(item));
    }

    public static void Seek(long ms)
    {
        if (_mp == null || !_mp.IsSeekable) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, State.LengthMs - 1000));
        State.TimeMs = ms;
        // A TV plays a live-style restream, so a seek starts a new one from there.
        if (CastService.Active && State.Item is { } item)
        {
            item.StartMs = ms;
            Task.Run(() => Play(item));
            return;
        }
        _mp.Time = ms;
        Broadcast();
    }

    public static void SeekRelative(long deltaMs) => Seek(State.TimeMs + deltaMs);

    public static void SetVolume(int volume)
    {
        if (_mp == null) return;
        State.Volume = Math.Clamp(volume, 0, 150);
        State.Muted = false;
        ApplyVolume();
        SettingsService.Current.Volume = State.Volume;
        Broadcast();
        SaveVolumeSoon();
    }

    private static int _volumeSave;

    // Dragging the slider sends many changes; write the settings file once it settles.
    private static void SaveVolumeSoon()
    {
        int ticket = Interlocked.Increment(ref _volumeSave);
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            if (ticket == _volumeSave) SettingsService.Save();
        });
    }

    public static void ToggleMute()
    {
        if (_mp == null) return;
        State.Muted = !State.Muted;
        ApplyVolume();
        Broadcast();
    }

    // Frames reach the screen ~16 ms after VLC shows them (socket, next refresh, compositor); measured on screen.
    private const int VideoPathMs = 16;

    private static int AudioDelayMs => VideoPathMs + SettingsService.Current.AudioDelayMs;

    public static void ApplyAudioDelay()
    {
        if (_mp?.Media != null) _mp.SetAudioDelay(AudioDelayMs * 1000L);
    }

    private static volatile bool _sessionReady;
    private static int _sessionWatch;

    // Windows session level does the work; VLC stays at 100 and only goes above it for boost.
    private static void ApplyVolume()
    {
        if (_mp == null) return;
        int volume = State.Volume;
        bool silent = State.Muted || State.Status == "paused";
        float level = silent ? 0f : (float)Math.Pow(Math.Min(volume, 100) / 100.0, 3);
        if (AppAudio.Apply(level)) _sessionReady = true;
        int vlc = _sessionReady ? Math.Max(100, volume) : (State.Muted ? 0 : volume);
        if (_mp.Volume != vlc) _mp.Volume = vlc;
    }

    // A new stream or output device can open a fresh session at full level; set it as soon as it shows up.
    private static void WatchSessions()
    {
        int watch = Interlocked.Increment(ref _sessionWatch);
        Task.Run(async () =>
        {
            for (int i = 0; i < 80 && watch == _sessionWatch; i++)
            {
                ApplyVolume();
                await Task.Delay(i < 40 ? 25 : 250);
            }
        });
    }

    public static void SetRate(float rate)
    {
        if (_mp == null) return;
        State.Rate = Math.Clamp(rate, 0.25f, 4f);
        _mp.SetRate(State.Rate);
        Broadcast();
    }

    public static void SetAudioTrack(int id)
    {
        if (_mp == null) return;
        _mp.SetAudioTrack(id);
        State.AudioTrack = id;
        int position = State.AudioTracks.FindIndex(t => t.Id == id);
        if (State.Item is { } item && position >= 0)
        {
            SettingsService.Current.AudioChoices[AudioKey(item)] = position;
            SettingsService.Save();
        }
        Broadcast();
    }

    private static string AudioKey(PlayRequest item) =>
        item.Kind == MediaKind.Episode && item.SeriesId != null ? $"series:{item.SeriesId}" : $"{item.Kind}:{item.Id}".ToLowerInvariant();

    private static int _playCount;
    private static int _audioChosenFor = -1;

    // VLC picks audio by flags in the stream, which IPTV providers often set on a later track. Use the first track
    // unless the viewer picked another one for this channel before, or a preferred language is set (VLC handles that).
    private static void ChooseAudioTrack()
    {
        var item = State.Item;
        if (_mp == null || item == null || State.AudioTracks.Count < 2) return;
        if (!string.IsNullOrWhiteSpace(SettingsService.Current.PreferredAudioLanguage)) return;
        string key = AudioKey(item);
        if (_audioChosenFor == _playCount) return;
        _audioChosenFor = _playCount;

        int position = SettingsService.Current.AudioChoices.TryGetValue(key, out var saved) && saved < State.AudioTracks.Count ? saved : 0;
        int id = State.AudioTracks[position].Id;
        if (_mp.AudioTrack == id) return;
        _mp.SetAudioTrack(id);
        State.AudioTrack = id;
        Log.Information("Audio track {Position} of {Count} selected for {Item}", position + 1, State.AudioTracks.Count, item.Name);
    }

    public static void SetSubtitle(int id)
    {
        if (_mp == null) return;
        _mp.SetSpu(id);
        State.SubtitleTrack = id;
        Broadcast();
    }

    private static void RefreshTracks()
    {
        if (_mp == null) return;
        try
        {
            State.AudioTracks = _mp.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => new TrackInfo { Id = t.Id, Name = t.Name ?? $"Track {t.Id}" }).ToList();
            State.SubtitleTracks = _mp.SpuDescription.Where(t => t.Id >= 0).Select(t => new TrackInfo { Id = t.Id, Name = t.Name ?? $"Track {t.Id}" }).ToList();
            State.AudioTrack = _mp.AudioTrack;
            State.SubtitleTrack = _mp.Spu;
            State.Seekable = _mp.IsSeekable;
            State.LengthMs = _mp.Length > 0 ? _mp.Length : State.LengthMs;
        }
        catch (Exception ex)
        {
            Log.Debug("Track refresh failed: {Message}", ex.Message);
        }
    }

    private static void OnEnded()
    {
        var item = State.Item;
        if (item?.Kind == MediaKind.Live && _retries < 3)
        {
            Retry(item);
            return;
        }
        if (item != null && _accountId != null && item.Kind != MediaKind.Live)
            LibraryService.UpdateProgress(_accountId, item.Kind, item.Id, State.LengthMs, State.LengthMs, true);
        SetStatus("ended");
        _ = MessageHub.Send("libraryChanged", new { });
    }

    private static void OnError(string message)
    {
        var item = State.Item;
        if (item != null && _retries < 2)
        {
            Retry(item);
            return;
        }
        State.Error = message;
        SetStatus("error");
    }

    // Live streams often drop for a second; reconnect quietly before surfacing an error.
    private static void Retry(PlayRequest item)
    {
        _retries++;
        int attempt = _retries;
        Log.Warning("Stream interrupted, reconnect attempt {Attempt}", attempt);
        SetStatus("buffering");
        Thread.Sleep(1000 * attempt);
        if (!ReferenceEquals(State.Item, item)) return;
        var resume = item.Kind == MediaKind.Live ? 0 : State.TimeMs;
        item.StartMs = resume;
        Play(item);
        _retries = attempt;
    }

    private static void SaveProgress(bool force)
    {
        var item = State.Item;
        if (item == null || _accountId == null || item.Kind == MediaKind.Live) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastProgressSave < 5000) return;
        _lastProgressSave = now;
        bool persist = force || now - _lastProgressPersist > 30000;
        if (persist) _lastProgressPersist = now;
        if (State.TimeMs > 0)
            LibraryService.UpdateProgress(_accountId, item.Kind, item.Id, State.TimeMs, State.LengthMs, persist);
    }

    private static void SetStatus(string status, bool throttle = false)
    {
        State.Status = status;
        Broadcast(throttle);
    }

    public static void Broadcast(bool throttle = false)
    {
        long now = Environment.TickCount64;
        if (throttle && now - Interlocked.Read(ref _lastBroadcast) < 250) return;
        Interlocked.Exchange(ref _lastBroadcast, now);
        _ = MessageHub.Send("player", State);
    }

    public static void Shutdown()
    {
        try
        {
            SaveProgress(true);
            CastService.Shutdown();
            _mp?.Stop();
            _mp?.Dispose();
            _vlc?.Dispose();
            Sink.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Player shutdown failed");
        }
    }
}
