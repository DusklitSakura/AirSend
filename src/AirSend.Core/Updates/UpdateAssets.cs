using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AirSend.Core.Updates;

/// <summary>
/// What a particular copy of AirSend is: the architecture it was built for, whether
/// the .NET runtime travels with it, and the version it was built as.
/// </summary>
/// <remarks>
/// <c>AirSend.App.csproj</c> writes these three values to <c>build-info.txt</c> next
/// to the executable, and the zip packages ship that file. The updater therefore
/// knows exactly which package may replace this installation instead of inferring it
/// from the file layout.
/// </remarks>
public sealed record InstalledBuild(string RuntimeIdentifier, bool SelfContained, string? Version)
{
    public const string FileName = "build-info.txt";

    public string Flavor => SelfContained ? "selfcontained" : "frameworkdependent";

    /// <summary>The ending every package that may replace this installation has.</summary>
    public string PackageSuffix => $"-{RuntimeIdentifier}-{Flavor}.zip";

    public string Describe() => $"{RuntimeIdentifier} · {Flavor}";

    public static string RuntimeIdentifierFor(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64",
    };

    /// <summary>
    /// Reads the identity written at build time. Copies that predate the file (or a
    /// plain dev build) fall back to what can be observed: the architecture of the
    /// running process and whether the .NET runtime sits in the same folder.
    /// </summary>
    public static InstalledBuild Detect(string baseDirectory)
    {
        Dictionary<string, string> values = ReadBuildInfo(baseDirectory);

        string runtime = values.TryGetValue("runtime", out string? runtimeValue) && !string.IsNullOrWhiteSpace(runtimeValue)
            ? runtimeValue.Trim()
            : RuntimeIdentifierFor(RuntimeInformation.ProcessArchitecture);

        bool selfContained = values.TryGetValue("flavor", out string? flavor) && !string.IsNullOrWhiteSpace(flavor)
            ? flavor.Trim().Equals("selfcontained", StringComparison.OrdinalIgnoreCase)
            : LooksSelfContained(baseDirectory);

        string? version = values.TryGetValue("version", out string? versionValue) && !string.IsNullOrWhiteSpace(versionValue)
            ? versionValue.Trim()
            : null;

        return new InstalledBuild(runtime, selfContained, version);
    }

    /// <summary>
    /// A build is self-contained when the .NET runtime sits next to the executable.
    /// Only used for copies that have no <c>build-info.txt</c>.
    /// </summary>
    public static bool LooksSelfContained(string baseDirectory) =>
        File.Exists(Path.Combine(baseDirectory, "coreclr.dll"));

    /// <summary>Exact contents of <c>build-info.txt</c>; the build writes the same lines.</summary>
    public static string Format(string runtimeIdentifier, bool selfContained, string version) => string.Join(
        Environment.NewLine,
        $"version={version}",
        $"runtime={runtimeIdentifier}",
        $"flavor={(selfContained ? "selfcontained" : "frameworkdependent")}") + Environment.NewLine;

    private static Dictionary<string, string> ReadBuildInfo(string baseDirectory)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(baseDirectory, FileName);

        try
        {
            if (!File.Exists(path))
            {
                return values;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable build info only costs precision: the caller falls back to
            // the observable layout, and the package is verified again later.
        }

        return values;
    }
}

/// <summary>
/// Picks the release asset that matches the running build and re-checks the
/// unpacked package before it is allowed to replace anything.
/// </summary>
public enum UpdateFailure
{
    /// <summary>The release has no package for this architecture/flavour/version.</summary>
    NoPackageForThisBuild,

    /// <summary>The downloaded package is not the one that was requested.</summary>
    PackageMismatch,
}

public sealed class UpdateException : Exception
{
    public UpdateException(UpdateFailure reason, string message)
        : base(message) => Reason = reason;

    public UpdateFailure Reason { get; }
}

