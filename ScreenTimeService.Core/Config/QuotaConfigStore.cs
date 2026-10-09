using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScreenTime.Common;
using ScreenTimeService.Security;

namespace ScreenTimeService.Config;

/// <summary>Loads the admin-editable <see cref="QuotaConfig"/> policy, seeding defaults on first run.</summary>
public sealed class QuotaConfigStore(ILogger<QuotaConfigStore> logger)
{
    // JsonStringEnumConverter so DayOfWeek fields (DailyLimitOverrides, ExceptionWindows) read and
    // write as "Thursday" rather than a raw integer — this file is hand-edited by an admin.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public QuotaConfig LoadOrCreate()
    {
        AclHelper.EnsureProgramDataDirectory(Paths.ProgramDataDirectory);

        if (File.Exists(Paths.ConfigFilePath))
        {
            try
            {
                var json = File.ReadAllText(Paths.ConfigFilePath);
                var config = JsonSerializer.Deserialize<QuotaConfig>(json, SerializerOptions);
                if (config is not null)
                {
                    return config;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Failed to read {Path}; falling back to defaults.", Paths.ConfigFilePath);
            }
        }

        var defaults = new QuotaConfig();
        Save(defaults);
        return defaults;
    }

    private void Save(QuotaConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(Paths.ConfigFilePath, json);
    }
}
