namespace IndefiniteError.Tests;

/// <summary>The pure core: BLAKE2b, unit, pick, site names, and the fault payload.</summary>
public sealed class CoreTests
{
    private static byte[] Counting(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static string Blake2bHex(byte[] data, int size)
    {
        var digest = new byte[size];
        Blake2b.Hash(data, digest);
        return Convert.ToHexStringLower(digest);
    }

    [Fact]
    public void Blake2b_512_matches_rfc_7693() =>
        Assert.Equal(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1"
            + "7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Blake2bHex("abc"u8.ToArray(), 64));

    // From Python's hashlib.blake2b(data, digest_size=8): an independent implementation.
    // Lengths straddle the 128-byte block, where the final flag moves.
    [Theory]
    [InlineData(0, "e4a6a0577479b2b4")]
    [InlineData(128, "c2d13df1b6617e82")]
    [InlineData(129, "f667e47a6d5350a6")]
    public void Blake2b_8_matches_hashlib(int length, string hex) => Assert.Equal(hex, Blake2bHex(Counting(length), 8));

    [Fact]
    public void Blake2b_8_is_not_a_truncated_blake2b_512() =>
        Assert.NotEqual(Blake2bHex("abc"u8.ToArray(), 64)[..16], Blake2bHex("abc"u8.ToArray(), 8));

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Blake2b_rejects_a_digest_size_out_of_range(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Blake2b.Hash([], new byte[size]));

    [Fact]
    public void Unit_is_the_big_endian_digest_over_two_to_the_64()
    {
        // hashlib.blake2b(b"rate\x000", digest_size=8) = aeb5ae81384e97ea
        Assert.Equal(0xaeb5ae81384e97eaUL / 18446744073709551616.0, Schedule.Unit("rate", 0));
    }

    [Fact]
    public void Unit_is_in_zero_one_and_spreads()
    {
        var draws = Enumerable.Range(0, 10_000).Select(n => Schedule.Unit("call", 7, "site", n)).ToList();
        Assert.All(draws, u => Assert.InRange(u, 0.0, Math.BitDecrement(1.0)));
        Assert.InRange(draws.Average(), 0.49, 0.51);
    }

    [Fact]
    public void Nul_joined_parts_do_not_collide() =>
        Assert.NotEqual(Schedule.Unit("mode", 0, "a\0b"), Schedule.Unit("mode", 0, "a"));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.2499, 0)]
    [InlineData(0.25, 1)]
    [InlineData(0.9999999999999999, 3)]
    public void Pick_floors_u_times_the_count(double u, int index) => Assert.Equal(index, Schedule.Pick([0, 1, 2, 3], u));

    [Theory]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Pick_crashes_out_of_zero_one(double u) =>
        Assert.Throws<InvalidOperationException>(() => Schedule.Pick([0, 1, 2, 3], u));

    [Fact]
    public void Rates_span_gentle_to_brutal() =>
        Assert.Equal([0.01, 0.05, 0.2, 0.5], Enumerable.Range(0, 300).Select(s => Schedule.Rate(s)).Distinct().Order());

    [Fact]
    public void An_off_site_never_faults() =>
        Assert.All(Enumerable.Range(0, 1000), n => Assert.Null(Schedule.Decide(3, 0.99, Mode.Off, "s", n)));

    [Theory]
    [InlineData("db.commit")]
    [InlineData("ünïcode.sïte")]
    [InlineData("Store.DepositAsync")]
    [InlineData("emoji.🙂")]
    public void Valid_site_names(string name) => Assert.Null(SiteName.Problem(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\u00a0b")] // no-break space
    [InlineData("a\u2028b")] // line separator
    [InlineData("a\u200bb")] // zero-width space: a format character
    [InlineData("a\ue000b")] // private use
    public void Invalid_site_names(string name) => Assert.NotNull(SiteName.Problem(name));

    [Fact] // not [InlineData]: a lone surrogate doesn't survive test-case serialization
    public void A_lone_surrogate_is_not_a_site_name()
    {
        Assert.Equal("a site name must be valid UTF-16, got \"a\ud800b\"", SiteName.Problem("a\ud800b"));
        Assert.NotNull(SiteName.Problem("\udc00"));
    }

    [Fact]
    public void Site_problems_show_what_is_invisible() =>
        Assert.Equal("a site name must be printable with no whitespace, got \"a\\u0009b\"", SiteName.Problem("a\tb"));

    [Fact]
    public void The_payload_is_phase_site_call_and_seed() =>
        Assert.Equal("after app.get#4 (seed=13)", new Fault(13, "app.get", 4, Phase.After).ToString());

    [Fact]
    public void Wire_names_reject_values_outside_the_enum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Mode)99).Wire());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Phase)99).Wire());
    }

    [Fact]
    public void A_fault_line_is_one_write_of_utf8()
    {
        var stream = new WriteCounter();
        FaultLogs.To(stream)("indefinite-error: before ünïcode.sïte#31 (seed=-1)\n");
        Assert.Equal(1, stream.Writes);
        Assert.Equal("indefinite-error: before ünïcode.sïte#31 (seed=-1)\n", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void A_closed_stderr_loses_the_line_not_the_request()
    {
        var stream = new WriteCounter { Broken = true };
        FaultLogs.To(stream)("indefinite-error: x\n");
        Assert.Equal(1, stream.Writes);
        Assert.NotNull(FaultLogs.Stderr);
    }

    private sealed class WriteCounter : MemoryStream
    {
        public int Writes { get; private set; }

        public bool Broken { get; init; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            if (Broken)
            {
                throw new IOException("closed");
            }

            base.Write(buffer);
        }
    }
}
