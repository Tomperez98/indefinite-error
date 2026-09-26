using System.Globalization;

namespace IndefiniteError.Tests;

/// <summary><c>spec/faults.tsv</c>: the fault line every implementation writes.</summary>
public sealed class FaultContractTests
{
    public static TheoryData<long, string, int, string> Rows() =>
        new(Spec.Rows("faults.tsv").Select(row =>
            (long.Parse(row[0], CultureInfo.InvariantCulture), Spec.Site(row[1]), int.Parse(row[2], CultureInfo.InvariantCulture), row[3])));

    [Theory]
    [MemberData(nameof(Rows))]
    public void Call_n_faults_with_the_payload_and_writes_its_line(long seed, string site, int n, string payload)
    {
        // Call site n+1 times in one injection, resuming after each fault, as if each were its own request.
        var op = Flavor.Func.Recorder([], site);
        var captured = new Captured();
        var faults = new List<Fault>();
        using (Helpers.Inject(seed, captured))
        {
            for (var i = 0; i <= n; i++)
            {
                try
                {
                    op(i);
                }
                catch (IndefiniteFaultException e)
                {
                    faults.Add(e.Fault);
                    Injection.Current!.Resume();
                }
            }
        }

        Assert.NotEmpty(faults);
        Assert.Equal(n, faults[^1].N);
        Assert.Equal(payload, faults[^1].ToString());
        Assert.Equal($"indefinite-error: {payload}\n", captured.Lines[^1]);
    }
}
