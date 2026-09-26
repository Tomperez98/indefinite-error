using System.Globalization;
using System.Text;

namespace IndefiniteError;

/// <summary>
/// A site name goes on the fault line, so it must be non-empty and printable,
/// with no whitespace, for the line to stay parseable.
/// </summary>
internal static class SiteName
{
    /// <summary>Why <paramref name="name"/> can't be a site, or null if it can.</summary>
    public static string? Problem(string name)
    {
        if (name.Length == 0)
        {
            return "a site name must be non-empty";
        }

        var rest = name.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return $"a site name must be valid UTF-16, got {Show(name)}";
            }

            if (!IsPrintable(rune) || Rune.IsWhiteSpace(rune))
            {
                return $"a site name must be printable with no whitespace, got {Show(name)}";
            }

            rest = rest[consumed..];
        }

        return null;
    }

    /// <summary>As Python's <c>str.isprintable</c>: no control, format, private-use, unassigned, or separator characters.</summary>
    private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator => false,
        _ => true,
    };

    /// <summary>The name with every non-printable UTF-16 unit escaped, for an error message.</summary>
    public static string Show(string name)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in name)
        {
            var printable = !char.IsSurrogate(c) && IsPrintable(new Rune(c)) && !char.IsWhiteSpace(c);
            builder.Append(printable || char.IsSurrogate(c) ? c.ToString() : $"\\u{(int)c:x4}");
        }

        return builder.Append('"').ToString();
    }
}
