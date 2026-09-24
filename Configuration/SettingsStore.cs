using System.Text.Json;
using HdrCapture.Infrastructure;
using System.IO;

namespace HdrCapture.Configuration;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directoryPath;
    private readonly string _filePath;

    public SettingsStore(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRCapture");
        _filePath = Path.Combine(_directoryPath, "settings.json");
    }

    public string FilePath => _filePath;

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error("Settings file is invalid; restoring defaults.", ex);
            TryBackupInvalidFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(_directoryPath);

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private void TryBackupInvalidFile()
    {
        try
        {
            var backupPath = _filePath + "." +
                DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".bad";
            File.Move(_filePath, backupPath, overwrite: false);
        }
        catch
        {
        }
    }
}
