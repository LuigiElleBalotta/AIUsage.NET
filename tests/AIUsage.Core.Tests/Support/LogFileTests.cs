using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class LogFileTests : IDisposable
{
    private readonly string _dir;

    public LogFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AIUsageLogFileTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Append_WritesLineToFile()
    {
        var log = new LogFile(_dir, "test.log");
        log.Append("hello world");
        log.Close();

        var path = Path.Combine(_dir, "test.log");
        Assert.True(File.Exists(path));
        Assert.Contains("hello world", File.ReadAllText(path));
    }

    [Fact]
    public void Append_ExceedingCap_RotatesToArchive()
    {
        // Tiny cap so a couple of lines trigger rotation.
        var log = new LogFile(_dir, "test.log", maxBytes: 20);
        log.Append("first line long enough to exceed cap");
        log.Append("second line");
        log.Close();

        var archivePath = Path.Combine(_dir, "test.1.log");
        var mainPath = Path.Combine(_dir, "test.log");
        Assert.True(File.Exists(archivePath));
        Assert.True(File.Exists(mainPath));
        Assert.Contains("first line", File.ReadAllText(archivePath));
        Assert.Contains("second line", File.ReadAllText(mainPath));
    }

    [Fact]
    public void Open_ExistingOversizeFile_RotatesOnLaunch()
    {
        Directory.CreateDirectory(_dir);
        var mainPath = Path.Combine(_dir, "test.log");
        File.WriteAllText(mainPath, new string('x', 100));

        var log = new LogFile(_dir, "test.log", maxBytes: 10);
        log.Open();
        log.Append("new line after rotation");
        log.Close();

        var archivePath = Path.Combine(_dir, "test.1.log");
        Assert.True(File.Exists(archivePath));
        Assert.Contains("new line after rotation", File.ReadAllText(mainPath));
    }

    [Fact]
    public void Append_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "nested", "logs");
        var log = new LogFile(nested, "app.log");
        log.Append("line");
        log.Close();

        Assert.True(File.Exists(Path.Combine(nested, "app.log")));
    }
}
