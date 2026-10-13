using System.IO;
using System.Text.Json;
using ExcelSearch.Data;

namespace ExcelSearch.Services;

/// <summary>
/// Settings the user changes inside the app (today: which database to use). Stored per Windows user in
/// %LocalAppData%\ExcelSearch\user-settings.json and read at startup as the last configuration source, so it wins over appsettings.
/// </summary>
public static class UserSettings
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExcelSearch", "user-settings.json");

    public static void SaveProvider(DatabaseProvider provider)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(new { Database = new { Provider = provider.ToString() } }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

/// <summary>Restarting the app (a database switch needs one).</summary>
public interface IAppLifecycle
{
    void Restart();
}
