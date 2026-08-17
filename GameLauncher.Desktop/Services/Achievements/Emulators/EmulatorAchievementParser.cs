using System.Globalization;
using System.Text.Json;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Achievements.Emulators;

/// <summary>
/// Reads the achievement files Steam emulators leave on disk.
/// </summary>
/// <remarks>
/// <para>
/// Pure: text in, entries out. No file system, no clock, no database. Every
/// format quirk worth knowing lives here where it can be tested against a
/// captured file, which matters because these formats are conventions rather
/// than specifications — there is no document to check, only what the writers
/// actually produce.
/// </para>
/// <para>
/// Three shapes are handled. Goldberg writes JSON keyed by API name. CODEX and
/// RUNE write INI, either a section per achievement or a flat list under one
/// heading; both dialects appear in the wild and neither announces which it is,
/// so the reader accepts both from the same file.
/// </para>
/// <para>
/// A file that cannot be parsed yields nothing rather than throwing. These are
/// files written by other programs, frequently mid-write when the watcher
/// notices them, and a half-flushed file is an ordinary event rather than an
/// error worth propagating.
/// </para>
/// </remarks>
public static class EmulatorAchievementParser
{
    /// <summary>Keys that hold an unlocked flag, whatever the writer calls it.</summary>
    private static readonly string[] EarnedKeys = ["earned", "achieved", "unlocked", "haveachieved"];

    /// <summary>Keys that hold an unlock timestamp.</summary>
    private static readonly string[] EarnedTimeKeys =
        ["earned_time", "unlocktime", "unlock_time", "haveachievedtime", "time"];

    /// <summary>Keys that hold progress so far.</summary>
    private static readonly string[] ProgressKeys = ["progress", "curprogress", "current"];

    /// <summary>Keys that hold what progress is measured against.</summary>
    private static readonly string[] TargetKeys = ["max_progress", "maxprogress", "max", "target"];

    /// <summary>Section names whose contents are statistics rather than achievements.</summary>
    private static readonly string[] StatSections = ["stats", "statsint", "statsfloat", "statistics"];

    /// <summary>Section names whose contents are a flat list of achievements.</summary>
    private static readonly string[] FlatAchievementSections =
        ["achievements", "steamachievements", "activeachievements"];

    /// <summary>
    /// Parses an achievement file.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="steamAppId">The application the file belongs to.</param>
    /// <param name="sourceKey">Which reader is asking, recorded on every entry.</param>
    /// <param name="sourcePath">Where the file came from, recorded on every entry.</param>
    /// <returns>What the file described, or an empty snapshot.</returns>
    /// <remarks>
    /// The format is inferred from the content rather than the file name, because
    /// at least one writer has shipped JSON in a file called <c>.ini</c>.
    /// </remarks>
    public static ExternalAchievementSnapshot Parse(
        string? content,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ExternalAchievementSnapshot.Empty;
        }

        var trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');

        var entries = trimmed.StartsWith('{')
            ? ParseJson(trimmed, steamAppId, sourceKey, sourcePath)
            : ParseIni(trimmed, steamAppId, sourceKey, sourcePath);

