using Dalamud.Configuration;
using PlotWatch.Windows;

namespace PlotWatch.Data;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 10;

    public int Version { get; set; } = CurrentVersion;

    public int DefaultTab { get; set; } = (int)PlotWatchTab.Dashboard;

    public float TextScalePercent { get; set; } = 100f;

    public bool DisplayLotteryTimesInUtc { get; set; }

    public bool ChatNotificationsEnabled { get; set; } = true;

    public bool ToastNotificationsEnabled { get; set; } = true;

    public bool SoundNotificationsEnabled { get; set; }

    public List<int> HousingWorldIds { get; set; } = [];
    public ulong LastHousingCharacterContentId { get; set; }
    // Json.NET otherwise appends saved selections to the initialized defaults.
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<int> HousingDistrictIds { get; set; } = [339, 340, 341, 641, 979];
    public int HousingSizeMask { get; set; } = 7;
    public int HousingEligibilityMask { get; set; } = 7;
    public int HousingPhaseMask { get; set; } = 7;
    public int HousingStatusMask { get; set; } = 15;
    public bool HousingIncludeOutdatedReports { get; set; } = true;
    public DateTimeOffset HousingRefreshCooldownUntilUtc { get; set; }
    public DateTimeOffset HousingNextAutomaticRefreshUtc { get; set; }

    public void Normalize()
    {
        if (DefaultTab < 0 || DefaultTab >= Enum.GetValues<PlotWatchTab>().Length)
            DefaultTab = (int)PlotWatchTab.Dashboard;
        else if ((PlotWatchTab)DefaultTab == PlotWatchTab.Sweeps)
            DefaultTab = (int)PlotWatchTab.Dashboard;

        TextScalePercent = Math.Clamp(TextScalePercent, 75f, 150f);
        HousingWorldIds ??= [];
        HousingDistrictIds ??= [339, 340, 341, 641, 979];
        HousingDistrictIds.RemoveAll(id => id < 0);
        HousingSizeMask &= 7;
        HousingEligibilityMask &= 7;
        if (Version < 9)
        {
            // Legacy masks used enum ordinal bits: Any=1 (unused), Entry=2,
            // Results=4, Unavailable=8, Unknown=16. Availability is no longer a phase.
            var oldMask = HousingPhaseMask;
            HousingPhaseMask = ((oldMask & (2 | 8)) != 0 ? 1 : 0)
                | ((oldMask & (4 | 8)) != 0 ? 2 : 0)
                | ((oldMask & 16) != 0 ? 4 : 0);
        }
        HousingPhaseMask &= 7;
        if (Version < 10)
        {
            HousingStatusMask = 15;
            HousingIncludeOutdatedReports = true;
        }
        HousingStatusMask &= 15;

        Version = CurrentVersion;
    }

    public void ResetToDefaults()
    {
        DefaultTab = (int)PlotWatchTab.Dashboard;
        TextScalePercent = 100f;
        DisplayLotteryTimesInUtc = false;
        ChatNotificationsEnabled = true;
        ToastNotificationsEnabled = true;
        SoundNotificationsEnabled = false;
        HousingWorldIds = [];
        LastHousingCharacterContentId = 0;
        HousingDistrictIds = [339, 340, 341, 641, 979];
        HousingSizeMask = HousingEligibilityMask = 7;
        HousingPhaseMask = 7;
        HousingStatusMask = 15;
        HousingIncludeOutdatedReports = true;
        HousingRefreshCooldownUntilUtc = default;
        HousingNextAutomaticRefreshUtc = default;
        Version = CurrentVersion;
    }
}
