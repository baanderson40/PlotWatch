using Dalamud.Configuration;
using PlotWatch.Windows;

namespace PlotWatch.Data;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 8;

    public int Version { get; set; } = CurrentVersion;

    public int DefaultTab { get; set; } = (int)PlotWatchTab.Dashboard;

    public float TextScalePercent { get; set; } = 100f;

    public bool DisplayLotteryTimesInUtc { get; set; }

    public bool ChatNotificationsEnabled { get; set; } = true;

    public bool ToastNotificationsEnabled { get; set; } = true;

    public bool SoundNotificationsEnabled { get; set; }

    public List<int> HousingWorldIds { get; set; } = [];
    public ulong LastHousingCharacterContentId { get; set; }
    public List<int> HousingDistrictIds { get; set; } = [339, 340, 341, 641, 979];
    public int HousingSizeMask { get; set; } = 7;
    public int HousingEligibilityMask { get; set; } = 7;
    public int HousingPhaseMask { get; set; } = 15;
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
        HousingPhaseMask &= 15;

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
        HousingDistrictIds = [339, 340, 341, 641, 979];
        HousingSizeMask = HousingEligibilityMask = 7;
        HousingPhaseMask = 15;
        HousingRefreshCooldownUntilUtc = default;
        HousingNextAutomaticRefreshUtc = default;
        Version = CurrentVersion;
    }
}
