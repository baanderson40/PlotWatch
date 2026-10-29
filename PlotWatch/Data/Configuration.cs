using Dalamud.Configuration;
using PlotWatch.Windows;

namespace PlotWatch.Data;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public int DefaultTab { get; set; } = (int)PlotWatchTab.Dashboard;

    public float TextScalePercent { get; set; } = 100f;

    public bool DisplayLotteryTimesInUtc { get; set; }

    public bool ChatNotificationsEnabled { get; set; } = true;

    public bool ToastNotificationsEnabled { get; set; } = true;

    public bool SoundNotificationsEnabled { get; set; }

    public void Normalize()
    {
        if (DefaultTab < 0 || DefaultTab >= Enum.GetValues<PlotWatchTab>().Length)
            DefaultTab = (int)PlotWatchTab.Dashboard;

        TextScalePercent = Math.Clamp(TextScalePercent, 75f, 150f);

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
        Version = CurrentVersion;
    }
}
