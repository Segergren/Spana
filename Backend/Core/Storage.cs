using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Spana.Backend.Core;

public static class Storage
{
    // SPANA_DATA points test instances at their own folder.
    public static readonly string Root = Environment.GetEnvironmentVariable("SPANA_DATA") is { Length: > 0 } dir ? dir : DefaultRoot();

    // The app used to be called Setv; its folder moves over on first start so settings and cache are kept.
    private static string DefaultRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(appData, "Spana");
        string old = Path.Combine(appData, "Setv");
        if (Directory.Exists(root) || !Directory.Exists(old)) return root;
        try
        {
            Directory.Move(old, root);
            return root;
        }
        catch (IOException)
        {
            return old;
        }
        catch (UnauthorizedAccessException)
        {
            return old;
        }
    }
    public static string CacheRoot => Path.Combine(Root, "cache");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly object WriteLock = new();

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read {Path}", path);
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, true);
        }
    }
}

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(Storage.Root, "settings.json");
    public static Settings Current { get; private set; } = new();

    public static void Load() => Current = Storage.Load<Settings>(FilePath);

    public static void Save() => Storage.Save(FilePath, Current);

    public static Account? ActiveAccount =>
        Current.Accounts.FirstOrDefault(a => a.Id == Current.ActiveAccountId) ?? Current.Accounts.FirstOrDefault();
}
