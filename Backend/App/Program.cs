using System.Diagnostics;
using System.Drawing;
using Photino.NET;
using Serilog;
using Spana.Backend.Api;
using Spana.Backend.Cast;
using Spana.Backend.Core;
using Spana.Backend.Iptv;
using Spana.Backend.Player;
using Velopack;

namespace Spana.Backend.App;

public static class Program
{
    private static PhotinoWindow? _window;
    private static bool _fullscreen;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(Storage.Root);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(Storage.Root, "logs", "spana.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 5,
                fileSizeLimitBytes: 10 * 1024 * 1024)
            .CreateLogger();

        try
        {
            Log.Information("Spana starting");
            SettingsService.Load();
            PlayerService.Initialize();
            string baseUrl = ApiServer.Start();
            CastService.Initialize();
            UpdateService.Start();

            if (SettingsService.ActiveAccount is { } account)
            {
                CatalogService.Warm(account);
                GuideService.Warm(account);
                MatchDataService.Start();
                FeedCache.Invalidate();
                _ = Routes.RefreshAccountInfo(account);
            }

            // Headless runs serve the API only, for tests against many playlists.
            if (Environment.GetEnvironmentVariable("SPANA_HEADLESS") == "1")
            {
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            string? devUrl = Environment.GetEnvironmentVariable("SPANA_DEV_URL");
            string page = $"{devUrl ?? baseUrl}/index.html?port={ApiServer.Port}&t={ApiServer.Token}&ik={ApiServer.ImageKey}";

            CreateWindow(page);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Spana crashed");
        }
        finally
        {
            PlayerService.Shutdown();
            Log.CloseAndFlush();
        }
    }

    private static void CreateWindow(string url)
    {
        var app = new PhotinoApplication
        {
            NotificationsEnabled = false,
            ShutdownMode = PhotinoShutdownMode.OnExplicitShutdown
        };
        app.Startup += (_, _) => BuildWindow(url, app);
        app.Run(null);
    }

    private static void BuildWindow(string url, PhotinoApplication app)
    {
        var saved = SettingsService.Current.Window;
        var w = new PhotinoWindow();
        if (OperatingSystem.IsWindows())
            w = w.SetBrowserControlInitParameters("--autoplay-policy=no-user-gesture-required --no-proxy-server");
        w = w.SetUserDataFolder(Path.Combine(Storage.Root, "webview"))
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(saved?.Width > 400 ? saved.Width : 1440, saved?.Height > 300 ? saved.Height : 900))
            .SetMinSize(960, 600)
            .SetResizable(true)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(Debugger.IsAttached || Environment.GetEnvironmentVariable("SPANA_DEVTOOLS") == "1")
            .SetZoomEnabled(false);

        string icon = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "icon.ico" : "icon.png");
        if (File.Exists(icon)) w = w.SetIconFile(icon);
        bool restore = saved != null && saved.Width > 400 && IsOnScreen(saved);
        w = restore
            ? w.SetUseOsDefaultLocation(false).SetLocation(new Point(saved!.X, saved.Y))
            : w.Center();
        if (restore && saved!.Maximized) w = w.SetMaximized(true);
        _normal = restore ? new Rectangle(saved!.X, saved.Y, saved.Width, saved.Height) : null;
        _maximized = restore && saved!.Maximized;

        _window = w.Load(url);
        _window.SetTitle("Spana");
        _window.RegisterLocationChangedHandler((_, _) => WindowChanged());
        _window.RegisterSizeChangedHandler((_, _) => WindowChanged());
        _window.RegisterMaximizedHandler((_, _) => WindowChanged());
        _window.RegisterRestoredHandler((_, _) => WindowChanged());
        _window.RegisterClosingHandler((_, _) =>
        {
            SaveWindowState();
            app.Shutdown(0, false);
        });
        _window.Show();
    }

    // Size and position of the window when neither maximized nor minimized; a maximized window restores to it.
    private static Rectangle? _normal;
    private static bool _maximized;
    private static int _saveTicket;

    private static void WindowChanged()
    {
        if (_window == null || _fullscreen) return;
        if (!Capture()) return;
        int ticket = Interlocked.Increment(ref _saveTicket);
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            if (ticket == _saveTicket) Persist();
        });
    }

    // Reads the window on its own thread; false while minimized, whose position is off screen.
    private static bool Capture()
    {
        try
        {
            var state = _window!.WindowState;
            if (state == PhotinoWindowState.Minimized) return false;
            _maximized = state == PhotinoWindowState.Maximized;
            if (state == PhotinoWindowState.Normal) _normal = new Rectangle(_window.Location, _window.Size);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SaveWindowState()
    {
        if (_window == null || _fullscreen) return;
        Capture();
        Persist();
    }

    private static void Persist()
    {
        try
        {
            if (_normal is not { } n) return;
            SettingsService.Current.Window = new WindowState { X = n.X, Y = n.Y, Width = n.Width, Height = n.Height, Maximized = _maximized };
            SettingsService.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save window state");
        }
    }

    // A monitor that was unplugged would otherwise leave the window off screen.
    private static bool IsOnScreen(WindowState saved)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var title = new NativeRect { Left = saved.X + 40, Top = saved.Y, Right = saved.X + saved.Width - 40, Bottom = saved.Y + 40 };
        return MonitorFromRect(ref title, 0) != IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    public static void SetFullscreen(bool enabled)
    {
        if (_window == null || enabled == _fullscreen) return;
        _fullscreen = enabled;
        _window.Invoke(() => _window.SetFullScreen(enabled));
        _ = MessageHub.Send("fullscreen", new { enabled });
    }
}
