using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace IndefiniteError.AspNetCore;

/// <summary>How the middleware reads <see cref="IndefiniteErrorHeaders.Seed"/>: <c>spec/seed-header.tsv</c>.</summary>
internal static class SeedHeader
{
    /// <summary>
    /// The seed, if <paramref name="values"/> is exactly one value matching
    /// <c>-?[0-9]{1,19}</c> that fits an int64. The header is untrusted input:
    /// <c>+1</c>, <c> 1</c>, <c>1_0</c>, and non-ASCII digits are all malformed.
    /// </summary>
    public static bool TryParse(StringValues values, out long seed)
    {
        seed = 0;
        if (values.Count != 1 || values[0] is not { } raw)
        {
            return false;
        }

        var digits = raw.StartsWith('-') ? raw.AsSpan(1) : raw.AsSpan();
        if (digits.Length is < 1 or > 19 || digits.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed); // only a range error is left
    }
}
