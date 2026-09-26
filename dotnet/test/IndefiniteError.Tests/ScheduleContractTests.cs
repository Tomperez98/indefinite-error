using System.Globalization;

namespace IndefiniteError.Tests;

/// <summary><c>spec/schedule.tsv</c>: which calls fault, and when, for each seed and site.</summary>
public sealed class ScheduleContractTests
{
    private static readonly (long Seed, string Site, string Mode, string Rate, string Calls)[] Schedule_ =
        [.. Spec.Rows("schedule.tsv").Select(row => (long.Parse(row[0], CultureInfo.InvariantCulture), Spec.Site(row[1]), row[2], row[3], row[4]))];

    public static TheoryData<long, string, string, string, string> Rows() => new(Schedule_);

    [Fact]
    public void The_schedule_is_not_empty() => Assert.Equal(80, Schedule_.Length);

    [Theory]
    [MemberData(nameof(Rows))]
    public void Core_reproduces_the_schedule(long seed, string site, string mode, string rate, string calls)
    {
        var modeOf = Schedule.ModeOf(seed, site);
        var rateOf = Schedule.Rate(seed);
        Assert.Equal(mode, modeOf.Wire());
        Assert.Equal(double.Parse(rate, CultureInfo.InvariantCulture), rateOf);
        var drawn = string.Concat(Enumerable.Range(0, calls.Length).Select(n => Schedule.Decide(seed, rateOf, modeOf, site, n) switch
        {
            null => '.',
            Phase.Before => 'b',
            Phase.After => 'a',
            _ => '?',
        }));
        Assert.Equal(calls, drawn);
    }

    [Theory]
    [InlineData("sv-SE")] // its minus sign is U+2212
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void The_schedule_ignores_the_current_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Core_reproduces_the_schedule_for_negative_seeds();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private void Core_reproduces_the_schedule_for_negative_seeds()
    {
        var negative = Schedule_.Where(row => row.Seed < 0).ToList();
        Assert.NotEmpty(negative);
        foreach (var (seed, site, mode, rate, calls) in negative)
        {
            Core_reproduces_the_schedule(seed, site, mode, rate, calls);
        }
    }
}
