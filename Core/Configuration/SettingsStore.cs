using System.Text.Json;
using System.IO;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;

namespace ChGhost.Core.Configuration;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly IAppLogger _logger;

    public SettingsStore(string settingsPath, IAppLogger logger)
    {
        _settingsPath = settingsPath;
        _logger = logger;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            AppSettings defaults = AppSettings.CreateDefaults();
            try
            {
                Save(defaults);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"Unable to create default settings at '{_settingsPath}'.", ex);
            }
            return defaults.Normalize();
        }

        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                ?? throw new JsonException("Settings document was empty.");
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Error($"Unable to load settings from '{_settingsPath}'; defaults will be used.", ex);
            AppSettings defaults = AppSettings.CreateDefaults();
            try
            {
                Save(defaults);
            }
            catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"Unable to rewrite default settings at '{_settingsPath}'.", saveException);
            }
            return defaults.Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
