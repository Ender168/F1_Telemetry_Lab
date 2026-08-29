using Microsoft.Data.Sqlite;
using System.IO.Compression;

namespace F1TelemetryLab.Tests;

public sealed class AnalysisPackTests
{
    [Fact]
    public void AnalysisPackContainsFullDatabaseCompactDatabaseAndJsonSidecars()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_pack_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var database = Path.Combine(root, "session.sqlite");
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE session_metadata(key TEXT PRIMARY KEY, value TEXT);
                    INSERT INTO session_metadata(key, value) VALUES
                        ('session_name', 'Pack Test'),
                        ('track_name', 'Mexico'),
                        ('track_id', '19'),
                        ('track_length_m', '4304'),
                        ('session_type', '10'),
                        ('total_laps', '36');
                    CREATE TABLE raw_packets(id INTEGER PRIMARY KEY, marker TEXT);
                    INSERT INTO raw_packets(marker) VALUES ('raw-data-survives');
                    """;
                command.ExecuteNonQuery();
            }

            var zip = SessionPackager.CreateZip(root, database, "Pack Test");
            Assert.True(File.Exists(zip));

            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.ToDictionary(
                entry => entry.FullName.Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);

            Assert.Contains("Pack Test/session.sqlite", entries.Keys);
            Assert.Contains("Pack Test/chatgpt_pack.sqlite", entries.Keys);
            Assert.Contains("Pack Test/session_summary.json", entries.Keys);
            Assert.Contains("Pack Test/setup.json", entries.Keys);
            Assert.Contains("Pack Test/events.json", entries.Keys);
            Assert.Contains("Pack Test/data_quality.json", entries.Keys);
            Assert.Contains("Pack Test/track.json", entries.Keys);
            Assert.Contains("Pack Test/PACK_CONTENTS.txt", entries.Keys);

            var extracted = Path.Combine(root, "extracted.sqlite");
            entries["Pack Test/session.sqlite"].ExtractToFile(extracted, overwrite: true);
            using var snapshot = new SqliteConnection($"Data Source={extracted};Mode=ReadOnly");
            snapshot.Open();
            using var markerCommand = snapshot.CreateCommand();
            markerCommand.CommandText = "SELECT marker FROM raw_packets LIMIT 1";
            Assert.Equal("raw-data-survives", Convert.ToString(markerCommand.ExecuteScalar()));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
