using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed record RaceProfileLearningResult(
    int TrackId,
    int PitSamplesAdded,
    IReadOnlyDictionary<int, int> TyreSamplesAdded,
    string ModelPath,
    string Summary);

public static class RaceProfileLearningService
{
    public static RaceProfileLearningResult Learn(string sessionFolder, string rootFolder)
    {
        var database = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(database)) throw new FileNotFoundException("session.sqlite not found", database);
        var trackId = ReadMetadataInt(database, "track_id");
        var sessionType = ReadMetadataInt(database, "session_type");
        var trackName = ReadMetadata(database, "track_name") ?? TrackNames.GetTrackName(trackId);
        var sessionUid = ReadMetadata(database, "session_uid") ?? Path.GetFileName(sessionFolder);
        var profileFolder = RaceEngineerProfileStore.EnsureDefaultProfiles(rootFolder);
        var path = RaceEngineerProfileStore.LearnedModelPath(profileFolder, trackId);
        if (sessionType is < 15 or > 17)
        {
            return new RaceProfileLearningResult(
                trackId,
                0,
                new Dictionary<int, int>(),
                path,
                "Race profile learning skipped: the selected session is not Race, Race 2 or Race 3.");
        }
        var driver = RaceReportDataService.LoadDrivers(sessionFolder).FirstOrDefault(x => x.IsPlayer)
                     ?? throw new InvalidDataException("Player laps are unavailable for race-profile learning.");
        var rows = RaceReportDataService.LoadRows(sessionFolder, driver.CarIndex);
        var model = RaceEngineerProfileStore.ReadLearnedModel(profileFolder, trackId) ?? new LearnedRaceModel
        {
            TrackId = trackId,
            TrackName = trackName
        };
        if (model.ProcessedSessionUids.Contains(sessionUid))
        {
            return new RaceProfileLearningResult(
                trackId,
                0,
                new Dictionary<int, int>(),
                path,
                "Race profile learning skipped: this session is already included in the track model.");
        }

        var tyreObservations = rows
            .Where(x => x.CleanLap && !x.PitThisLap)
            .Select(x => new
            {
                Compound = x.VisualCompoundEnd,
                Wear = new[] { x.TyreWearFlDelta, x.TyreWearFrDelta, x.TyreWearRlDelta, x.TyreWearRrDelta }.Max()
            })
            .Where(x => x.Compound > 0 && x.Wear is > 0.02 and < 10)
            .GroupBy(x => x.Compound)
            .ToDictionary(x => x.Key, x => x.Select(v => v.Wear).ToList());

        foreach (var pair in tyreObservations)
        {
            var current = model.Tyres.GetValueOrDefault(pair.Key) ?? new LearnedTyreModel();
            var observationMean = pair.Value.Average();
            var combinedSamples = current.Samples + pair.Value.Count;
            current.WearMeanPctPerLap = combinedSamples == 0
                ? 0
                : (current.WearMeanPctPerLap * current.Samples + observationMean * pair.Value.Count) / combinedSamples;
            current.Samples = combinedSamples;
            model.Tyres[pair.Key] = current;
        }

        var baselinePace = rows
            .Where(x => x.CleanLap && !x.PitThisLap && x.LapTimeMs > 30_000)
            .Select(x => x.LapTimeMs)
            .OrderBy(x => x)
            .ToList();
        var baseline = baselinePace.Count == 0 ? 0 : Median(baselinePace);
        var pitLosses = rows
            .Where(x => x.PitThisLap && x.LapTimeMs > baseline && baseline > 0)
            .Select(x => (x.LapTimeMs - baseline) / 1000d)
            .Where(x => x is > 5 and < 60)
            .ToList();
        if (pitLosses.Count > 0)
        {
            var observationMean = pitLosses.Average();
            var combinedSamples = model.PitSamples + pitLosses.Count;
            model.PitLossMeanSeconds =
                (model.PitLossMeanSeconds * model.PitSamples + observationMean * pitLosses.Count) / combinedSamples;
            model.PitSamples = combinedSamples;
        }

        model.TrackName = trackName;
        model.ProcessedSessionUids.Add(sessionUid);
        model.UpdatedAt = DateTimeOffset.Now;
        RaceEngineerProfileStore.WriteLearnedModel(profileFolder, model);
        SaveObservations(database, trackId, tyreObservations, pitLosses);
        var summary = $"Race profile learned: pit samples +{pitLosses.Count}, tyre samples +{tyreObservations.Sum(x => x.Value.Count)}.";
        return new RaceProfileLearningResult(
            trackId,
            pitLosses.Count,
            tyreObservations.ToDictionary(x => x.Key, x => x.Value.Count),
            path,
            summary);
    }

    private static void SaveObservations(
        string database,
        int trackId,
        IReadOnlyDictionary<int, List<double>> tyres,
        IReadOnlyList<double> pitLosses)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Cache=Private;Default Timeout=30");
        connection.Open();
        DatabaseSchemaMigrator.Apply(connection);
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO race_learning_observations(
                learned_at,track_id,metric,visual_compound,sample_count,observed_value,unit)
            VALUES($at,$track,$metric,$compound,$samples,$value,$unit)
            """;
        foreach (var name in new[] { "$at", "$track", "$metric", "$compound", "$samples", "$value", "$unit" })
            command.Parameters.AddWithValue(name, 0);
        foreach (var pair in tyres)
        {
            command.Parameters["$at"].Value = DateTimeOffset.Now.ToString("O");
            command.Parameters["$track"].Value = trackId;
            command.Parameters["$metric"].Value = "tyre_wear";
            command.Parameters["$compound"].Value = pair.Key;
            command.Parameters["$samples"].Value = pair.Value.Count;
            command.Parameters["$value"].Value = pair.Value.Average();
            command.Parameters["$unit"].Value = "pct_per_lap";
            command.ExecuteNonQuery();
        }
        if (pitLosses.Count > 0)
        {
            command.Parameters["$at"].Value = DateTimeOffset.Now.ToString("O");
            command.Parameters["$track"].Value = trackId;
            command.Parameters["$metric"].Value = "pit_loss";
            command.Parameters["$compound"].Value = DBNull.Value;
            command.Parameters["$samples"].Value = pitLosses.Count;
            command.Parameters["$value"].Value = pitLosses.Average();
            command.Parameters["$unit"].Value = "seconds";
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string? ReadMetadata(string database, string key)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Private");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM session_metadata WHERE key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int ReadMetadataInt(string database, string key) =>
        int.TryParse(ReadMetadata(database, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;
    }
}
