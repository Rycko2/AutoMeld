using System.Text.Json;

namespace AutoMeld;

public sealed class Configuration
{
    public bool ConfirmBeforeStarting { get; set; } = true;
    public string LastImportPath { get; set; } = string.Empty;

    public static Configuration Load(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) return new Configuration();
        try
        {
            return JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)) ?? new Configuration();
        }
        catch (JsonException)
        {
            return new Configuration();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}