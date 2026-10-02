using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using F1TelemetryLab;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab.Tests;

public sealed class RecordingDiscardTests
{
    [Fact]
    public async Task DiscardRemovesOnlyActiveFolderSkipsFinalizationAndAllowsRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "F1TL_discard_" + Guid.NewGuid().ToString("N"));
        var oldFolder = Path.Combine(root, "telemetry_packs", "previous-race");
        Directory.CreateDirectory(oldFolder);
        var keep = Path.Combine(oldFolder, "keep.txt"); File.WriteAllText(keep, "previous recording");
        var messages = new ConcurrentQueue<string>();
        await using var recorder = new UdpRecorder(); recorder.Log += messages.Enqueue;
        try
        {
            var port = FreePort(); recorder.Start(port, root);
            var folder = recorder.CurrentSession!.SessionFolder;
            Assert.True(File.Exists(recorder.CurrentSession.DatabasePath));
            using var sender = new UdpClient();
            // Even unsupported/invalid input must not leave raw files behind on discard.
            await sender.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, port));
            await WaitForPacket(recorder);
            var discard = recorder.DiscardAsync();
            var repeatedStop = recorder.StopAsync(createZip: true);
            Assert.Null(await discard);
            Assert.Null(await repeatedStop);
            Assert.False(Directory.Exists(folder));
            Assert.Equal("previous recording", File.ReadAllText(keep));
            Assert.Null(recorder.CurrentSession);
            Assert.False(recorder.IsActive);
            Assert.Empty(recorder.LiveCars);
            Assert.Equal("Recording discarded", recorder.Status);
            Assert.DoesNotContain(messages, m => m.Contains("Analyzing session") || m.Contains("RAR created") || m.Contains("profile learning skipped"));
            Assert.Empty(Directory.GetFiles(root, "*.rar", SearchOption.AllDirectories));
            recorder.Start(port, root); // UDP socket and database handles have both been released.
            var next = recorder.CurrentSession!.SessionFolder;
            Assert.NotEqual(folder, next);
            await recorder.DiscardAsync();
            Assert.False(Directory.Exists(next));
        }
        finally
        {
            if (recorder.IsActive) await recorder.DiscardAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DiscardUsesOwnedFolderNotMutableMetadataOrPreviousSavedSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "F1TL_discard_" + Guid.NewGuid().ToString("N"));
        await using var recorder = new UdpRecorder();
        try
        {
            recorder.Start(FreePort(), root);
            var owned = recorder.CurrentSession!.SessionFolder;
            var other = Path.Combine(root, "telemetry_packs", "other"); Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");
            recorder.CurrentSession.SessionFolder = other;
            await recorder.DiscardAsync();
            Assert.False(Directory.Exists(owned));
            Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
            Assert.Null(await recorder.DiscardAsync());
            Assert.True(Directory.Exists(other));

            recorder.Start(FreePort(), root);
            var saved = await recorder.StopAsync(createZip: false);
            Assert.NotNull(saved);
            Assert.True(File.Exists(saved.DatabasePath));
            Assert.Null(await recorder.DiscardAsync());
            Assert.True(File.Exists(saved.DatabasePath));
        }
        finally
        {
            if (recorder.IsActive) await recorder.DiscardAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReplacedRecordingFolderReportsFailureWithoutDeletingReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "F1TL_discard_failure_" + Guid.NewGuid().ToString("N"));
        await using var recorder = new UdpRecorder();
        string? folder = null;
        try
        {
            recorder.Start(FreePort(), root);
            folder = recorder.CurrentSession!.SessionFolder;
            // Linux permits renaming the open DB's directory; Windows tests a locked file instead.
            if (!OperatingSystem.IsWindows())
            {
                Directory.Move(folder, folder + "-moved");
                File.WriteAllText(folder, "replacement");
                await Assert.ThrowsAsync<IOException>(() => recorder.DiscardAsync());
                Assert.Equal("Stopped; deletion failed", recorder.Status);
                Assert.False(recorder.IsActive);
                Assert.Equal("replacement", File.ReadAllText(folder));
            }
            else
            {
                using var held = new FileStream(Path.Combine(folder, "held.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
                await Assert.ThrowsAnyAsync<IOException>(() => recorder.DiscardAsync());
                Assert.Equal("Stopped; deletion failed", recorder.Status);
                Assert.False(recorder.IsActive);
                Assert.True(File.Exists(Path.Combine(folder, "held.txt")));
            }
        }
        finally
        {
            if (recorder.IsActive) await recorder.DiscardAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DatabaseDiscardRollsBackPendingRowsAndReleasesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "F1TL_discard_db_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.sqlite");
        try
        {
            using (var database = new TelemetryDatabase(path))
            {
                database.InsertRaw(DateTimeOffset.UtcNow, null, new byte[] { 1, 2, 3 });
                database.Discard();
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM raw_packets";
                Assert.Equal(0L, command.ExecuteScalar());
            }
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private static int FreePort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitForPacket(UdpRecorder recorder)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (recorder.PacketsSeen == 0) await Task.Delay(10, timeout.Token);
    }
}
