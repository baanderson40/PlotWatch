using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlotWatch.Lottery;

namespace PlotWatch.Data;

public sealed record BidMessageMatch(int DistrictId, int Ward, int Plot, int Ticket, long CycleId,
    DateTimeOffset ResultsAtUtc);

/// <summary>Complete installed-template match. Time output is evaluated at the expected UTC boundary,
/// not guessed from numeric tokens. Unsupported expressions fail closed.</summary>
public static class BidMessageParser
{
    public static BidMessageMatch? Match(string macro, string message, IReadOnlyDictionary<int, string> districtNames,
        DateTimeOffset receivedAt, TimeZoneInfo zone, int dateFormat)
    {
        if (macro.Length is 0 or > 8192 || message.Length is 0 or > 4096 || dateFormat is < 0 or > 3) return null;
        var schedule = LotterySchedule.GetSnapshot(receivedAt);
        var boundary = schedule.Results.StartsAtUtc;
        // Allow delivery just after the boundary without selecting the next Application occurrence.
        if (receivedAt < boundary - LotterySchedule.ApplicationLength || receivedAt > boundary.AddMinutes(2)) return null;
        var time = TimeZoneInfo.ConvertTime(boundary, zone);
        if (zone.IsAmbiguousTime(time.DateTime) || zone.IsInvalidTime(time.DateTime)) return null;
        var matches = new List<BidMessageMatch>();
        foreach (var district in districtNames)
        {
            if (district.Key is not (339 or 340 or 341 or 641 or 979) || string.IsNullOrWhiteSpace(district.Value)) continue;
            try
            {
                var compiler = new Compiler(time, dateFormat, district.Value);
                var pattern = compiler.Render(macro, 0);
                if (!compiler.Complete) return null;
                var match = Regex.Match(message, @"\A" + pattern + @"\z", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(30));
                if (!match.Success) continue;
                var ward = int.Parse(match.Groups["ward"].Value, CultureInfo.InvariantCulture);
                var plot = int.Parse(match.Groups["plot"].Value, CultureInfo.InvariantCulture);
                var ticket = int.Parse(match.Groups["ticket"].Value, CultureInfo.InvariantCulture);
                if (ward is < 1 or > 30 || plot is < 1 or > 60 || ticket <= 0) continue;
                matches.Add(new(district.Key, ward, plot, ticket, schedule.CycleId, boundary));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or RegexMatchTimeoutException)
            { return null; }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private sealed class Compiler(DateTimeOffset time, int dateFormat, string district)
    {
        private readonly HashSet<string> fields = [];
        private bool setTime;
        public bool Complete => setTime && new[] { "ward", "plot", "ticket", "district", "t_year", "t_mon", "t_day", "t_hour", "t_min" }.All(fields.Contains);

        public string Render(string text, int depth)
        {
            if (depth > 16) throw new FormatException();
            var output = new StringBuilder();
            for (var i = 0; i < text.Length;)
            {
                if (text[i] == '\\' && i + 1 < text.Length) { output.Append(Regex.Escape(text[++i].ToString())); i++; }
                else if (char.IsWhiteSpace(text[i]))
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    output.Append(@"\s+");
                }
                else if (text[i] != '<') output.Append(Regex.Escape(text[i++].ToString()));
                else
                {
                    var end = End(text, i);
                    var (name, args) = Token(text[(i + 1)..end]);
                    string part;
                    if (name == "num" && args.Length == 1 && args[0] is "lnum2" or "lnum3" or "lnum4")
                    {
                        var field = args[0] switch { "lnum2" => "ward", "lnum3" => "plot", _ => "ticket" };
                        if (!fields.Add(field)) throw new FormatException();
                        part = "(?<" + field + ">[0-9]{1," + (field == "ticket" ? "9" : "2") + "})";
                    }
                    else if (name == "sheet" && args.SequenceEqual(new[] { "PlaceName", "<sheet(TerritoryType,lnum1,5)>", "0" }))
                    {
                        if (!fields.Add("district")) throw new FormatException();
                        part = Regex.Escape(district);
                    }
                    else if (name == "settime" && args.SequenceEqual(new[] { "lnum5" }))
                    { if (setTime) throw new FormatException(); setTime = true; part = ""; }
                    else if (name == "num" && args.Length == 1) part = Number(args[0], depth + 1).ToString(CultureInfo.InvariantCulture);
                    else if (name == "sec" && args.SequenceEqual(new[] { "t_min" })) part = Number("t_min", depth + 1).ToString("00", CultureInfo.InvariantCulture);
                    else if (name == "if" && args.Length == 3)
                        part = Render(args[Number(args[0], depth + 1) != 0 ? 1 : 2], depth + 1);
                    else if (name == "switch" && args.Length >= 2)
                    {
                        var index = Number(args[0], depth + 1);
                        if (index < 1 || index >= args.Length) throw new FormatException();
                        part = Render(args[index], depth + 1);
                    }
                    else if (name is "colortype" or "edgecolortype" && args.Length == 1 && uint.TryParse(args[0], out _)) part = "";
                    else throw new FormatException();
                    output.Append(part);
                    i = end + 1;
                }
            }
            return output.ToString();
        }

        private int Number(string expression, int depth)
        {
            if (depth > 16) throw new FormatException();
            if (int.TryParse(expression, NumberStyles.None, CultureInfo.InvariantCulture, out var literal)) return literal;
            if (expression.StartsWith('[') && expression.EndsWith(']'))
            {
                var match = Regex.Match(expression, @"\A\[(t_hour|gnum77)(==|>=)([0-9]{1,2})\]\z");
                if (!match.Success) throw new FormatException();
                var left = Number(match.Groups[1].Value, depth + 1);
                var right = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                return (match.Groups[2].Value == "==" ? left == right : left >= right) ? 1 : 0;
            }
            if (expression.StartsWith('<'))
            {
                var rendered = Render(expression, depth + 1);
                if (!int.TryParse(rendered, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new FormatException();
                return value;
            }
            if (expression == "gnum77") return dateFormat;
            fields.Add(expression);
            return expression switch
            {
                "t_hour" => time.Hour, "t_min" => time.Minute, "t_day" => time.Day,
                "t_mon" => time.Month, "t_year" => time.Year, _ => throw new FormatException(),
            };
        }

        private static (string Name, string[] Args) Token(string token)
        {
            var opening = token.IndexOf('(');
            if (opening < 1 || !token.EndsWith(')')) throw new FormatException();
            var content = token[(opening + 1)..^1];
            var args = new List<string>();
            var start = 0;
            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] == '\\') i++;
                else if (content[i] == '<') i = End(content, i);
                else if (content[i] == ',') { args.Add(content[start..i]); start = i + 1; }
            }
            args.Add(content[start..]);
            return (token[..opening], args.ToArray());
        }

        private static int End(string text, int start)
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
            throw new FormatException();
        }
    }
}
