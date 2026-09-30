using AirSend.Core.Updates;

namespace AirSend.Core.Tests;

/// <summary>
/// The file swap is the step that failed in the wild: the batch helper of 0.2.1 could
/// not copy anything because the quoted destination path ended with a backslash. It
/// now runs inside AirSend itself, and these tests cover the behaviour that matters —
/// every file, missing directories, locked targets, a bounded wait for the old
/// process, and a report the next start can read.
/// </summary>
public class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"airsend-apply-{Guid.NewGuid():N}");

    public UpdateApplierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void CopiesEveryFileIncludingNewSubdirectories()
    {
        string source = Path.Combine(_root, "package");
        string target = Path.Combine(_root, "install");
        Write(Path.Combine(source, "AirSend.exe"), "new exe");
        Write(Path.Combine(source, "Assets", "AppIcon.ico"), "icon");
        Write(Path.Combine(target, "AirSend.exe"), "old exe");
        Write(Path.Combine(target, "stale.txt"), "kept");

        UpdateApplyResult result = UpdateApplier.Apply(new UpdateApplyRequest(source, target, "0.2.3"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.FilesCopied);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "AirSend.exe")));
        Assert.Equal("icon", File.ReadAllText(Path.Combine(target, "Assets", "AppIcon.ico")));

        // Nothing is deleted: the update only overwrites what the package carries.
        Assert.True(File.Exists(Path.Combine(target, "stale.txt")));

        string report = File.ReadAllText(Path.Combine(target, UpdateApplier.ReportFileName));
        Assert.Contains("result=ok", report, StringComparison.Ordinal);
        Assert.Contains("version=0.2.3", report, StringComparison.Ordinal);
        Assert.Contains("files=2", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAFailureWhenAFileCannotBeReplaced()
    {
        string source = Path.Combine(_root, "package");
        string target = Path.Combine(_root, "install");
        Write(Path.Combine(source, "AirSend.exe"), "new exe");
        Write(Path.Combine(target, "AirSend.exe"), "old exe");

        // Exactly what a still-running instance looks like: the file is locked.
        using (FileStream locked = File.Open(
                   Path.Combine(target, "AirSend.exe"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            UpdateApplyResult result = UpdateApplier.Apply(new UpdateApplyRequest(
                source,
                target,
                "0.2.3",
                RetryDelay: TimeSpan.Zero));

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
            Assert.Contains("AirSend.exe", result.Error!, StringComparison.Ordinal);
        }

        string report = File.ReadAllText(Path.Combine(target, UpdateApplier.ReportFileName));
        Assert.Contains("result=failed", report, StringComparison.Ordinal);
        Assert.Contains("error=", report, StringComparison.Ordinal);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "AirSend.exe")));
    }

    [Fact]
    public void WaitsForTheOldProcessBeforeCopying()
    {
        string source = Path.Combine(_root, "package");
        string target = Path.Combine(_root, "install");
        Write(Path.Combine(source, "AirSend.exe"), "new exe");

        using var blocker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping")
        {
            ArgumentList = { "-n", "30", "127.0.0.1" },
            CreateNoWindow = true,
        })!;

        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            blocker.Kill();
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        UpdateApplyResult result = UpdateApplier.Apply(new UpdateApplyRequest(
            source,
            target,
            "0.2.3",
            WaitForProcessId: blocker.Id,
            WaitTimeout: TimeSpan.FromSeconds(20)));
        clock.Stop();

        Assert.True(result.Succeeded, result.Error);
        Assert.True(clock.ElapsedMilliseconds >= 500, $"no esperó al proceso ({clock.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void GivesUpWaitingSoAStuckProcessCannotBlockTheUpdate()
    {
        string source = Path.Combine(_root, "package");
        string target = Path.Combine(_root, "install");
        Write(Path.Combine(source, "AirSend.exe"), "new exe");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        UpdateApplyResult result = UpdateApplier.Apply(new UpdateApplyRequest(
            source,
            target,
            "0.2.3",
            WaitForProcessId: Environment.ProcessId,   // this test process: never exits
            WaitTimeout: TimeSpan.FromMilliseconds(300)));
        clock.Stop();

        Assert.True(result.Succeeded, result.Error);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 10_000);
    }

    [Fact]
    public void FailsClearlyWhenThePackageIsMissing()
    {
        UpdateApplyResult result = UpdateApplier.Apply(new UpdateApplyRequest(
            Path.Combine(_root, "no-such-package"),
            Path.Combine(_root, "install"),
            "0.2.3"));

        Assert.False(result.Succeeded);
        Assert.Contains("no-such-package", result.Error!, StringComparison.Ordinal);
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
