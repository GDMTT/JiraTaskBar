using System.Text.Json;
using System.Text.Json.Serialization;

namespace JiraTaskBar.Core.Storage;

/// <summary>Reads and atomically writes a JSON file in the app data folder.</summary>
public sealed class JsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public JsonFileStore(string path) => _path = path;

    public string FilePath => _path;

    public T? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize<T>(stream, Options);
            }
            catch (JsonException)
            {
                // Keep the corrupt file for inspection and start fresh.
                File.Copy(_path, _path + ".corrupt", overwrite: true);
                return null;
            }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JiraTaskBar");

    public static string File(string name) => Path.Combine(DataDirectory, name);
}
