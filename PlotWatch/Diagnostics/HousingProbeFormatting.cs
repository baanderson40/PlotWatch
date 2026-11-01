using System.Text;
using System.Text.RegularExpressions;

namespace PlotWatch.Diagnostics;

public static class HousingProbeFormatting
{
    public const int MaximumAtkValues = 512;
    public const int MaximumNodes = 1024;
    public const int MaximumDepth = 8;
    public const int PartBodyLength = 2400;

    /// <summary>Lossless bounded parts, so a large snapshot cannot hide late-index evidence.</summary>
    public static IReadOnlyList<string> SnapshotParts(string header, IEnumerable<string> details)
    {
        var body = string.Join("; ", details);
        var count = Math.Max(1, (body.Length + PartBodyLength - 1) / PartBodyLength);
        var result = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var start = index * PartBodyLength;
            var length = Math.Min(PartBodyLength, body.Length - start);
            result.Add($"{header}; part={index + 1}/{count}; {body.Substring(start, length)}");
        }
        return result;
    }

    /// <summary>Match the full localized Addon/8847 address, optionally without its size suffix.</summary>
    public static bool IsCompleteAddress(string text, string macro)
    {
        if (text.Length > 512 || macro.Length > 4096) return false;
        if (!macro.Contains("<num(lnum3)>", StringComparison.Ordinal) ||
            !(macro.Contains("<ordinal(lnum2)>", StringComparison.Ordinal) || macro.Contains("<num(lnum2)>", StringComparison.Ordinal)) ||
            !macro.Contains("<sheet(PlaceName,lnum1,0)>", StringComparison.Ordinal)) return false;
        var optionalSize = "";
        var sizeStart = macro.IndexOf("<switch(lnum4,", StringComparison.Ordinal);
        if (sizeStart >= 0)
        {
            var before = macro[..sizeStart].TrimEnd();
            var end = macro.IndexOf('>', sizeStart);
            if (end < 0) return false;
            if ((before.EndsWith('(') && macro[(end + 1)..].Trim() == ")") ||
                (before.EndsWith('（') && macro[(end + 1)..].Trim() == "）"))
            {
                var opening = before[^1];
                var sizePattern = BuildPattern(macro[sizeStart..(end + 1)]);
                if (sizePattern is null) return false;
                optionalSize = "(?:\\s*" + Regex.Escape(opening.ToString()) + sizePattern +
                    Regex.Escape(opening == '(' ? ")" : "）") + ")?";
                macro = before[..^1].TrimEnd();
            }
        }
        var pattern = BuildPattern(macro);
        if (pattern is null) return false;
        try
        {
            var match = Regex.Match(text.Trim(), "\\A" + pattern + optionalSize + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return match.Success && int.TryParse(match.Groups["plot"].Value, out var plot) && plot is >= 1 and <= 60 &&
                int.TryParse(match.Groups["ward"].Value, out var ward) && ward is >= 1 and <= 30;
        }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static string? BuildPattern(string macro)
    {
        var result = new StringBuilder();
        for (var index = 0; index < macro.Length;)
        {
            if (macro[index] != '<')
            {
                if (char.IsWhiteSpace(macro[index]))
                {
                    while (index < macro.Length && char.IsWhiteSpace(macro[index])) index++;
                    result.Append(@"\s*");
                }
                else result.Append(Regex.Escape(macro[index++].ToString()));
                continue;
            }
            var end = macro.IndexOf('>', index);
            if (end < 0) return null;
            var token = macro[index..(end + 1)];
            switch (token)
            {
                case "<num(lnum3)>": result.Append(@"(?<plot>\d{1,2})"); break;
                case "<num(lnum2)>": result.Append(@"(?<ward>\d{1,2})"); break;
                case "<ordinal(lnum2)>": result.Append(@"(?<ward>\d{1,2})[^\d\s,]{0,8}"); break;
                case "<sheet(PlaceName,lnum1,0)>": result.Append(@"[\p{L}\p{M}\p{N} '\-’・]{1,100}"); break;
                default:
                    if (!token.StartsWith("<switch(lnum4,", StringComparison.Ordinal) || !token.EndsWith(")>", StringComparison.Ordinal)) return null;
                    var sizes = token[14..^2].Split(',');
                    result.Append("(?:" + string.Join("|", sizes.Select(Regex.Escape)) + ")");
                    break;
            }
            index = end + 1;
        }
        return result.ToString();
    }
}
