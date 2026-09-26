namespace IndefiniteError.Tests;

/// <summary>How an operation is run through its site: the four overloads of one contract.</summary>
public enum Flavor
{
    Action,
    Func,
    AsyncTask,
    AsyncTaskOfT,
}

public enum Outcome
{
    Ok,
    Before,
    After,
}

/// <summary>Fault lines, captured instead of written to stderr.</summary>
internal sealed class Captured
{
    private readonly List<string> _lines = [];

    public FaultLog Log => line =>
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    };

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }
}

/// <summary>Shared test helpers. They fail the test themselves; none returns an error.</summary>
internal static class Helpers
{
    public const int Calls = 50;

    public static readonly IReadOnlyList<long> Seeds = [.. Enumerable.Range(0, 300).Select(i => (long)i)];

    public static readonly Flavor[] Flavors = Enum.GetValues<Flavor>();

    public static TheoryData<Flavor> AllFlavors => new(Flavors);

    private static readonly Captured Discarded = new();

    public static Injection.Scope Inject(long seed, Captured? captured = null) =>
        Injection.Enter(seed, (captured ?? Discarded).Log);

    /// <summary>
    /// Calls <paramref name="body"/> through <paramref name="site"/> in this
    /// flavor, as one blocking call. An async flavor yields first, like I/O.
    /// </summary>
    public static int Call(this Flavor flavor, IndefiniteSite site, Func<int> body)
    {
        switch (flavor)
        {
            case Flavor.Action:
                var result = 0;
                site.Run(() => { result = body(); });
                return result;
            case Flavor.Func:
                return site.Run(body);
            case Flavor.AsyncTask:
                var asyncResult = 0;
                site.RunAsync(async () =>
                {
                    await Task.Yield();
                    asyncResult = body();
                }).GetAwaiter().GetResult();
                return asyncResult;
            case Flavor.AsyncTaskOfT:
                return site.RunAsync(async () =>
                {
                    await Task.Yield();
                    return body();
                }).GetAwaiter().GetResult();
            default:
                throw new ArgumentOutOfRangeException(nameof(flavor), flavor, null);
        }
    }

    /// <summary>An op that records each real execution and returns <c>i * 2</c>.</summary>
    public static Func<int, int> Recorder(this Flavor flavor, List<int> ran, string name = "tests.op")
    {
        var site = new IndefiniteSite(name);
        return i => flavor.Call(site, () =>
        {
            ran.Add(i);
            return i * 2;
        });
    }

    /// <summary>
    /// Ok, or the injected phase: one call stands for one request. Catching
    /// the fault here stands in for the middleware; resuming lets the next
    /// call run as if in a fresh request, with the call numbers kept.
    /// </summary>
    public static Outcome Attempt(Func<int, int> op, int i)
    {
        try
        {
            var result = op(i);
            Assert.Equal(i * 2, result);
            return Outcome.Ok;
        }
        catch (IndefiniteFaultException e)
        {
            Injection.Current!.Resume();
            return e.Fault.Phase == Phase.Before ? Outcome.Before : Outcome.After;
        }
    }

    /// <summary>The outcomes of <paramref name="calls"/> calls to <paramref name="op"/> in one injection.</summary>
    public static List<Outcome> Run(long seed, Func<int, int> op, int calls = Calls)
    {
        using var _ = Inject(seed);
        return [.. Enumerable.Range(0, calls).Select(i => Attempt(op, i))];
    }

    /// <summary>The first fault <paramref name="call"/> hits across <see cref="Seeds"/> (of <paramref name="phase"/>, if given).</summary>
    public static Fault FirstFault(Action call, Phase? phase = null)
    {
        foreach (var seed in Seeds)
        {
            using var _ = Inject(seed);
            try
            {
                call();
            }
            catch (IndefiniteFaultException e) when (phase is null || e.Fault.Phase == phase)
            {
                return e.Fault;
            }
            catch (IndefiniteFaultException)
            {
            }
        }

        Assert.Fail($"no seed in 0..299 faulted {phase?.Wire() ?? "at all"}");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The first seed that gives <paramref name="site"/> this <paramref name="mode"/>.</summary>
    public static long SeedWhere(string site, Mode mode)
    {
        foreach (var seed in Seeds)
        {
            if (Schedule.ModeOf(seed, site) == mode)
            {
                return seed;
            }
        }

        Assert.Fail($"no seed in 0..299 gives {site} mode {mode.Wire()}");
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>A failure the operation itself reports: a value its caller handles.</summary>
internal sealed class DefiniteException : Exception
{
    public DefiniteException()
        : base("definite")
    {
    }
}
