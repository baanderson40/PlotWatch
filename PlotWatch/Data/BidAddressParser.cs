using System.Text;
using System.Text.RegularExpressions;

namespace PlotWatch.Data;

public sealed record BidAddressMatch(int DistrictId, int Ward, int Plot, int? Size, string RenderedText);

/// <summary>Extracts a complete visible placard address using installed Addon/8847 formatting.</summary>
public static class BidAddressParser
{
    public static BidAddressMatch? Match(string text, string macro, IReadOnlyDictionary<int, string> districtNames)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || macro.Length is 0 or > 4096) return null;
        var sizeStart = macro.IndexOf(" (<switch(lnum4,", StringComparison.Ordinal);
        var sizePattern = "";
        string[] sizeNames = [];
        if (sizeStart >= 0)
        {
            var end = macro.IndexOf(")>)", sizeStart, StringComparison.Ordinal);
            if (end < 0 || end + 3 != macro.Length) return null;
            sizeNames = macro[(sizeStart + " (<switch(lnum4,".Length)..end].Split(',');
            if (sizeNames.Length is < 2 or > 4 || sizeNames.Any(string.IsNullOrWhiteSpace)) return null;
            sizePattern = "(?:\\s*\\((?<size>" + string.Join("|", sizeNames.Select(Regex.Escape)) + ")\\))?";
            macro = macro[..sizeStart];
        }

        var matches = new List<BidAddressMatch>();
        foreach (var district in districtNames)
        {
            if (district.Key is not (339 or 340 or 341 or 641 or 979) || string.IsNullOrWhiteSpace(district.Value)) continue;
            var pattern = Compile(macro, district.Value);
            if (pattern is null) return null;
            try
            {
                var match = Regex.Match(text.Trim(), @"\A" + pattern + sizePattern + @"\z",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
                if (!match.Success || !int.TryParse(match.Groups["ward"].Value, out var ward) || ward is < 1 or > 30 ||
                    !int.TryParse(match.Groups["plot"].Value, out var plot) || plot is < 1 or > 60) continue;
                var size = match.Groups["size"].Value;
                var sizeIndex = size.Length == 0 ? (int?)null : Array.IndexOf(sizeNames, size);
                if (sizeIndex < 0) sizeIndex = null;
                matches.Add(new(district.Key, ward, plot, sizeIndex, text));
            }
            catch (RegexMatchTimeoutException) { return null; }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? Compile(string macro, string district)
    {
        var output = new StringBuilder();
        for (var i = 0; i < macro.Length;)
        {
            if (macro[i] != '<')
            {
                if (char.IsWhiteSpace(macro[i]))
                {
                    while (i < macro.Length && char.IsWhiteSpace(macro[i])) i++;
                    output.Append(@"\s*");
                }
                else output.Append(Regex.Escape(macro[i++].ToString()));
                continue;
            }
            var end = macro.IndexOf('>', i);
            if (end < 0) return null;
            var token = macro[i..(end + 1)];
            switch (token)
            {
                case "<num(lnum3)>": output.Append("(?<plot>[0-9]{1,2})"); break;
                case "<num(lnum2)>": output.Append("(?<ward>[0-9]{1,2})"); break;
                case "<ordinal(lnum2)>": output.Append("(?<ward>[0-9]{1,2})[^\\d\\s,]{0,8}"); break;
                case "<sheet(PlaceName,lnum1,0)>": output.Append(Regex.Escape(district)); break;
                default: return null;
            }
            i = end + 1;
        }
        return output.ToString();
    }
}
