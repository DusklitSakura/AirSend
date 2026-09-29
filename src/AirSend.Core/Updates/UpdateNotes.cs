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

            if (IsTableRow(trimmed, out string[] cells))
            {
                // "| a | b |" and the "|---|---|" underline that follows it: the
                // dialog has no table renderer, so rows become "a — b".
                if (cells.All(cell => cell.Length > 0 && cell.All(character => character is '-' or ':')))
                {
                    continue;
                }

                line = string.Join(" — ", cells.Where(cell => cell.Length > 0));
            }
            else if (trimmed.StartsWith('#'))
            {
                line = trimmed.TrimStart('#').TrimStart();
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "• " + trimmed[2..].TrimStart();
            }

            // Inline code keeps its text, only the delimiters go: "`AirSend.exe`" is
            // an executable name, not something to pad with spaces.
            builder.AppendLine(InlineSyntax()
                .Replace(line, "$1")
                .Replace("**", string.Empty)
                .Replace("`", string.Empty));
        }

        string text = builder.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n");
        }

        return text.Trim();
    }

    private static bool IsTableRow(string line, out string[] cells)
    {
        cells = [];
        if (line.Length < 2 || line[0] != '|' || line[^1] != '|')
        {
            return false;
        }

        cells = [.. line.Trim('|').Split('|').Select(cell => cell.Trim())];
        return true;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineSyntax();
}
