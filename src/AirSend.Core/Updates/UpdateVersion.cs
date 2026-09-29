using System.Globalization;

namespace AirSend.Core.Updates;

/// <summary>
/// Version of a release tag such as <c>v0.2.1</c> or <c>0.3.0-beta.1</c>.
/// </summary>
/// <remarks>
/// Only what the update check needs is implemented: the numeric core compared
/// field by field, plus the rule that a final release outranks its own
/// pre-releases. Pre-release identifiers are compared as strings, which is enough
/// because <c>/releases/latest</c> never returns a pre-release in the first place.
/// </remarks>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        string? preRelease = null;
        int dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];
        }

        // Build metadata ("0.2.1+abc") does not take part in the comparison.
        int plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        string[] parts = trimmed.Split('.');
        var numbers = new int[3];
        for (int index = 0; index < numbers.Length; index++)
        {
            string part = index < parts.Length ? parts[index] : "0";
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new UpdateVersion(
            numbers[0],
            numbers[1],
            numbers[2],
            string.IsNullOrWhiteSpace(preRelease) ? null : preRelease);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        if (PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (PreRelease is null)
        {
            return 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsNewerThan(UpdateVersion other) => CompareTo(other) > 0;

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
