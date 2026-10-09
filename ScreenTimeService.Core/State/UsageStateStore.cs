using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScreenTime.Common;
using ScreenTimeService.Security;
using ScreenTimeService.Time;

namespace ScreenTimeService.State;

/// <summary>Atomically loads/saves <see cref="UsageState"/> under the locked-down ProgramData directory.</summary>
public sealed class UsageStateStore(ILogger<UsageStateStore> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public UsageState LoadOrCreate(IClock clock, string sid)
    {
        AclHelper.EnsureProgramDataDirectory(Paths.ProgramDataDirectory);
        Directory.CreateDirectory(Paths.StateDirectory);

        var path = Paths.StateFilePathFor(sid);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<UsageState>(json);
                if (state is not null)
                {
                    return state;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Failed to read {Path}; starting from a fresh state.", path);
            }
        }

        return new UsageState
        {
            Date = DateOnly.FromDateTime(clock.LocalNow),
            UsedSeconds = 0,
            LastObservedUtc = clock.UtcNow,
            MonotonicAnchorTicks = clock.MonotonicTicks,
            ExhaustedNotified = false,
            CheckpointNotified = false,
        };
    }

    public void Save(UsageState state, string sid)
    {
        AclHelper.EnsureProgramDataDirectory(Paths.ProgramDataDirectory);
        Directory.CreateDirectory(Paths.StateDirectory);

        var path = Paths.StateFilePathFor(sid);
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        File.WriteAllText(tempPath, json);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
