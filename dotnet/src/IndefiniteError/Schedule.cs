using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IndefiniteError;

/// <summary>Which phases of a site's calls may fault, for one seed.</summary>
internal enum Mode
{
    Off,
    Before,
    After,
    Both,
}

/// <summary>When a faulted call ends its request: before it runs, or after.</summary>
internal enum Phase
{
    Before,
    After,
}

/// <summary>
/// The pure core: every decision is a function of <c>(seed, site, n)</c>, as
/// <c>spec/README.md</c> defines it. Nothing here has state.
/// </summary>
internal static class Schedule
{
    // Per-seed fault rates (swarm testing): some seeds are gentle, some brutal.
    private static readonly double[] Rates = [0.01, 0.05, 0.2, 0.5];
    private static readonly Mode[] Modes = [Mode.Off, Mode.Before, Mode.After, Mode.Both];

    public static string Wire(this Mode mode) => mode switch
    {
        Mode.Off => "off",
        Mode.Before => "before",
        Mode.After => "after",
        Mode.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string Wire(this Phase phase) => phase switch
    {
        Phase.Before => "before",
        Phase.After => "after",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    public static double Rate(long seed) => Pick(Rates, Unit("rate", seed));

    public static Mode ModeOf(long seed, string site) => Pick(Modes, Unit("mode", seed, site));

    /// <summary>The phase of the <paramref name="n"/>-th call to <paramref name="site"/>, or null if it passes.</summary>
    public static Phase? Decide(long seed, double rate, Mode mode, string site, int n)
    {
        if (mode == Mode.Off || Unit("call", seed, site, n) >= rate)
        {
            return null;
        }

        return mode switch
        {
            Mode.Before => Phase.Before,
            Mode.After => Phase.After,
            _ => Unit("phase", seed, site, n) < 0.5 ? Phase.Before : Phase.After,
        };
    }

    public static double Unit(string tag, long seed) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"{tag}\0{seed}"));

    public static double Unit(string tag, long seed, string site) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"{tag}\0{seed}\0{site}"));

    public static double Unit(string tag, long seed, string site, int n) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"{tag}\0{seed}\0{site}\0{n}"));

    /// <summary>
    /// A uniform double in [0, 1) from the NUL-joined parts: the BLAKE2b-8 digest
    /// of their UTF-8, read big-endian, over 2^64.
    /// </summary>
    /// <remarks>
    /// Integers are written with the invariant culture: some cultures' minus
    /// sign isn't <c>-</c>, and a seed must decide the same everywhere.
    /// </remarks>
    private static double Hash(string joined)
    {
        Span<byte> digest = stackalloc byte[8];
        Blake2b.Hash(Encoding.UTF8.GetBytes(joined), digest);
        return BinaryPrimitives.ReadUInt64BigEndian(digest) / 18446744073709551616.0; // 2^64
    }

    public static T Pick<T>(T[] options, double u)
    {
        // A digest within 2^10 of 2^64 rounds to 1.0: as in every port, a crash, not a skew.
        if (u is not (>= 0.0 and < 1.0))
        {
            throw new InvalidOperationException($"indefinite: u={u.ToString("R", CultureInfo.InvariantCulture)} is out of [0, 1)");
        }

        return options[(int)(u * options.Length)];
    }
}