public static class UpdateAssets
{
    public static string CurrentRuntimeIdentifier() =>
        InstalledBuild.RuntimeIdentifierFor(RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// The asset that may replace <paramref name="build"/> at <paramref name="version"/>.
    /// Matching is deliberately strict: a package for another architecture, another
    /// flavour or another version would either not run at all or leave stale files
    /// behind. When the release has no such package the result is null, and the
    /// caller tells the user to fetch it from the release page instead of downloading
    /// something that does not fit.
    /// </summary>
    public static UpdateAsset? Select(
        IReadOnlyList<UpdateAsset> assets,
        InstalledBuild build,
        UpdateVersion version)
    {
        string suffix = build.PackageSuffix;
        string versionMarker = $"-{version}-";

        return assets.FirstOrDefault(asset =>
            !string.IsNullOrWhiteSpace(asset.DownloadUrl)
            && asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && asset.Name.Contains(versionMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies an unpacked package: it has to contain the executable and, when it
    /// carries its own build info, that info has to describe the architecture,
    /// flavour and version that were asked for. Throws when it does not.
    /// </summary>
    public static void EnsurePackageMatches(
        string packageDirectory,
        InstalledBuild build,
        UpdateVersion version)
    {
        string executable = Path.Combine(packageDirectory, "AirSend.exe");
        if (!File.Exists(executable))
        {
            throw new UpdateException(UpdateFailure.PackageMismatch, "el paquete no contiene AirSend.exe");
        }

        InstalledBuild package = InstalledBuild.Detect(packageDirectory);

        if (package.Version is null)
        {
            // Releases published before build-info.txt existed. The package still
            // gives itself away: a self-contained one carries the .NET runtime next
            // to the executable, and the executable itself names its architecture.
            string packageFlavor = InstalledBuild.LooksSelfContained(packageDirectory)
                ? "selfcontained"
                : "frameworkdependent";

            if (!string.Equals(packageFlavor, build.Flavor, StringComparison.Ordinal))
            {
                throw new UpdateException(
                    UpdateFailure.PackageMismatch,
                    $"el paquete es {packageFlavor} y esta instalación es {build.Flavor}");
            }

            if (MachineOf(executable) is { } machine)
            {
                string packageRuntime = InstalledBuild.RuntimeIdentifierFor(machine);
                if (!string.Equals(packageRuntime, build.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(
                        UpdateFailure.PackageMismatch,
                        $"el ejecutable del paquete es {packageRuntime} y esta instalación es {build.RuntimeIdentifier}");
                }
            }

            if (UpdateVersion.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion, out UpdateVersion fileVersion)
                && !fileVersion.Equals(version))
            {
                throw new UpdateException(
                    UpdateFailure.PackageMismatch,
                    $"el paquete contiene la versión {fileVersion} y no {version}");
            }

            return;
        }

        if (!string.Equals(package.RuntimeIdentifier, build.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(
                UpdateFailure.PackageMismatch,
                $"el paquete es para {package.RuntimeIdentifier} y esta instalación es {build.RuntimeIdentifier}");
        }

        if (package.SelfContained != build.SelfContained)
        {
            throw new UpdateException(
                UpdateFailure.PackageMismatch,
                $"el paquete es {package.Flavor} y esta instalación es {build.Flavor}");
        }

        if (UpdateVersion.TryParse(package.Version, out UpdateVersion packageVersion) && !packageVersion.Equals(version))
        {
            throw new UpdateException(
                UpdateFailure.PackageMismatch,
                $"el paquete contiene la versión {packageVersion} y no {version}");
        }
    }

    /// <summary>
    /// Architecture recorded in a PE file ("MZ" → e_lfanew → "PE\0\0" → Machine),
    /// or null when the file cannot be read. Used to check packages that predate
    /// build-info.txt without having to run them.
    /// </summary>
    internal static Architecture? MachineOf(string executablePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(executablePath);
            Span<byte> dosHeader = stackalloc byte[64];

            if (stream.Read(dosHeader) < dosHeader.Length || dosHeader[0] != 'M' || dosHeader[1] != 'Z')
            {
                return null;
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
            if (peOffset <= 0 || peOffset > stream.Length - 6)
            {
                return null;
            }

            stream.Position = peOffset;
            Span<byte> peHeader = stackalloc byte[6];

            if (stream.Read(peHeader) < peHeader.Length || peHeader[0] != 'P' || peHeader[1] != 'E')
            {
                return null;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]) switch
            {
                0x014c => Architecture.X86,
                0x8664 => Architecture.X64,
                0xaa64 => Architecture.Arm64,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
