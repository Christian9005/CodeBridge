using System.Globalization;
using System.Text;

namespace CodeBridge.Flow.CodeGeneration;

/// <summary>
/// Puts text from a flow file into generated C#. A file is not trusted (it can come from an e-mail, a repository or a web editor), so whatever it holds must
/// end up as inert text inside a string or a comment, never as code: line breaks of every kind, quotes, braces, control characters and the invisible
/// characters that can reorder code on screen are all written as escapes or replaced.
/// </summary>
internal static class CSharpText
{
    private const int MaxCommentLength = 120;

    /// <summary>A regular string literal, quotes included.</summary>
    public static string Literal(string? value) => "\"" + Escape(value, interpolated: false) + "\"";

    /// <summary>The inside of an interpolated string literal ($"..."): like <see cref="Literal"/> but braces are doubled.</summary>
    public static string InterpolatedContent(string? value) => Escape(value, interpolated: true);

    /// <summary>One short line of plain text for a <c>//</c> comment: it cannot end the comment, however it is written.</summary>
    public static string Comment(string? value)
    {
        var text = value ?? string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, MaxCommentLength + 3));
        foreach (var ch in text)
        {
            if (builder.Length >= MaxCommentLength)
            {
                builder.Append("...");
                break;
            }

            builder.Append(NeedsEscape(ch) ? ' ' : ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Like <see cref="Comment"/>, safe inside an XML documentation comment too.</summary>
    public static string DocComment(string? value) =>
        Comment(value).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Escape(string? value, bool interpolated)
    {
        var text = value ?? string.Empty;
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '{' when interpolated:
                    builder.Append("{{");
                    break;
                case '}' when interpolated:
                    builder.Append("}}");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (NeedsEscape(ch))
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Control characters, every kind of line or paragraph break (U+0085, U+2028, U+2029), invisible format characters (bidi overrides, zero-width) and surrogates.</summary>
    private static bool NeedsEscape(char ch)
    {
        if (char.IsControl(ch) || char.IsSurrogate(ch))
            return true;

        return CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }
}
