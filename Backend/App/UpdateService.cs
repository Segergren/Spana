using Serilog;
using Spana.Backend.Core;
using Velopack;
using Velopack.Sources;

namespace Spana.Backend.App;

public sealed class UpdateState
{
    public string Current { get; set; } = "";
    public string Channel { get; set; } = "stable";
    // Set once an update is downloaded and ready to install.
    public string? Ready { get; set; }
}

// Updates come from GitHub Releases. Beta also takes prereleases; stable only final releases.
public static class UpdateService
{
    private const string Repo = "https://github.com/Segergren/Spana";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly UpdateState State = new();
    private static UpdateManager? _manager;
    private static UpdateInfo? _ready;
    private static int _checking;
    private static Timer? _timer;

    public static UpdateState GetState()
    {
        State.Channel = Channel;
        return State;
    }

    private static string Channel => SettingsService.Current.UpdateChannel == "beta" ? "beta" : "stable";

    public static void Start()
    {
        State.Current = typeof(UpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        _timer = new Timer(_ => _ = Check(), null, TimeSpan.FromSeconds(10), Interval);
    }

    public static async Task Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            bool beta = Channel == "beta";
            var manager = new UpdateManager(new GithubSource(Repo, null, beta), new UpdateOptions { AllowVersionDowngrade = true });
            if (!manager.IsInstalled) return;

            var info = await manager.CheckForUpdatesAsync();
            // Leaving the beta channel moves back to the newest stable release, even if it is older.
            bool leavingBeta = !beta && manager.CurrentVersion?.IsPrerelease == true;
            if (info == null || (info.IsDowngrade && !leavingBeta))
            {
                Clear();
                return;
            }

            string version = info.TargetFullRelease.Version.ToString();
            if (State.Ready == version) return;
            Log.Information("Downloading update {Version} ({Channel})", version, Channel);
            await manager.DownloadUpdatesAsync(info);
            _manager = manager;
            _ready = info;
            State.Ready = version;
            Broadcast();
        }
        catch (Exception ex)
        {
            Log.Warning("Update check failed: {Message}", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    // The channel changed: forget a download from the other channel and look again.
    public static void ChannelChanged()
    {
        Clear();
        _ = Check();
    }

    public static void Apply()
    {
        if (_manager == null || _ready == null) return;
        Log.Information("Installing update {Version}", State.Ready);
        _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    private static void Clear()
    {
        if (State.Ready == null) return;
        _manager = null;
        _ready = null;
        State.Ready = null;
        Broadcast();
    }

    private static void Broadcast() => _ = MessageHub.Send("update", GetState());
}
