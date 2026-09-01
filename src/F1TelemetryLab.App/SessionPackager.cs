using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace F1TelemetryLab;

public sealed record RarProcessResult(int ExitCode, string Output, string Error);

public interface IRarProcessRunner
{
    RarProcessResult Run(string executablePath, string workingDirectory, IReadOnlyList<string> arguments);
}

public sealed class WinRarProcessRunner : IRarProcessRunner
{
    public RarProcessResult Run(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("WinRAR could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new RarProcessResult(process.ExitCode, output, error);
    }
}

public static class SessionPackager
{
    public static string CreateRar(
        string sessionFolder,
        string databasePath,
        string? preferredSessionName = null,
        string? configuredWinRarPath = null,
        IRarProcessRunner? processRunner = null)
    {
        if (!Directory.Exists(sessionFolder)) throw new DirectoryNotFoundException($"Session folder not found: {sessionFolder}");
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("session.sqlite was not created. The RAR archive would be useless.", databasePath);

        SessionManifestService.Refresh(sessionFolder);
        var physical = Path.GetFileName(sessionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var baseName = SafeFileName(string.IsNullOrWhiteSpace(preferredSessionName) ? physical : preferredSessionName!);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = physical;
        var archivePath = Path.Combine(sessionFolder, baseName + ".rar");
        TryDelete(archivePath);

        var runner = processRunner ?? new WinRarProcessRunner();
        var executable = ResolveWinRar(configuredWinRarPath, processRunner is not null);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_rar_" + Guid.NewGuid().ToString("N"));
        var snapshotPath = Path.Combine(stagingRoot, "session.sqlite");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            CreateDatabaseSnapshot(databasePath, snapshotPath);
            CompactAndVerifySnapshot(snapshotPath);

            var add = BuildAddArguments(archivePath);
            var created = runner.Run(executable, stagingRoot, add);
            if (created.ExitCode != 0)
                throw new InvalidOperationException($"WinRAR archive creation failed with exit code {created.ExitCode}: {CleanError(created)}");
            if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
                throw new InvalidDataException("WinRAR reported success but did not create a non-empty archive.");

            var tested = runner.Run(executable, stagingRoot, BuildTestArguments(archivePath));
            if (tested.ExitCode != 0)
            {
                TryDelete(archivePath);
                throw new InvalidDataException($"WinRAR archive test failed with exit code {tested.ExitCode}: {CleanError(tested)}");
            }

            SessionManifestService.Refresh(sessionFolder, archivePath: archivePath);
            return archivePath;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch { }
        }
    }

    public static IReadOnlyList<string> BuildAddArguments(string archivePath) => new[]
    {
        "a",
        "-ma5",
        "-m5",
        "-md128m",
        "-ep",
        "-o+",
        "-idq",
        "-y",
        archivePath,
        "session.sqlite"
    };

    public static IReadOnlyList<string> BuildTestArguments(string archivePath) => new[]
    {
        "t",
        "-idq",
        "-y",
        archivePath
    };

    public static string? FindWinRar(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "WinRAR", "WinRAR.exe"));
        if (!string.IsNullOrWhiteSpace(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, "WinRAR", "WinRAR.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "WinRAR.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveWinRar(string? configuredPath, bool runnerIsInjected)
    {
        if (runnerIsInjected) return string.IsNullOrWhiteSpace(configuredPath) ? "WinRAR.exe" : configuredPath;
        return FindWinRar(configuredPath)
               ?? throw new FileNotFoundException("WinRAR.exe was not found. Install WinRAR or specify its path in Settings. ZIP fallback is intentionally disabled.");
    }

    private static void CreateDatabaseSnapshot(string sourcePath, string destinationPath)
    {
        TryDelete(destinationPath);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 60
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 60
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void CompactAndVerifySnapshot(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 60
        }.ToString());
        connection.Open();
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA optimize;";
            journal.ExecuteNonQuery();
        }
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        var result = Convert.ToString(integrity.ExecuteScalar());
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite snapshot failed integrity_check: " + result);
    }

    private static string CleanError(RarProcessResult value)
    {
        var text = string.IsNullOrWhiteSpace(value.Error) ? value.Output : value.Error;
        text = text.Trim();
        return text.Length <= 600 ? text : text[..600];
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
