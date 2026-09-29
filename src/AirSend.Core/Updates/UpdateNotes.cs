using System.Text;
using System.Text.RegularExpressions;

namespace AirSend.Core.Updates;

/// <summary>
/// GitHub release notes are Markdown; the update dialog shows them in a plain
/// <c>TextBlock</c>. This strips the syntax that would otherwise show up literally
/// without trying to be a full Markdown renderer.
/// </summary>
public static partial class UpdateNotes
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (string rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                line = trimmed.TrimStart('#').TrimStart();
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "• " + trimmed[2..].TrimStart();
            }

            builder.AppendLine(InlineSyntax().Replace(line, "$1").Replace("**", string.Empty).Replace('`', ' '));
        }

        string text = builder.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n");
        }

        return text.Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineSyntax();
}
