using System.Text.RegularExpressions;
using AirSend.Core;
using AirSend.Core.Capture;
using AirSend.Core.Logging;
using AirSend.Core.Localization;
using AirSend.Core.Pairing;
using AirSend.Core.Probe;
using AirSend.Core.Streaming;
using AirSend.Core.Updates;

namespace AirSend.Core.Tests;

/// <summary>
/// The log used to be Spanish whatever the interface language was, because the
/// messages were ported verbatim from the upstream Rust build. These tests keep the
/// catalog complete and keep the code from falling back to untranslated literals.
/// </summary>
public class LogLocalizationTests
{
    [Fact]
    public void FollowsTheConfiguredLanguage()
    {
        string previous = AppLog.Language;
        try
        {
            var parameters = new { dropped = 3, err = "boom" };

            AppLog.Language = "en";
            string english = AppLog.Text("log.rtp.send_failed", parameters);
            AppLog.Language = "zh";
            string chinese = AppLog.Text("log.rtp.send_failed", parameters);
            AppLog.Language = "es";
            string spanish = AppLog.Text("log.rtp.send_failed", parameters);

            Assert.Contains("RTP send failed", english, StringComparison.Ordinal);
            Assert.Contains("3", english, StringComparison.Ordinal);
            Assert.Contains("boom", english, StringComparison.Ordinal);
            Assert.Contains("RTP 发送失败", chinese, StringComparison.Ordinal);
            Assert.Contains("descartados", spanish, StringComparison.Ordinal);
        }
        finally
        {
            AppLog.Language = previous;
        }
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    [InlineData("es")]
    public void EveryEntryIsTranslated(string language)
    {
        Assert.NotEmpty(AppText.Keys);

        foreach (string key in AppText.Keys)
        {
            string text = AppText.Get(key, parameters: null);

            Assert.False(string.IsNullOrWhiteSpace(text), $"{key} está vacío en {language}");
            Assert.NotEqual(key, text);
        }
    }

    [Fact]
    public void AnUnknownKeyShowsItselfInsteadOfDisappearing()
    {
        Assert.Equal("log.this.key.does.not.exist", AppLog.Text("log.this.key.does.not.exist"));
    }

    /// <summary>
    /// Scans the sources for catalog literals: a typo in a key would otherwise only
    /// show up as a raw key in the log or in an error message of a running build.
    /// </summary>
    [Fact]
    public void EveryKeyUsedInTheSourceExistsInTheCatalog()
    {
        string root = RepositoryRoot();
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (Path.GetFileName(file).Equals("LogMessages.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file),
                         "\"(?<key>(?:log|error|name)\\.[a-z0-9_.]+)\""))
            {
                used.Add(match.Groups["key"].Value);
            }
        }

        Assert.NotEmpty(used);

        string[] missing = [.. used.Where(key => !AppText.Keys.Contains(key))];
        Assert.Empty(missing);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AirSend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"no encontré la raíz del repositorio desde {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// The failures users see (an unreachable receiver, a rejected pairing, an update
/// that does not fit) carry a catalog key instead of a fixed sentence, so the same
/// exception reads in whatever language the interface is using when it is shown.
/// </summary>
public class ErrorLocalizationTests
{
    [Fact]
    public void TheSameExceptionRendersInTheCurrentLanguage()
    {
        string previous = AppText.Language;
        try
        {
            var exception = new StreamingException("error.stream.no_ip", new { name = "卧室" });

            AppText.Language = "zh";
            string chinese = exception.LocalizedMessage;

            AppText.Language = "en";
            string english = exception.LocalizedMessage;

            AppText.Language = "es";
            string spanish = exception.LocalizedMessage;

            Assert.Contains("没有 IP 地址", chinese, StringComparison.Ordinal);
            Assert.Contains("has no IP address", english, StringComparison.Ordinal);
            Assert.Contains("no tiene dirección IP", spanish, StringComparison.Ordinal);
            Assert.Contains("卧室", chinese, StringComparison.Ordinal);
        }
        finally
        {
            AppText.Language = previous;
        }
    }

    [Fact]
    public void DomainExceptionsExposeTheirKey()
    {
        Assert.Equal(
            "error.stream.no_ip",
            new StreamingException("error.stream.no_ip", new { name = "x" }).MessageKey);
        Assert.Equal(
            "error.endpoint.zero_port",
            new ManualEndpointException(ManualEndpointError.ZeroPort, "1.2.3.4:0").MessageKey);
        Assert.Equal(
            UpdateFailure.PackageMismatch,
            new UpdateException(UpdateFailure.PackageMismatch, "error.update.package_no_executable").Reason);
        Assert.Equal(
            "error.capture.no_enumerator",
            new AudioCaptureException("error.capture.no_enumerator").MessageKey);
        Assert.Equal(
            "error.pairing.setup_m4_proof_mismatch",
            new AirPlayPairingException("error.pairing.setup_m4_proof_mismatch").MessageKey);
        Assert.Equal(
            "error.probe.not_rtsp",
            new AirPlayProbeException("error.probe.not_rtsp", new { line = "HTTP/1.0 200 OK" }).MessageKey);
    }

    [Fact]
    public void FrameworkFailuresKeepTheirOwnTechnicalText()
    {
        Assert.Equal("disk on fire", AirSendError.Describe(new IOException("disk on fire")));
    }
}
