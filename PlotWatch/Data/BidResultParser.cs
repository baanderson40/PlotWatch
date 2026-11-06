using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlotWatch.Lottery;

namespace PlotWatch.Data;

public sealed record BidResultMatch(uint TemplateRow, int? WinningNumber, long? CycleId, DateTimeOffset? CycleBoundaryUtc);

/// <summary>Anchored match against installed result-dialog template, not token scanning.</summary>
public static class BidResultParser
{
    public static BidResultMatch? Match(string text, IReadOnlyDictionary<uint, string> templates, TimeZoneInfo? timeZone = null,
        int? dateFormat = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return null;
        var results = new List<BidResultMatch>();
        foreach (var pair in templates.Where(item => item.Key is 7122 or 7124 or 7128))
        {
            try
            {
                var pattern = Compile(pair.Value, 0, dateFormat);
                if (pattern is null) continue;
                var match = Regex.Match(text, @"\A" + pattern + @"\z", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(30));
                if (!match.Success) continue;
                int? number = null;
                if (match.Groups["winningNumber"].Success)
                {
                    if (!int.TryParse(match.Groups["winningNumber"].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) continue;
                    number = parsed;
                }
                else if (pair.Key != 7128) continue;
                var cycle = ResolveCycle(pair.Key, match, timeZone ?? TimeZoneInfo.Local, dateFormat);
                results.Add(new(pair.Key, number, cycle?.CycleId, cycle?.BoundaryUtc));
            }
            catch (RegexMatchTimeoutException) { return null; }
        }
        return results.Count == 1 ? results[0] : null;
    }

    private static string? Compile(string template, int depth, int? dateFormat)
    {
        if (depth > 12 || template.Length > 8192) return null;
        var output = new StringBuilder();
        for (var i = 0; i < template.Length;)
        {
            if (template[i] == '\\' && i + 1 < template.Length)
            { output.Append(Regex.Escape(template[++i].ToString())); i++; continue; }
            if (char.IsWhiteSpace(template[i]))
            {
                while (i < template.Length && char.IsWhiteSpace(template[i])) i++;
                output.Append(@"\s+"); continue;
            }
            if (template[i] != '<') { output.Append(Regex.Escape(template[i++].ToString())); continue; }
            var end = TokenEnd(template, i);
            if (end < 0) return null;
            var token = template[(i + 1)..end];
            var opening = token.IndexOf('(');
            var name = opening < 0 ? token : token[..opening];
            var args = opening < 0 ? [] : SplitArgs(token[(opening + 1)..^1]);
            string? part = null;
            if (name == "num" && args.SequenceEqual(new[] { "lnum1" }))
                part = "(?<winningNumber>[0-9]{1,9})";
            else if (name == "num" && args.Length == 1 && args[0] is "lnum2" or "lnum3" or "t_day" or "t_mon")
                part = args[0] == "t_day" ? "(?<day>[0-9]{1,2})" : args[0] == "t_mon" ? "(?<month>[0-9]{1,2})" : "[0-9]{1,2}";
            else if (name == "num" && args.SequenceEqual(new[] { "t_year" })) part = "(?<year>[0-9]{4})";
            else if (name == "num" && args.SequenceEqual(new[] { "t_hour" })) part = "(?<hour>[0-9]{1,2})";
            else if (name == "kilo" && args.Length == 2 && args[0] is "lnum1" or "lnum2")
                part = "[0-9][0-9, .\\u00a0\\u202f]{0,23}";
            else if (name == "settime" && args.Length == 1 && args[0] is "lnum2" or "lnum3") part = "";
            else if (name is "if" or "switch")
            {
                var branches = args.Skip(1).Select(branch => Compile(branch, depth + 1, dateFormat)).ToArray();
                if (branches.Length < 2 || branches.Any(branch => branch is null)) return null;
                if (name == "if" && args.Length == 3 && args[0] == "[gnum77==3]" && dateFormat is { } format)
                {
                    var selected = format == 3 ? branches[0] : branches[1];
                    output.Append(selected);
                    i = end + 1;
                    continue;
                }
                if (name == "switch" && args[0] == "t_hour")
                    branches = args.Skip(1).Select(branch => "(?<hour>" + Regex.Escape(branch) + ")").ToArray();
                if (name == "if" && args.Length == 3 && args[0] == "[t_hour>=12]")
                    branches = args.Skip(1).Select(branch => "(?<ampm>" + Regex.Escape(branch) + ")").ToArray();
                part = "(?:" + string.Join("|", branches) + ")";
            }
            else if (name is "br" or "newline" && args.Length == 0) part = @"\s*";
            else if (name is "colortype" or "edgecolortype" && args.Length == 1 && uint.TryParse(args[0], out _)) part = "";
            else if (name == "sec" && args.SequenceEqual(new[] { "t_min" })) part = "(?<minute>[0-5][0-9])";
            else if (name == "ordinal" && args.Length == 1 && args[0].StartsWith("lnum", StringComparison.Ordinal)) part = "[0-9]{1,3}(?:st|nd|rd|th)?";
            if (part is null) return null;
            output.Append(part);
            i = end + 1;
        }
        return output.ToString();
    }

    private static (long CycleId, DateTimeOffset BoundaryUtc)? ResolveCycle(uint row, Match match, TimeZoneInfo zone, int? dateFormat)
    {
        if (dateFormat is null or < 0 or > 3) return null;
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute)) return null;
        var ampm = match.Groups["ampm"].Value;
        if (hour is < 1 or > 12 || ampm.Length == 0) return null;
        hour %= 12;
        if (ampm.Contains("p", StringComparison.OrdinalIgnoreCase)) hour += 12;
        DateTime displayed;
        try { displayed = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return null; }

        var rough = (long)Math.Floor((displayed - LotterySchedule.ApplicationAnchorUtc.UtcDateTime).TotalDays /
            LotterySchedule.CycleLength.TotalDays);
        // Refund-expiry display is scheduled 90 days after the cycle claim deadline.
        // Ninety days is ten complete nine-day cycles; search near the originating cycle.
        if (row == 7124) rough -= 10;
        var candidates = new List<(long CycleId, DateTimeOffset BoundaryUtc)>();
        for (var cycle = rough - 4; cycle <= rough + 4; cycle++)
        {
            try
            {
                var dates = BidPolicy.GetCalculatedDates(cycle);
                var boundary = row == 7124 ? dates.Claim.Value.AddDays(90) : dates.Claim.Value;
                var expected = TimeZoneInfo.ConvertTime(boundary, zone).DateTime.AddMinutes(-1);
                if (expected.Year == displayed.Year && expected.Month == displayed.Month && expected.Day == displayed.Day &&
                    expected.Hour == displayed.Hour && expected.Minute == displayed.Minute)
                    candidates.Add((cycle, boundary));
            }
            catch (ArgumentOutOfRangeException) { }
            catch (OverflowException) { }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static int TokenEnd(string text, int start)
    {
        var depth = 0;
        var square = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '[') square++;
            else if (text[i] == ']') square--;
            else if (square == 0 && text[i] == '<') depth++;
            else if (square == 0 && text[i] == '>' && --depth == 0) return i;
        }
        return -1;
    }

    private static string[] SplitArgs(string text)
    {
        var args = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') i++;
            else if (text[i] == '<') { var end = TokenEnd(text, i); if (end < 0) return []; i = end; }
            else if (text[i] == ',') { args.Add(text[start..i]); start = i + 1; }
        }
        args.Add(text[start..]);
        return args.ToArray();
    }
}
