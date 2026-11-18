using PlotWatch.Paissa;

internal static class WorldDataCenterTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("game worlds use bundled logical data-center assignments", LogicalAssignments),
        ("worlds missing from bundled map remain explicitly unknown", UnknownWorld),
        ("bundled world map keeps DCs distinct within a region", RegionalCounts),
    ];

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static void LogicalAssignments()
    {
        var cases = new (string World, string Dc)[]
        {
            ("Adamantoise", "Aether"), ("Behemoth", "Primal"), ("Balmung", "Crystal"),
            ("Seraph", "Dynamis"), ("Moogle", "Chaos"), ("Odin", "Light"),
            ("Aegis", "Elemental"), ("Alexander", "Gaia"), ("Anima", "Mana"),
            ("Belias", "Meteor"), ("Ravana", "Materia"),
        };

        foreach (var (world, expectedDc) in cases)
        {
            var assigned = WorldDataCenterMap.CreateWorld(123, world);
            Equal(123, assigned.Id);
            Equal(expectedDc, assigned.DataCenterName);
        }
    }

    private static void UnknownWorld()
    {
        var assigned = WorldDataCenterMap.CreateWorld(777, "FutureWorld");
        Equal(0, assigned.DataCenterId);
        Equal("Unknown data center", assigned.DataCenterName);
        Equal(false, WorldDataCenterMap.TryGetDataCenter("FutureWorld", out _, out _));
        Equal(true, WorldDataCenterMap.IsExcludedTestWorld("CloudTest01"));
        Equal(true, WorldDataCenterMap.IsExcludedTestWorld("CloudTest02"));
        Equal(false, WorldDataCenterMap.IsExcludedTestWorld("Adamantoise"));
    }

    private static void RegionalCounts()
    {
        var knownWorlds = new[]
        {
            "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
            "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
            "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",
            "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
        };
        var groups = knownWorlds.Select(name => WorldDataCenterMap.CreateWorld(1, name))
            .GroupBy(world => world.DataCenterName).ToDictionary(group => group.Key, group => group.Count());
        Equal(8, groups["Aether"]);
        Equal(8, groups["Crystal"]);
        Equal(8, groups["Dynamis"]);
        Equal(8, groups["Primal"]);
    }
}
