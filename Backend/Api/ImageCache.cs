using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Spana.Backend.Core;
using Spana.Backend.Iptv;

namespace Spana.Backend.Api;

// Disk cache for logos and posters so browsing never hits the provider twice for the same art.
public static class ImageCache
{
    private static readonly string Dir = Path.Combine(Storage.CacheRoot, "images");
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Failed = new();
    private static readonly SemaphoreSlim Concurrency = new(12, 12);
    private static readonly SemaphoreSlim PrefetchGate = new(6, 6);
    private const long MaxBytes = 512L * 1024 * 1024;
    private static int _writesSinceTrim;

    public static async Task<(string? path, string contentType)> GetAsync(string url)
    {
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        string path = Path.Combine(Dir, hash[..2], hash);
        if (File.Exists(path))
        {
            RevalidateIfDue(url, path);
            return (path, Sniff(path));
        }
        LoadFailed();
        if (Failed.TryGetValue(hash, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(24)) return (null, "");

        var task = InFlight.GetOrAdd(hash, _ => new Lazy<Task<string?>>(() => DownloadAsync(url, path, hash)));
        try
        {
            var result = await task.Value;
            return (result, result != null ? Sniff(result) : "");
        }
        finally
        {
            InFlight.TryRemove(hash, out _);
        }
    }

    private static readonly int[] Widths = [128, 320, 480, 640, 960];
    private static readonly SemaphoreSlim ResizeGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Resizing = new();
    private static readonly ConcurrentDictionary<string, byte> NoResize = new();

    // A copy scaled down to the width it is shown at, so a 2000 px match still is not decoded for a 320 px card.
    public static async Task<(string? path, string contentType)> GetAsync(string url, int width)
    {
        var (path, type) = await GetAsync(url);
        // GDI+ reads JPEG and PNG reliably; WebP, SVG and animated GIFs are served as they are.
        if (path == null || width <= 0 || !OperatingSystem.IsWindows() || (type != "image/jpeg" && type != "image/png"))
            return (path, type);
        int w = Widths.FirstOrDefault(x => x >= width, Widths[^1]);
        string thumb = $"{path}.w{w}";
        if (NoResize.ContainsKey(thumb)) return (path, type);
        if (File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= File.GetLastWriteTimeUtc(path))
            return (thumb, Sniff(thumb));

        var task = Resizing.GetOrAdd(thumb, _ => new Lazy<Task<string?>>(() => ResizeAsync(path, thumb, w)));
        try
        {
            var result = await task.Value;
            if (result == null) NoResize[thumb] = 0;
            return result != null ? (result, Sniff(result)) : (path, type);
        }
        finally
        {
            Resizing.TryRemove(thumb, out _);
        }
    }

    private static async Task<string?> ResizeAsync(string source, string target, int width)
    {
        await ResizeGate.WaitAsync();
        try
        {
            return await Task.Run(() => OperatingSystem.IsWindows() ? TryResize(source, target, width) : null);
        }
        finally
        {
            ResizeGate.Release();
        }
    }

    // Looked up once: asking GDI+ for its encoders from several threads at once sometimes returns none.
    [SupportedOSPlatform("windows")]
    private static readonly Lazy<(ImageCodecInfo jpeg, ImageCodecInfo png)> Encoders = new(() =>
    {
        var all = ImageCodecInfo.GetImageEncoders();
        return (all.First(c => c.FormatID == ImageFormat.Jpeg.Guid), all.First(c => c.FormatID == ImageFormat.Png.Guid));
    });

    // Caught here, inside the task, so a file GDI+ cannot read does not surface as unhandled in the debugger.
    [SupportedOSPlatform("windows")]
    private static string? TryResize(string source, string target, int width)
    {
        try
        {
            return Resize(source, target, width);
        }
        catch (Exception ex)
        {
            Log.Debug("Image resize skipped {Path}: {Message}", source, ex.Message);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? Resize(string source, string target, int width)
    {
        using var src = Image.FromFile(source);
        // Small enough already; serving the original avoids a needless copy.
        if (src.Width <= width * 1.25) return null;
        int height = Math.Max(1, (int)Math.Round(src.Height * (double)width / src.Width));
        bool alpha = Image.IsAlphaPixelFormat(src.PixelFormat);
        using var bmp = new Bitmap(width, height, alpha
            ? PixelFormat.Format32bppArgb
            : PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        string tmp = target + ".tmp";
        if (alpha)
        {
            bmp.Save(tmp, Encoders.Value.png, null);
        }
        else
        {
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            bmp.Save(tmp, Encoders.Value.jpeg, p);
        }
        File.Move(tmp, target, true);
        return target;
    }

    private static async Task<string?> DownloadAsync(string url, string path, string hash)
    {
        await Concurrency.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
            using var res = await XtreamClient.Client.SendAsync(req, cts.Token);
            res.EnsureSuccessStatusCode();
            var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length < 16) throw new InvalidDataException("empty image");
            await WriteAsync(path, bytes, res);
            if (Interlocked.Increment(ref _writesSinceTrim) % 500 == 0) _ = Task.Run(Trim);
            return path;
        }
        catch (Exception ex)
        {
            Failed[hash] = DateTimeOffset.UtcNow;
            SaveFailedSoon();
            Log.Debug("Image fetch failed {Url}: {Message}", url, ex.Message);
            return null;
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private sealed class Validators
    {
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    private static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, byte> Revalidating = new();

    private static async Task WriteAsync(string path, byte[] bytes, HttpResponseMessage res)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, path, true);
        SaveValidators(path, new Validators
        {
            ETag = res.Headers.ETag?.ToString(),
            LastModified = res.Content.Headers.LastModified?.ToString("R"),
            CheckedAt = DateTimeOffset.UtcNow
        });
    }

    private static Validators? LoadValidators(string path)
    {
        try
        {
            string meta = path + ".meta";
            return File.Exists(meta) ? System.Text.Json.JsonSerializer.Deserialize<Validators>(File.ReadAllText(meta)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveValidators(string path, Validators v)
    {
        try { File.WriteAllText(path + ".meta", System.Text.Json.JsonSerializer.Serialize(v)); }
        catch { }
    }

    // A cached image is always served right away; once it is a week old the source is asked whether it changed.
    private static void RevalidateIfDue(string url, string path)
    {
        var v = LoadValidators(path);
        var checkedAt = v?.CheckedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        if (DateTimeOffset.UtcNow - checkedAt < RecheckAfter || !Revalidating.TryAdd(path, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
                if (v?.ETag != null) req.Headers.TryAddWithoutValidation("If-None-Match", v.ETag);
                if (v?.LastModified != null) req.Headers.TryAddWithoutValidation("If-Modified-Since", v.LastModified);
                using var res = await XtreamClient.Client.SendAsync(req, cts.Token);
                if (res.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    SaveValidators(path, new Validators { ETag = v?.ETag, LastModified = v?.LastModified, CheckedAt = DateTimeOffset.UtcNow });
                    return;
                }
                if (!res.IsSuccessStatusCode) return;
                var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
                if (bytes.Length < 16) return;
                var existing = await File.ReadAllBytesAsync(path);
                bool changed = !bytes.AsSpan().SequenceEqual(existing);
                if (changed)
                {
                    await WriteAsync(path, bytes, res);
                    Log.Debug("Image updated at source {Url}", url);
                }
                else
                {
                    SaveValidators(path, new Validators
                    {
                        ETag = res.Headers.ETag?.ToString(),
                        LastModified = res.Content.Headers.LastModified?.ToString("R"),
                        CheckedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                // Offline or dead source: keep serving the copy we have.
                Log.Debug("Image revalidation skipped {Url}: {Message}", url, ex.Message);
            }
            finally
            {
                Revalidating.TryRemove(path, out _);
            }
        });
    }

    // Downloads images in the background so pages find them on disk; never competes much with on-screen requests.
    public static void Prefetch(IEnumerable<string?> urls)
    {
        var todo = urls.Where(u => u != null && (u.StartsWith("http://") || u.StartsWith("https://")))
            .Distinct().Cast<string>().ToList();
        if (todo.Count == 0) return;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int fetched = 0;
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (url, _) =>
            {
                string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                LoadFailed();
                if (File.Exists(Path.Combine(Dir, hash[..2], hash)) || Failed.ContainsKey(hash)) return;
                await PrefetchGate.WaitAsync();
                try
                {
                    var (path, _) = await GetAsync(url);
                    if (path != null) Interlocked.Increment(ref fetched);
                }
                finally
                {
                    PrefetchGate.Release();
                }
            });
            if (fetched > 0) Log.Information("Prefetched {Count} images in {Ms} ms", fetched, sw.ElapsedMilliseconds);
        });
    }

    private static string Sniff(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var fs = File.OpenRead(path);
        int n = fs.Read(head);
        if (n >= 4 && head[0] == 0x89 && head[1] == 0x50) return "image/png";
        if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8) return "image/jpeg";
        if (n >= 12 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return "image/webp";
        if (n >= 3 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F') return "image/gif";
        if (n >= 1 && (head[0] == '<' || head[0] == 0xEF)) return "image/svg+xml";
        return "application/octet-stream";
    }

    public static void Trim()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.Extension != ".meta")
                .OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxBytes)
                {
                    f.Delete();
                    try { File.Delete(f.FullName + ".meta"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Image cache trim failed");
        }
    }

    public static long SizeBytes() =>
        Directory.Exists(Dir) ? new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public static void Clear()
    {
        try { Directory.Delete(Dir, true); } catch { }
        Failed.Clear();
        try { File.Delete(FailedFile); } catch { }
    }

    // Dead links are remembered across restarts so they never hold up a page twice.
    private static readonly string FailedFile = Path.Combine(Storage.CacheRoot, "image-failures.json");
    private static int _failedLoaded;
    private static int _saveScheduled;

    private static void LoadFailed()
    {
        if (Interlocked.Exchange(ref _failedLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(FailedFile)) return;
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(FailedFile));
            if (map == null) return;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var (k, v) in map) if (v > cutoff) Failed[k] = v;
        }
        catch { }
    }

    private static void SaveFailedSoon()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            Interlocked.Exchange(ref _saveScheduled, 0);
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                Storage.Save(FailedFile, Failed.Where(f => f.Value > cutoff).ToDictionary(k => k.Key, v => v.Value));
            }
            catch { }
        });
    }
}
