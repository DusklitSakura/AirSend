using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using AirSend.Core.Updates;

namespace AirSend.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v0.2.1", "0.2.0", true)]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("V0.2.1", "0.2.0", true)]
    [InlineData("v0.10.0", "0.9.9", true)]
    [InlineData("v1.0.0", "0.99.99", true)]
    [InlineData("v0.2.0", "0.2.0", false)]
    [InlineData("v0.1.9", "0.2.0", false)]
    [InlineData("v0.2.1-beta.1", "0.2.1", false)]
    [InlineData("v0.2.2-beta.1", "0.2.1", true)]
    [InlineData("v0.2", "0.2.0", false)]
    [InlineData("v0.2.1+3f2a1c", "0.2.1", false)]
    public void ComparesReleaseTags(string tag, string current, bool newer)
    {
        Assert.True(UpdateVersion.TryParse(tag, out UpdateVersion candidate), tag);
        Assert.True(UpdateVersion.TryParse(current, out UpdateVersion installed), current);

        Assert.Equal(newer, candidate.IsNewerThan(installed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.2.x")]
    public void RejectsTagsThatAreNotVersions(string tag)
    {
        Assert.False(UpdateVersion.TryParse(tag, out _));
    }

    [Fact]
    public void AReleaseOutranksItsOwnPreReleases()
    {
        Assert.True(UpdateVersion.TryParse("0.3.0", out UpdateVersion release));
        Assert.True(UpdateVersion.TryParse("0.3.0-rc.1", out UpdateVersion candidate));

        Assert.True(release.IsNewerThan(candidate));
        Assert.False(candidate.IsNewerThan(release));
    }
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverMeansNever()
    {
        Assert.False(UpdatePolicy.IsDue(UpdateCheckInterval.Never, null, Now, alreadyCheckedThisSession: false));
    }

    [Fact]
    public void StartupChecksOncePerSession()
    {
        Assert.True(UpdatePolicy.IsDue(UpdateCheckInterval.Startup, null, Now, alreadyCheckedThisSession: false));
        Assert.False(UpdatePolicy.IsDue(UpdateCheckInterval.Startup, null, Now, alreadyCheckedThisSession: true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, false)]
    [InlineData(25, true)]
    public void DailyChecksOnceADay(int? hoursSinceLastCheck, bool due)
    {
        DateTimeOffset? last = hoursSinceLastCheck is { } hours ? Now.AddHours(-hours) : null;

        Assert.Equal(due, UpdatePolicy.IsDue(UpdateCheckInterval.Daily, last, Now, alreadyCheckedThisSession: true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(3, false)]
    [InlineData(8, true)]
    public void WeeklyChecksOnceAWeek(int? daysSinceLastCheck, bool due)
    {
        DateTimeOffset? last = daysSinceLastCheck is { } days ? Now.AddDays(-days) : null;

        Assert.Equal(due, UpdatePolicy.IsDue(UpdateCheckInterval.Weekly, last, Now, alreadyCheckedThisSession: false));
    }

    [Theory]
    [InlineData(UpdateCheckInterval.Never, "never")]
    [InlineData(UpdateCheckInterval.Startup, "startup")]
    [InlineData(UpdateCheckInterval.Daily, "daily")]
    [InlineData(UpdateCheckInterval.Weekly, "weekly")]
    public void RoundTripsThroughTheSetting(UpdateCheckInterval interval, string stored)
    {
        Assert.Equal(stored, UpdatePolicy.ToSetting(interval));
        Assert.Equal(interval, UpdatePolicy.Parse(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sometimes")]
    public void UnknownSettingsFallBackToStartup(string? stored)
    {
        Assert.Equal(UpdateCheckInterval.Startup, UpdatePolicy.Parse(stored));
    }
}

public class UpdateAssetSelectionTests
{
    private static readonly string[] AllPackageNames =
    [
        "AirSend-0.2.1-win-x64-selfcontained.zip",
        "AirSend-0.2.1-win-x64-frameworkdependent.zip",
        "AirSend-0.2.1-win-x86-selfcontained.zip",
        "AirSend-0.2.1-win-x86-frameworkdependent.zip",
        "AirSend-0.2.1-win-arm64-selfcontained.zip",
        "AirSend-0.2.1-win-arm64-frameworkdependent.zip",
    ];

    [Theory]
    [InlineData("win-x64", true, "AirSend-0.2.1-win-x64-selfcontained.zip")]
    [InlineData("win-x64", false, "AirSend-0.2.1-win-x64-frameworkdependent.zip")]
    [InlineData("win-x86", true, "AirSend-0.2.1-win-x86-selfcontained.zip")]
    [InlineData("win-x86", false, "AirSend-0.2.1-win-x86-frameworkdependent.zip")]
    [InlineData("win-arm64", true, "AirSend-0.2.1-win-arm64-selfcontained.zip")]
    [InlineData("win-arm64", false, "AirSend-0.2.1-win-arm64-frameworkdependent.zip")]
    public void PicksThePackageForThisExactBuild(string runtime, bool selfContained, string expected)
    {
        UpdateAsset[] assets = [.. AllPackageNames.Select(Name)];

        UpdateAsset? picked = UpdateAssets.Select(assets, new InstalledBuild(runtime, selfContained, "0.2.1"), Version("0.2.1"));

        Assert.NotNull(picked);
        Assert.Equal(expected, picked.Name);
    }

    [Fact]
    public void RefusesAPackageBuiltForAnotherVersion()
    {
        // The v0.2.1 release containing 0.2.0 packages (a mis-tagged build) must lead
        // to "download it yourself", never to installing the wrong binaries.
        UpdateAsset[] assets = [Name("AirSend-0.2.0-win-x64-selfcontained.zip")];

        Assert.Null(UpdateAssets.Select(assets, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1")));
    }

    [Fact]
    public void RefusesAnotherArchitectureOrFlavour()
    {
        UpdateAsset[] assets =
        [
            Name("AirSend-0.2.1-win-arm64-selfcontained.zip"),
            Name("AirSend-0.2.1-win-x64-frameworkdependent.zip"),
        ];

        Assert.Null(UpdateAssets.Select(assets, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1")));
    }

    [Fact]
    public void IgnoresAssetsWithoutADownloadUrl()
    {
        UpdateAsset[] assets = [new("AirSend-0.2.1-win-x64-selfcontained.zip", string.Empty, 0)];

        Assert.Null(UpdateAssets.Select(assets, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1")));
    }

    private static UpdateAsset Name(string name) => new(name, $"https://example.invalid/{name}", 1024);

    private static UpdateVersion Version(string text) =>
        UpdateVersion.TryParse(text, out UpdateVersion version) ? version : default;
}

public class InstalledBuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"airsend-build-{Guid.NewGuid():N}");

    public InstalledBuildTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void ReadsTheIdentityTheBuildWrote()
    {
        File.WriteAllText(
            Path.Combine(_directory, InstalledBuild.FileName),
            InstalledBuild.Format("win-arm64", selfContained: false, version: "0.2.1"));

        InstalledBuild build = InstalledBuild.Detect(_directory);

        Assert.Equal("win-arm64", build.RuntimeIdentifier);
        Assert.False(build.SelfContained);
        Assert.Equal("0.2.1", build.Version);
        Assert.Equal("frameworkdependent", build.Flavor);
        Assert.Equal("-win-arm64-frameworkdependent.zip", build.PackageSuffix);
    }

    [Fact]
    public void FallsBackToTheObservableLayoutWithoutBuildInfo()
    {
        InstalledBuild build = InstalledBuild.Detect(_directory);

        Assert.Equal(InstalledBuild.RuntimeIdentifierFor(RuntimeInformation.ProcessArchitecture), build.RuntimeIdentifier);
        Assert.False(build.SelfContained);
        Assert.Null(build.Version);

        File.WriteAllBytes(Path.Combine(_directory, "coreclr.dll"), [0x4d, 0x5a]);

        InstalledBuild selfContained = InstalledBuild.Detect(_directory);
        Assert.True(selfContained.SelfContained);
        Assert.Equal("selfcontained", selfContained.Flavor);
    }

    [Fact]
    public void AcceptsAPackageThatMatches()
    {
        WritePackage("win-x64", selfContained: true, version: "0.2.1");

        UpdateAssets.EnsurePackageMatches(_directory, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1"));
    }

    [Theory]
    [InlineData("win-x86", true, "0.2.1")]
    [InlineData("win-x64", false, "0.2.1")]
    [InlineData("win-x64", true, "0.2.0")]
    public void RejectsAPackageThatDoesNotMatch(string runtime, bool selfContained, string version)
    {
        WritePackage(runtime, selfContained, version);

        UpdateException error = Assert.Throws<UpdateException>(() =>
            UpdateAssets.EnsurePackageMatches(_directory, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1")));

        Assert.Equal(UpdateFailure.PackageMismatch, error.Reason);
    }

    [Fact]
    public void RejectsAPackageWithoutTheExecutable()
    {
        File.WriteAllText(
            Path.Combine(_directory, InstalledBuild.FileName),
            InstalledBuild.Format("win-x64", selfContained: true, version: "0.2.1"));

        UpdateException error = Assert.Throws<UpdateException>(() =>
            UpdateAssets.EnsurePackageMatches(_directory, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.1")));

        Assert.Equal(UpdateFailure.PackageMismatch, error.Reason);
    }

    /// <summary>
    /// Packages published before build-info.txt existed cannot be identified from
    /// metadata, so the files themselves have to give it away: a bundled runtime for
    /// the flavour and the PE header for the architecture.
    /// </summary>
    [Fact]
    public void VerifiesLegacyPackagesByTheirLayout()
    {
        WriteLegacyPackage(machine: 0x8664, selfContained: false);
        var installed = new InstalledBuild("win-x64", false, "0.2.0");

        UpdateAssets.EnsurePackageMatches(_directory, installed, Version("0.2.0"));

        Assert.Throws<UpdateException>(() => UpdateAssets.EnsurePackageMatches(
            _directory, installed with { SelfContained = true }, Version("0.2.0")));
        Assert.Throws<UpdateException>(() => UpdateAssets.EnsurePackageMatches(
            _directory, installed with { RuntimeIdentifier = "win-arm64" }, Version("0.2.0")));
    }

    [Fact]
    public void AcceptsALegacySelfContainedPackage()
    {
        WriteLegacyPackage(machine: 0x8664, selfContained: true);

        UpdateAssets.EnsurePackageMatches(_directory, new InstalledBuild("win-x64", true, "0.2.0"), Version("0.2.0"));
    }

    [Fact]
    public void RecognisesTheArchitectureRecordedInTheExecutable()
    {
        string path = Path.Combine(_directory, "AirSend.exe");

        foreach ((ushort machine, Architecture expected) in new[]
                 {
                     ((ushort)0x014c, Architecture.X86),
                     ((ushort)0x8664, Architecture.X64),
                     ((ushort)0xaa64, Architecture.Arm64),
                 })
        {
            File.WriteAllBytes(path, PortableExecutable(machine));
            Assert.Equal(expected, UpdateAssets.MachineOf(path));
        }

        File.WriteAllBytes(path, "not a portable executable"u8.ToArray());
        Assert.Null(UpdateAssets.MachineOf(path));
    }

    private void WriteLegacyPackage(ushort machine, bool selfContained)
    {
        File.WriteAllBytes(Path.Combine(_directory, "AirSend.exe"), PortableExecutable(machine));

        if (selfContained)
        {
            File.WriteAllBytes(Path.Combine(_directory, "coreclr.dll"), [0x4d, 0x5a]);
        }
    }

    private static byte[] PortableExecutable(ushort machine)
    {
        byte[] image = new byte[512];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c), 0x80);
        image[0x80] = (byte)'P';
        image[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), machine);
        return image;
    }

    private void WritePackage(string runtime, bool selfContained, string version)
    {
        File.WriteAllText(
            Path.Combine(_directory, InstalledBuild.FileName),
            InstalledBuild.Format(runtime, selfContained, version));
        File.WriteAllBytes(Path.Combine(_directory, "AirSend.exe"), [0x4d, 0x5a]);
    }

    private static UpdateVersion Version(string text) =>
        UpdateVersion.TryParse(text, out UpdateVersion version) ? version : default;
}

public class UpdateNotesTests
{
    [Fact]
    public void StripsTheMarkdownTheDialogCannotRender()
    {
        string plain = UpdateNotes.ToPlainText(
            "# AirSend 0.2.1\n\n**能做什么**\n\n- 自动发现 `HomePod`\n- 见 [发布页](https://example.invalid)\n");

        Assert.DoesNotContain("#", plain);
        Assert.DoesNotContain("**", plain);
        Assert.DoesNotContain("`", plain);
        Assert.DoesNotContain("]", plain);
        Assert.Contains("• 自动发现", plain);
        Assert.Contains("见 发布页", plain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyNotesStayEmpty(string? markdown)
    {
        Assert.Equal(string.Empty, UpdateNotes.ToPlainText(markdown));
    }

    [Fact]
    public void TurnsTablesIntoReadableLines()
    {
        string plain = UpdateNotes.ToPlainText(
            "| 包 | 适合 |\n|---|---|\n| `AirSend-0.2.1-win-x64-selfcontained.zip` | 普通电脑 |\n");

        Assert.DoesNotContain("|", plain);
        Assert.DoesNotContain("---", plain);
        Assert.Contains("包 — 适合", plain);
        Assert.Contains("AirSend-0.2.1-win-x64-selfcontained.zip — 普通电脑", plain);
    }
}

public class UpdateDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"airsend-download-{Guid.NewGuid():N}");

    public UpdateDownloadTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are not worth failing a test run over.
        }
    }

    [Fact]
    public async Task ResumesWhereTheConnectionWasCut()
    {
        byte[] payload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index % 251))];
        var handler = new DroppingHandler(payload, dropAfter: 1024);
        using var http = new HttpClient(handler);
        using var client = new UpdateClient("owner/repo", http) { RetryDelay = TimeSpan.Zero };
        string path = Path.Combine(_directory, "package.zip");
        var progress = new Collector();

        await client.DownloadAsync(
            new UpdateAsset("AirSend-0.2.1-win-x64-selfcontained.zip", "https://example.invalid/package.zip", payload.Length),
            path,
            progress);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0]);
        Assert.Equal(1024, handler.Requests[1]);
        Assert.Equal(1d, progress.Values[^1]);
    }

    [Fact]
    public async Task RestartsWhenTheServerIgnoresTheRange()
    {
        byte[] payload = [.. Enumerable.Range(0, 2048).Select(index => (byte)(index % 97))];
        var handler = new DroppingHandler(payload, dropAfter: 512, honourRange: false);
        using var http = new HttpClient(handler);
        using var client = new UpdateClient("owner/repo", http) { RetryDelay = TimeSpan.Zero };
        string path = Path.Combine(_directory, "package.zip");

        await client.DownloadAsync(
            new UpdateAsset("AirSend-0.2.1-win-x64-selfcontained.zip", "https://example.invalid/package.zip", payload.Length),
            path);

        // The second response was a full 200, so the partial file must not be kept.
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GivesUpAfterTheLastAttempt()
    {
        byte[] payload = new byte[1024];
        var handler = new DroppingHandler(payload, dropAfter: 128, alwaysDrop: true);
        using var http = new HttpClient(handler);
        using var client = new UpdateClient("owner/repo", http) { RetryDelay = TimeSpan.Zero };

        // The failure carries a catalog key, so the dialog can explain it in the
        // language the interface is using.
        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(
            new UpdateAsset("AirSend-0.2.1-win-x64-selfcontained.zip", "https://example.invalid/package.zip", payload.Length),
            Path.Combine(_directory, "package.zip")));

        Assert.Equal(UpdateFailure.Download, error.Reason);
        Assert.Equal(4, handler.Requests.Count);
    }

    private sealed class Collector : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class DroppingHandler(byte[] payload, int dropAfter, bool honourRange = true, bool alwaysDrop = false)
        : HttpMessageHandler
    {
        public List<long?> Requests { get; } = [];

        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            Requests.Add(from);

            // The first attempt always breaks mid-transfer; later ones either honour
            // the range request or (to model a server that ignores it) start over.
            bool drop = alwaysDrop || _calls++ == 0;
            bool serveRest = honourRange && from is { } offset && offset > 0;

            if (drop)
            {
                var full = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DroppingStream(payload, dropAfter)),
                };
                full.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(full);
            }

            if (!serveRest)
            {
                var whole = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(payload, writable: false)),
                };
                whole.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(whole);
            }

            long start = from!.Value;
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new MemoryStream(payload[(int)start..], writable: false)),
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, payload.Length - 1, payload.Length);
            partial.Content.Headers.ContentLength = payload.Length - start;
            return Task.FromResult(partial);
        }
    }

    /// <summary>Reads the first bytes and then fails the way a reset connection does.</summary>
    private sealed class DroppingStream(byte[] payload, int dropAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= dropAfter)
            {
                throw new IOException("se cortó la conexión");
            }

            int take = Math.Min(buffer.Length, dropAfter - _position);
            payload.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
