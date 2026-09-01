using F1TelemetryLab;
using System.IO.Compression;
using System.Xml.Linq;

namespace F1TelemetryLab.Tests;

public sealed class RaceSummaryWorkbookTests
{
    [Fact]
    public void ExportCreatesSmallValidOpenXmlWorkbookWithoutCsvSidecars()
    {
        SQLitePCL.Batteries_V2.Init();
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-xlsx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using (var database = new TelemetryDatabase(Path.Combine(folder, "session.sqlite")))
            {
                database.SaveMetadata(new SessionMetadata
                {
                    SessionName = "Workbook Test",
                    TrackName = "China",
                    TrackId = 2,
                    SessionType = 15,
                    SessionFolder = folder,
                    DatabasePath = Path.Combine(folder, "session.sqlite")
                });
            }

            var path = RaceSummaryWorkbookExporter.Export(folder);

            Assert.True(File.Exists(path));
            using var archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
            Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
            Assert.NotNull(archive.GetEntry("xl/styles.xml"));
            Assert.Equal(5, archive.Entries.Count(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));
            foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                _ = XDocument.Load(stream);
            }
            Assert.Empty(Directory.EnumerateFiles(folder, "*.csv", SearchOption.AllDirectories));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
