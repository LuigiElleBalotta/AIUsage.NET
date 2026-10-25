using AIUsage.Core.Providers.OpenCode;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeVersionProbeTests
{
    [Fact]
    public async Task ParsesV2Version()
    {
        var probe = new OpenCodeVersionProbe(_ => Task.FromResult<string?>("2.1.2\n"));

        var hint = await probe.GetAsync();

        Assert.Equal("2.1.2", hint.Version);
        Assert.Equal("2.1.2", hint.Display);
    }

    [Fact]
    public async Task ParsesV1VersionPrefix()
    {
        var probe = new OpenCodeVersionProbe(_ => Task.FromResult<string?>("v1.4.0"));

        var hint = await probe.GetAsync();

        Assert.Equal("1.4.0", hint.Version);
    }

    [Fact]
    public async Task MissingCli_ReturnsUnknown()
    {
        var probe = new OpenCodeVersionProbe(_ => Task.FromResult<string?>(null));

        var hint = await probe.GetAsync();

        Assert.Null(hint.Version);
        Assert.Equal("unknown", hint.Display);
    }

    [Fact]
    public async Task MalformedOutput_ReturnsUnknownButKeepsRawHint()
    {
        var probe = new OpenCodeVersionProbe(_ => Task.FromResult<string?>("not a version"));

        var hint = await probe.GetAsync();

        Assert.Null(hint.Version);
        Assert.Equal("not a version", hint.Raw);
    }

    [Fact]
    public async Task Timeout_ReturnsUnknownAndIsCached()
    {
        var calls = 0;
        var probe = new OpenCodeVersionProbe(_ =>
        {
            calls++;
            return Task.FromException<string?>(new TimeoutException());
        });

        var first = await probe.GetAsync();
        var second = await probe.GetAsync();

        Assert.True(first.TimedOut);
        Assert.True(second.TimedOut);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void KnownInstallLocations_IncludeOpencodeBin()
    {
        var home = Path.Combine(Path.GetTempPath(), "AIUsageOpenCodeVersion_" + Guid.NewGuid().ToString("N"));
        var expected = Path.Combine(home, ".opencode", "bin", "opencode.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            File.WriteAllText(expected, "");

            var resolved = OpenCodeVersionProbe.ResolveExecutable(
                home: home,
                localAppData: Path.Combine(home, "appdata"),
                pathValue: "",
                fileExists: File.Exists);

            Assert.Equal(expected, resolved);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProbeIsCachedForTheProbeLifetime()
    {
        var calls = 0;
        var probe = new OpenCodeVersionProbe(_ =>
        {
            calls++;
            return Task.FromResult<string?>("2.1.2");
        });

        await probe.GetAsync();
        await probe.GetAsync();
        await probe.GetAsync();

        Assert.Equal(1, calls);
    }
}
