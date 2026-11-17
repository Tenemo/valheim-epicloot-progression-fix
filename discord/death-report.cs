using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EpicLootProgressionFix
{
    // Shared by the live report and offline tests; no game, Discord connection or saved delivery state is needed.
    internal static class DeathReport
    {
        internal sealed class Text
        {
            internal string PlayerTitle = "Death ranking — last 24 hours";
            internal string CauseTitle = "What kills us — last 24 hours";
            internal string OtherPlayers = "Other players:";
            internal string OtherCauses = "Other causes:";
            internal string NoDeaths = "Nobody died.";
            internal string TotalDeaths = "Total deaths:";
            internal string MissingName = "Player";
            internal string UnknownCause = "Unknown";
        }

        internal sealed class Row
        {
            internal string Key, Name;
            internal int Count;
            internal DateTime LatestUtc;
        }

        internal static string Plain(string value, string fallback, int limit = 500)
        {
            string Clean(string text) => Regex.Replace(new string(Regex.Replace(text ?? "", @"<[^>]+>", "")
                .Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
                .ToArray()).Replace('`', '\''), @"\s+", " ").Trim();
            value = Clean(value);
            if (value.Length == 0) value = Clean(fallback);
            if (value.Length <= limit) return value;
            int end = StringInfo.ParseCombiningCharacters(value).TakeWhile(index => index <= limit - 1).Last();
            return value.Substring(0, end) + "…";
        }

        internal static string Name(string value, string fallback)
        {
            value = Plain(value, fallback, 500);
            return StringInfo.ParseCombiningCharacters(value).Length > 60 ? new StringInfo(value).SubstringByTextElements(0, 60) : value;
        }

        internal static string Format(IEnumerable<Row> totals, bool causes, Text wording)
        {
            var defaults = new Text();
            string title = Plain(causes ? wording.CauseTitle : wording.PlayerTitle, causes ? defaults.CauseTitle : defaults.PlayerTitle);
            string omitted = Plain(causes ? wording.OtherCauses : wording.OtherPlayers, causes ? defaults.OtherCauses : defaults.OtherPlayers);
            string missing = Plain(wording.MissingName, defaults.MissingName);
            string unknown = Plain(wording.UnknownCause, defaults.UnknownCause);
            var rows = totals.Select(row => new Row { Key = row.Key, Count = row.Count,
                Name = causes && row.Key == "unknown" ? Name(unknown, defaults.UnknownCause) : Name(row.Name, causes ? unknown : missing) })
                .OrderByDescending(row => row.Count).ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal).ToList();
            string footer = rows.Count == 0 ? "" : "\n" + Plain(wording.TotalDeaths, defaults.TotalDeaths) + " " + rows.Sum(row => (long)row.Count);
            var text = new StringBuilder("```ansi\n" + title + "\n\n");
            if (rows.Count == 0) text.Append(Plain(wording.NoDeaths, defaults.NoDeaths));
            int rank = 0, previous = -1;
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (row.Count != previous) rank = index + 1;
                previous = row.Count;
                string line = $"{rank}. {row.Name}: {row.Count}\n";
                string remainder = omitted + " " + (rows.Count - index) + ".\n";
                // Reserve the footer, closing fence and an omitted-row count before accepting each complete row.
                int reserve = index + 1 < rows.Count ? remainder.Length : 0;
                if (text.Length + line.Length + reserve + footer.Length + 4 > 2000)
                { text.Append(remainder); break; }
                text.Append(line);
            }
            return text.Append(footer).Append("\n```").ToString();
        }

        internal static TimeZoneInfo ResolveZone(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "UTC" : name.Trim();
            try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
            catch (TimeZoneNotFoundException error)
            {
                string equivalent = name == "Europe/Warsaw" ? "Central European Standard Time" :
                    name == "Central European Standard Time" ? "Europe/Warsaw" : null;
                if (equivalent != null)
                    try { return TimeZoneInfo.FindSystemTimeZoneById(equivalent); }
                    catch (TimeZoneNotFoundException) { }
                throw new InvalidOperationException("Death leaderboard Report time zone is unavailable: " + name + ". Set a valid time zone and restart.", error);
            }
        }

        internal static DateTime LatestDeadline(DateTime utcNow, TimeZoneInfo zone, int hour)
        {
            if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour));
            DateTime date = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone).Date;
            DateTime End(DateTime day)
            {
                var local = DateTime.SpecifyKind(day.AddHours(hour), DateTimeKind.Unspecified);
                // A skipped hour runs at the first valid minute; a repeated hour runs at its first occurrence only.
                while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
                if (zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
                return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            }
            DateTime end = End(date);
            return end <= utcNow ? end : End(date.AddDays(-1));
        }
    }
}
