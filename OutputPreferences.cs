using System.Text.Json;

namespace FS80H;

record AppPreferences(string SaveDirectory = "", string Alias = "", bool Auto = true, bool Series = true, int Samples = 5, int RecognitionThreshold = 40);

static class OutputPreferences
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FutronicFS80H", "settings.json");
    public static AppPreferences LoadSettings(string? settingsPath = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(settingsPath ?? SettingsPath)) ?? new();
            string directory = settings.SaveDirectory ?? "";
            string alias = new string((settings.Alias ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
            return settings with { SaveDirectory = Path.IsPathFullyQualified(directory) ? directory : "", Alias = alias, Samples = Math.Clamp(settings.Samples, 2, 10), RecognitionThreshold = Math.Clamp(settings.RecognitionThreshold, 30, 60) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public static string Load(string? settingsPath = null) => LoadSettings(settingsPath).SaveDirectory;
    public static void Save(string directory, string? settingsPath = null) => SaveSettings(LoadSettings(settingsPath) with { SaveDirectory = directory }, settingsPath);
    public static void SaveSettings(AppPreferences settings, string? settingsPath = null)
    {
        if (settings.SaveDirectory.Length > 0 && !Path.IsPathFullyQualified(settings.SaveDirectory)) throw new ArgumentException("Нужен полный путь папки.");
        string path = settingsPath ?? SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
