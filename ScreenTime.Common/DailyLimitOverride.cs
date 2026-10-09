namespace ScreenTime.Common;

/// <summary>
/// Replaces <see cref="QuotaConfig.DailyLimitMinutes"/> as the daily quota on a specific day of
/// the week (e.g. a higher weekend limit). Days without a matching entry keep using the default.
/// </summary>
public sealed class DailyLimitOverride
{
    public DayOfWeek DayOfWeek { get; set; }

    public int LimitMinutes { get; set; }
}