        return entries.Count == 0
            ? ExternalAchievementSnapshot.Empty
            : new ExternalAchievementSnapshot(steamAppId, entries);
    }

    /// <summary>
    /// Reads Goldberg's JSON form.
    /// </summary>
    /// <param name="content">The document.</param>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="sourceKey">Reader key.</param>
    /// <param name="sourcePath">File path.</param>
    /// <returns>The entries found.</returns>
    /// <remarks>
    /// The document is an object keyed by API name. Values are usually objects
    /// carrying <c>earned</c> and <c>earned_time</c>, but a bare
    /// <see langword="true"/> appears in older files and means earned with no
    /// time recorded.
    /// </remarks>
    private static List<ExternalAchievement> ParseJson(
        string content,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        var entries = new List<ExternalAchievement>();

        try
        {
            using var document = JsonDocument.Parse(content);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                var entry = property.Value.ValueKind switch
                {
                    JsonValueKind.Object => FromJsonObject(property.Name, property.Value),
                    JsonValueKind.True => Achievement(property.Name, unlocked: true, null),
                    JsonValueKind.False => Achievement(property.Name, unlocked: false, null),

                    // A bare number keyed by name is a statistic, which is how
                    // Goldberg's stats file is shaped.
                    JsonValueKind.Number when property.Value.TryGetDouble(out var value) =>
                        Statistic(property.Name, value),

                    _ => null
                };

                if (entry is not null)
                {
                    Stamp(entry, steamAppId, sourceKey, sourcePath);
                    entries.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            // Half-written, or not JSON after all. Nothing to report: the watcher
            // will look again when the writer finishes.
            return [];
        }

        return entries;
    }

    /// <summary>Reads one JSON achievement object.</summary>
    /// <param name="apiName">The achievement's API name.</param>
    /// <param name="element">Its value.</param>
    /// <returns>The entry.</returns>
    private static ExternalAchievement FromJsonObject(string apiName, JsonElement element)
    {
        var unlocked = false;
        DateTimeOffset? unlockedAt = null;
        double? progress = null;
        double? target = null;

        foreach (var field in element.EnumerateObject())
        {
            var key = field.Name.ToLowerInvariant();

            if (EarnedKeys.Contains(key))
            {
                unlocked = field.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Number => field.Value.TryGetDouble(out var flag) && flag > 0,
                    JsonValueKind.String => IsTruthy(field.Value.GetString()),
                    _ => unlocked
                };
            }
            else if (EarnedTimeKeys.Contains(key))
            {
                unlockedAt = ReadTimestamp(RawNumber(field.Value));
            }
            else if (ProgressKeys.Contains(key))
            {
                progress = RawNumber(field.Value);
            }
            else if (TargetKeys.Contains(key))
            {
                target = RawNumber(field.Value);
            }
        }

        return new ExternalAchievement
        {
            ApiName = apiName,
            Kind = ExternalAchievementKind.Achievement,
            IsUnlocked = unlocked,
            UnlockedAt = unlocked ? unlockedAt : null,
            CurrentValue = progress,
            TargetValue = target
        };
    }

    /// <summary>
    /// Reads the INI forms CODEX and RUNE write.
    /// </summary>
    /// <param name="content">The document.</param>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="sourceKey">Reader key.</param>
    /// <param name="sourcePath">File path.</param>
    /// <returns>The entries found.</returns>
    /// <remarks>
    /// A section named after an achievement holds its fields. A section named
    /// <c>[Achievements]</c> or <c>[Stats]</c> holds a flat list instead, where
    /// the key is the name and the value is a flag, a timestamp or a counter.
    /// Both are accepted from the same file because writers mix them.
    /// </remarks>
    private static List<ExternalAchievement> ParseIni(
        string content,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        var entries = new List<ExternalAchievement>();

        var section = string.Empty;
        ExternalAchievement? current = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush(entries, current, steamAppId, sourceKey, sourcePath);
                current = null;

                section = line[1..^1].Trim();

                // A per-achievement section: the heading is the API name.
                if (!IsStatSection(section) && !IsFlatSection(section))
                {
                    current = new ExternalAchievement
                    {
                        ApiName = section,
                        Kind = ExternalAchievementKind.Achievement
                    };
                }

                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (current is not null)
            {
                ApplyField(current, key, value);
                continue;
            }

            if (IsStatSection(section))
            {
                var stat = Statistic(key, ParseNumber(value) ?? 0);
                Stamp(stat, steamAppId, sourceKey, sourcePath);
                entries.Add(stat);
                continue;
            }

            if (IsFlatSection(section))
            {
                entries.Add(FromFlatEntry(key, value, steamAppId, sourceKey, sourcePath));
            }
        }

        Flush(entries, current, steamAppId, sourceKey, sourcePath);

        return entries;
    }

    /// <summary>
    /// Reads a flat <c>name = value</c> achievement line.
    /// </summary>
    /// <param name="apiName">The achievement's API name.</param>
    /// <param name="value">Its value.</param>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="sourceKey">Reader key.</param>
    /// <param name="sourcePath">File path.</param>
    /// <returns>The entry.</returns>
    /// <remarks>
    /// The value carries two meanings depending on the writer: a flag, or the
    /// unlock time itself. A number large enough to be a Unix timestamp is read
    /// as one, which also makes it unlocked — nothing writes a timestamp for an
    /// achievement that was never earned.
    /// </remarks>
    private static ExternalAchievement FromFlatEntry(
        string apiName,
        string value,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        var number = ParseNumber(value);
        var timestamp = ReadTimestamp(number);

        var entry = new ExternalAchievement
        {
            ApiName = apiName,
            Kind = ExternalAchievementKind.Achievement,
            IsUnlocked = timestamp is not null || IsTruthy(value) || number > 0,
            UnlockedAt = timestamp
        };

        Stamp(entry, steamAppId, sourceKey, sourcePath);

        return entry;
    }

    /// <summary>Applies one <c>key = value</c> line to the achievement being built.</summary>
    /// <param name="entry">The achievement.</param>
    /// <param name="key">Field name.</param>
    /// <param name="value">Field value.</param>
    private static void ApplyField(ExternalAchievement entry, string key, string value)
    {
        var name = key.ToLowerInvariant();

        if (EarnedKeys.Contains(name))
        {
            entry.IsUnlocked = IsTruthy(value);
        }
        else if (EarnedTimeKeys.Contains(name))
        {
            entry.UnlockedAt = ReadTimestamp(ParseNumber(value));
        }
        else if (ProgressKeys.Contains(name))
        {
            entry.CurrentValue = ParseNumber(value);
        }
        else if (TargetKeys.Contains(name))
        {
            entry.TargetValue = ParseNumber(value);
        }
    }

    /// <summary>Adds a completed achievement section to the list.</summary>
    /// <param name="entries">The list being built.</param>
    /// <param name="entry">The achievement, or <see langword="null"/>.</param>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="sourceKey">Reader key.</param>
    /// <param name="sourcePath">File path.</param>
    private static void Flush(
        List<ExternalAchievement> entries,
        ExternalAchievement? entry,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ApiName))
        {
            return;
        }

        // A locked achievement must not carry a time, whatever the file said.
        if (!entry.IsUnlocked)
        {
            entry.UnlockedAt = null;
        }

        Stamp(entry, steamAppId, sourceKey, sourcePath);
        entries.Add(entry);
    }

    /// <summary>Records where an entry came from.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="sourceKey">Reader key.</param>
    /// <param name="sourcePath">File path.</param>
    private static void Stamp(
        ExternalAchievement entry,
        int steamAppId,
        string sourceKey,
        string sourcePath)
    {
        entry.SteamAppId = steamAppId;
        entry.SourceKey = sourceKey;
        entry.SourcePath = sourcePath;
    }

    /// <summary>Builds an achievement entry.</summary>
    /// <param name="apiName">Its API name.</param>
    /// <param name="unlocked">Whether it is earned.</param>
    /// <param name="unlockedAt">When, if known.</param>
    /// <returns>The entry.</returns>
    private static ExternalAchievement Achievement(string apiName, bool unlocked, DateTimeOffset? unlockedAt) =>
        new()
        {
            ApiName = apiName,
            Kind = ExternalAchievementKind.Achievement,
            IsUnlocked = unlocked,
            UnlockedAt = unlocked ? unlockedAt : null
        };

    /// <summary>Builds a statistic entry.</summary>
    /// <param name="apiName">Its name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The entry.</returns>
    private static ExternalAchievement Statistic(string apiName, double value) =>
        new()
        {
            ApiName = apiName,
            Kind = ExternalAchievementKind.Statistic,
            IsUnlocked = false,
            CurrentValue = value
        };

    /// <summary>Reads a JSON value as a number, whatever type it arrived as.</summary>
    /// <param name="element">The value.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    private static double? RawNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetDouble(out var value) => value,
        JsonValueKind.String => ParseNumber(element.GetString()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null
    };

    /// <summary>Parses a number the way a file wrote it.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Invariant culture: these files are written by programs, and a machine
    /// whose locale uses a comma for the decimal point must not read
    /// <c>1.5</c> as fifteen.
    /// </remarks>
    private static double? ParseNumber(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Decides whether a text value means yes.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns a number into an unlock time, if it is one.
    /// </summary>
    /// <param name="value">The number the file held.</param>
    /// <returns>The time, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Zero is the value these writers use for "never", so it is rejected rather
    /// than read as 1970. The upper bound rejects a value that is really a
    /// counter — a stat of four billion is a stat, not a date in the year 2106.
    /// </remarks>
    private static DateTimeOffset? ReadTimestamp(double? value)
    {
        if (value is not { } seconds || seconds <= 0)
        {
            return null;
        }

        // Roughly 2001 to 2100: before the first is not a real unlock, after it
        // is not a timestamp.
        if (seconds is < 978_307_200 or > 4_102_444_800)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime();
    }

    /// <summary>Determines whether a section holds statistics.</summary>
    /// <param name="section">The section name.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool IsStatSection(string section) =>
        StatSections.Contains(section.Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Determines whether a section holds a flat achievement list.</summary>
    /// <param name="section">The section name.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool IsFlatSection(string section) =>
        FlatAchievementSections.Contains(section.Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparer.OrdinalIgnoreCase);
}
