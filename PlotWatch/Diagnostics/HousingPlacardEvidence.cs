using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlotWatch.Diagnostics;

/// <summary>Allowlisted, template-shaped rendered text only. Never a native ID/parameter decoder.</summary>
public static class HousingPlacardEvidence
{
    public static string? Describe(string text, IReadOnlyList<HousingProbeTemplate> templates)
    {
        if (text.Length is 0 or > HousingProbeState.MaximumDetailLength) return null;
        var evidence = new List<string>();
        foreach (var template in templates.Where(item => item.Sheet == "Addon" && item.Row is 7040 or 7041 or 7043 or 7044))
        {
            if (template.Macro.Length > 8192) continue;
            var pattern = Pattern(template.Macro, template.Row, 0);
            if (pattern is null) continue;
            try
            {
                // No arbitrary string wildcards: dynamic output is limited to known numeric time/count macros.
                foreach (Match match in Regex.Matches(text, @"(?<![\p{L}\p{N}])(?:" + pattern + @")(?![\p{L}\p{N}])",
                             RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Take(4))
                {
                    if (match.Length == 0) continue;
                    var category = template.Row switch
                    {
                        7040 => "fc-eligibility", 7041 => "personal-eligibility",
                        7043 => "purchase-status", _ => "displayed-deadline",
                    };
                    var detail = $"{category}-text=[{HousingProbeState.Clean(match.Value)}]; template-shape=Addon/{template.Row}";
                    if (template.Row == 7043 && match.Groups["participants"].Success &&
                        long.TryParse(match.Groups["participants"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                        detail += $"; rendered-participants={count}";
                    evidence.Add(detail);
                }
            }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return evidence.Count == 0 ? null :
            "placard-rendered-observations: " + string.Join("; ", evidence.Distinct()) +
            "; rendered text only: NOT native message identity or verified local-number values; " +
            "displayed deadline preserved verbatim, not converted to Unix time; unmatched text withheld";
    }

    private static string? Pattern(string macro, uint row, int depth)
    {
        if (depth > 12) return null;
        var result = new StringBuilder();
        for (var index = 0; index < macro.Length;)
        {
            if (macro[index] == '\\' && index + 1 < macro.Length)
            {
                result.Append(Regex.Escape(macro[index + 1].ToString()));
                index += 2;
            }
            else if (char.IsWhiteSpace(macro[index]))
            {
                while (index < macro.Length && char.IsWhiteSpace(macro[index])) index++;
                result.Append(@"\s+");
            }
            else if (macro[index] != '<') result.Append(Regex.Escape(macro[index++].ToString()));
            else
            {
                var end = TokenEnd(macro, index);
                if (end < 0) return null;
                var token = macro[(index + 1)..end];
                var opening = token.IndexOf('(');
                var name = opening < 0 ? token : token[..opening];
                var args = opening < 0 ? [] : Arguments(token[(opening + 1)..^1]);
                string? part = null;
                if (name is "switch" or "if")
                {
                    if (args.Length < 2 || (name == "if" && args.Length != 3)) return null;
                    var branches = args.Skip(1).Select(branch => Pattern(branch, row, depth + 1)).ToArray();
                    if (branches.Any(branch => branch is null)) return null;
                    part = "(?:" + string.Join("|", branches) + ")";
                }
                else if (name == "num" && args.Length == 1)
                    part = args[0] switch
                    {
                        "lnum2" when row == 7043 => @"(?<participants>[0-9]{1,9})",
                        "t_day" or "t_mon" when row == 7044 => @"[0-9]{1,2}",
                        "t_year" when row == 7044 => @"[0-9]{4}",
                        _ => null,
                    };
                else if (name == "sec" && row == 7044 && args.SequenceEqual(new[] { "t_min" })) part = @"[0-5][0-9]";
                else if (name == "settime" && row == 7044 && args.SequenceEqual(new[] { "lnum1" })) part = "";
                else if (name == "br" && args.Length == 0) part = @"\s*";
                else if (name is "colortype" or "edgecolortype" && args.Length == 1 && uint.TryParse(args[0], out _)) part = "";
                if (part is null) return null; // Fail closed for unsupported macros, never substitute arbitrary text.
                result.Append(part);
                index = end + 1;
            }
        }
        return result.ToString();
    }

    private static int TokenEnd(string text, int start)
    {
        var depth = 0;
        var square = 0;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\') { index++; continue; }
            if (character == '[') square++;
            else if (character == ']') square--;
            else if (square == 0 && character == '<') depth++;
            else if (square == 0 && character == '>' && --depth == 0) return index;
        }
        return -1;
    }

    private static string[] Arguments(string text)
    {
        var result = new List<string>();
        var start = 0;
        var square = 0;
        var parentheses = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\') { index++; continue; }
            if (character == '[') square++;
            else if (character == ']') square--;
            else if (square == 0 && character == '<')
            {
                var end = TokenEnd(text, index);
                if (end < 0) return [];
                index = end;
            }
            else if (square == 0 && character == '(') parentheses++;
            else if (square == 0 && character == ')') parentheses--;
            else if (character == ',' && square == 0 && parentheses == 0)
            {
                result.Add(text[start..index]);
                start = index + 1;
            }
        }
        result.Add(text[start..]);
        return result.ToArray();
    }
}
