namespace PlotWatch.Data;

/// <summary>Apply character changes once, independently of directory downloads or window visibility.</summary>
public static class HousingCharacterSelection
{
    public static bool Apply(Configuration configuration, ISet<int> selectedWorldIds, int? homeWorldId, ulong? contentId)
    {
        if (contentId is not > 0 || homeWorldId is not > 0 ||
            configuration.LastHousingCharacterContentId == contentId.Value)
            return false;

        // The directory can arrive later. Keep the known home ID selected in the meantime.
        selectedWorldIds.Clear();
        selectedWorldIds.Add(homeWorldId.Value);
        configuration.HousingWorldIds = [homeWorldId.Value];
        configuration.LastHousingCharacterContentId = contentId.Value;
        return true;
    }
}
