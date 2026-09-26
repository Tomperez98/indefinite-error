namespace IndefiniteError;

/// <summary>What a site call draws when it starts.</summary>
internal enum DrawKind
{
    /// <summary>Run the call and return what it returns.</summary>
    Pass,

    /// <summary>Fault it.</summary>
    Fault,

    /// <summary>A fault already ended this request: the call must never run.</summary>
    Over,
}

internal readonly record struct Draw(DrawKind Kind, Fault? Fault)
{
    public static readonly Draw Pass = new(DrawKind.Pass, null);
}

/// <summary>
/// One request's injection: what its seed chose, and which sites it reached.
/// </summary>
/// <remarks>
/// The only state is a call counter per site, and whether a fault has ended
/// the request. It travels with the request through an <see cref="AsyncLocal{T}"/>:
/// every <c>await</c>, <c>Task.Run</c>, and <c>Task.WhenAll</c> the request
/// starts sees it. Once the request ends it is closed: a task that outlives
/// the request calls through with no faults.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "_ended has no timer to free, and tokens from it outlive the request: fault exceptions carry them.")]
internal sealed class Injection
{
    private static readonly AsyncLocal<Injection?> Active = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mode> _modes = new(StringComparer.Ordinal); // a cache of ModeOf(seed, site)
    private readonly CancellationTokenSource _ended = new();
    private readonly FaultLog _log;
    private Fault? _aborted;
    private bool _closed;

    private Injection(long seed, FaultLog log)
    {
        Seed = seed;
        Rate = Schedule.Rate(seed);
        _log = log;
    }

    /// <summary>The injection of the request being served, if any, open or closed.</summary>
    public static Injection? Current => Active.Value;

    public long Seed { get; }

    /// <summary>The chance that a call to a site whose mode isn't <c>off</c> faults.</summary>
    public double Rate { get; }

    /// <summary>Cancelled when a fault ends the request.</summary>
    public CancellationToken Ended => _ended.Token;

    /// <summary>The fault that ended the request, if one did.</summary>
    public Fault? Aborted
    {
        get
        {
            lock (_lock)
            {
                return _aborted;
            }
        }
    }

    /// <summary>Whether the request has ended; a closed injection injects nothing.</summary>
    public bool Closed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Makes a new injection seeded <paramref name="seed"/> current, until the
    /// scope is disposed. Throws inside another open one: the middleware is
    /// installed twice.
    /// </summary>
    public static Scope Enter(long seed, FaultLog log)
    {
        var active = Active.Value;
        if (active is not null && !active.Closed)
        {
            throw new InvalidOperationException(
                $"indefinite: a request seeded {seed} inside one seeded {active.Seed}: is UseIndefiniteErrors installed twice?");
        }

        var injection = new Injection(seed, log);
        Active.Value = injection;
        return new Scope(injection, active);
    }

    /// <summary>
    /// Counts one call to <paramref name="site"/>, and draws its outcome. A
    /// <b>before</b> fault ends the request here, in the same step: the caller
    /// only has to throw <see cref="Over"/>. An <b>after</b> fault ends it
    /// when the caller, having run the operation, calls <see cref="Abort"/>.
    /// </summary>
    public Draw Next(string site)
    {
        Fault? before;
        lock (_lock)
        {
            if (_closed)
            {
                return Draw.Pass;
            }

            if (_aborted is not null)
            {
                return new Draw(DrawKind.Over, _aborted);
            }

            var n = _calls.GetValueOrDefault(site);
            _calls[site] = checked(n + 1);
            if (!_modes.TryGetValue(site, out var mode))
            {
                mode = _modes[site] = Schedule.ModeOf(Seed, site);
            }

            var phase = Schedule.Decide(Seed, Rate, mode, site, n);
            if (phase is null)
            {
                return Draw.Pass;
            }

            var fault = new Fault(Seed, site, n, phase.Value);
            if (phase == Phase.After)
            {
                return new Draw(DrawKind.Fault, fault);
            }

            before = _aborted = fault;
        }

        End(before);
        return new Draw(DrawKind.Fault, before);
    }

    /// <summary>
    /// Ends the request with an <b>after</b> <paramref name="fault"/>, whose
    /// operation has run, unless the request is already over. Returns what to
    /// throw: the fault that ended the request, which is <paramref name="fault"/>
    /// unless another got there first. Returns null if the request has closed
    /// and there is nothing left to fault: the caller keeps the real outcome.
    /// </summary>
    public IndefiniteFaultException? Abort(Fault fault)
    {
        Fault ended;
        lock (_lock)
        {
            if (_closed)
            {
                return null;
            }

            if (_aborted is not null)
            {
                return Over(_aborted);
            }

            ended = _aborted = fault;
        }

        End(ended);
        return Over(ended);
    }

    /// <summary>Writes the line for the fault that ended the request, and cancels <see cref="Ended"/>.</summary>
    private void End(Fault fault)
    {
        // Outside the lock: the log and the token's callbacks are other people's code.
        _log($"indefinite-error: {fault}\n");
        _ended.Cancel();
    }

    /// <summary>What a call throws once a fault has ended the request.</summary>
    public IndefiniteFaultException Over(Fault fault) => new(fault, Ended);

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }

    /// <summary>Calls per site so far (a copy). A missing site was never reached.</summary>
    public Dictionary<string, int> Calls()
    {
        lock (_lock)
        {
            return new(_calls, StringComparer.Ordinal);
        }
    }

    /// <summary>Which phases may fault, per site reached so far (a copy).</summary>
    public Dictionary<string, Mode> Modes()
    {
        lock (_lock)
        {
            return new(_modes, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Forgets the fault that ended the request, keeping the counters. For
    /// tests: they make one call per "request" without resetting call numbers.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            _aborted = null;
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            var sites = string.Join(", ", _modes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Wire()}"));
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Injection(seed={Seed}, rate={Rate}, sites=[{sites}])");
        }
    }

    /// <summary>An open injection. Disposing it closes the injection and restores the one it replaced.</summary>
    internal sealed class Scope(Injection injection, Injection? previous) : IDisposable
    {
        public Injection Injection { get; } = injection;

        public void Dispose()
        {
            Injection.Close();
            Active.Value = previous;
        }
    }
}
