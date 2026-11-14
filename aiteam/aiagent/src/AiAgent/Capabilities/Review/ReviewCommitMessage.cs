using System.Text;
using System.Text.RegularExpressions;

namespace AiAgent.Capabilities.Review;

/// <summary>
/// Builds the commit message for one fixed review comment: a subject naming what is fixed and the file
/// where the reviewer found it, and a body quoting the comment. The comment is untrusted text, so control
/// characters are removed and its length is limited.
/// </summary>
public static partial class ReviewCommitMessage
{
    public const int MaxSubjectLength = 100;
    public const int MaxBodyLength = 2_000;

    public static string Build(string jiraKey, ReviewComment comment)
    {
        var location = Location(comment);
        var prefix = $"{jiraKey}: ";
        var suffix = $" ({location})";
        var summary = Summary(comment.Body);
        var room = MaxSubjectLength - prefix.Length - suffix.Length;
        var subject = summary.Length == 0
            ? $"{prefix}Fix review comment in {location}"
            : prefix + Truncate(summary, Math.Max(room, 20)) + suffix;

        // Explicit '\n' so the message is the same on every platform.
        var message = new StringBuilder();
        message.Append(subject).Append("\n\n");
        message.Append($"Review comment {comment.Id} by {Clean(comment.Author)} on {location}:").Append("\n\n");
        foreach (var line in Truncate(CleanBody(comment.Body), MaxBodyLength).Split('\n'))
        {
            message.Append(line.Length == 0 ? ">" : $"> {line}").Append('\n');
        }

        return message.ToString().TrimEnd() + "\n";
    }

    /// <summary>First sentence of the first prose line, without markdown decoration or suggestion blocks.</summary>
    internal static string Summary(string body)
    {
        var inCodeBlock = false;
        foreach (var raw in CleanBody(body).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock || line.Length == 0)
            {
                continue;
            }

            line = MarkdownDecoration().Replace(line, string.Empty).Trim();
            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal);
            if (line.Length == 0)
            {
                continue;
            }

            var sentenceEnd = SentenceEnd().Match(line);
            var sentence = sentenceEnd.Success ? line[..sentenceEnd.Index] : line.TrimEnd('.', ':');
            return sentence.Length == 0 ? line : char.ToUpperInvariant(sentence[0]) + sentence[1..];
        }

        return string.Empty;
    }

    private static string Location(ReviewComment comment)
    {
        var path = Clean(comment.Path);
        if (path.Length == 0)
        {
            path = "unknown file";
        }

        return comment.Line is { } line ? $"{path}:{line}" : path;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";

    /// <summary>Single line with control characters removed.</summary>
    private static string Clean(string text) =>
        new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>Keeps line breaks, removes other control characters.</summary>
    private static string CleanBody(string text) =>
        new string(text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();

    // Leading list markers, headings and block quotes.
    [GeneratedRegex(@"^(?:[#>*\-+]+\s*|\d+[.)]\s+)+")]
    private static partial Regex MarkdownDecoration();

    // End of the first sentence: '.', '!' or '?' followed by whitespace or end of line.
    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();
}
