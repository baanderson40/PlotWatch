namespace PlotWatch.Paissa;

/// <summary>
/// Bundled logical data-center assignments. World.DataCenter currently collapses multiple
/// logical DCs in the same region to a representative group (for example, NA worlds to Aether).
/// World names/IDs still come from the game World sheet; unmapped names stay explicitly unknown.
/// </summary>
public static class WorldDataCenterMap
{
    private sealed record DataCenter(int Id, string Name);

    private static readonly DataCenter Unknown = new(0, "Unknown data center");
    private static readonly Dictionary<string, DataCenter> ByWorldName = BuildMap();

    public static PaissaWorld CreateWorld(int id, string name)
    {
        var dataCenter = ByWorldName.GetValueOrDefault(name, Unknown);
        return new PaissaWorld(id, name, dataCenter.Id, dataCenter.Name);
    }

    public static bool IsExcludedTestWorld(string name)
        => string.Equals(name, "CloudTest01", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "CloudTest02", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetDataCenter(string worldName, out int id, out string name)
    {
        if (ByWorldName.TryGetValue(worldName, out var dataCenter))
        {
            id = dataCenter.Id;
            name = dataCenter.Name;
            return true;
        }

        id = Unknown.Id;
        name = Unknown.Name;
        return false;
    }

    private static Dictionary<string, DataCenter> BuildMap()
    {
        var map = new Dictionary<string, DataCenter>(StringComparer.OrdinalIgnoreCase);
        Add(map, 1, "Aether", "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren");
        Add(map, 2, "Crystal", "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera");
        Add(map, 3, "Dynamis", "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph");
        Add(map, 4, "Primal", "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros");

        Add(map, 5, "Chaos", "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan");
        Add(map, 6, "Light", "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark");
        Add(map, 7, "Shadow", "Innocence", "Pixie", "Titania", "Tycoon");
        Add(map, 8, "Materia", "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan");

        Add(map, 9, "Elemental", "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon");
        Add(map, 10, "Gaia", "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima");
        Add(map, 11, "Mana", "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan");
        Add(map, 12, "Meteor", "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus");
        return map;
    }

    private static void Add(Dictionary<string, DataCenter> map, int id, string dataCenterName, params string[] worlds)
    {
        var dataCenter = new DataCenter(id, dataCenterName);
        foreach (var world in worlds)
            map.Add(world, dataCenter);
    }
}
