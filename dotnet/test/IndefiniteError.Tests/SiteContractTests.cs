namespace IndefiniteError.Tests;

/// <summary>The contract every site call keeps, in every overload. Catching the fault stands in for the middleware.</summary>
public sealed class SiteContractTests
{
    // --- Before never ran; after ran; outside an injection nothing happens ---

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Inert_outside_an_injection(Flavor flavor)
    {
        var ran = new List<int>();
        var op = flavor.Recorder(ran);
        Assert.Equal(Enumerable.Range(0, Helpers.Calls).Select(i => i * 2), Enumerable.Range(0, Helpers.Calls).Select(op));
        Assert.Equal(Enumerable.Range(0, Helpers.Calls), ran);
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Before_never_runs_after_always_runs(Flavor flavor)
    {
        foreach (var seed in Helpers.Seeds)
        {
            var ran = new List<int>();
            var outcomes = Helpers.Run(seed, flavor.Recorder(ran));
            Assert.Equal(Enumerable.Range(0, Helpers.Calls).Where(i => outcomes[i] != Outcome.Before), ran);
        }
    }

    // --- Definite outcomes pass through; cancellation outranks the fault ---

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_definite_exception_passes_through_unless_faulted(Flavor flavor)
    {
        var site = new IndefiniteSite("tests.boom");
        var seen = new HashSet<string>();
        foreach (var seed in Helpers.Seeds)
        {
            using var _ = Helpers.Inject(seed);
            try
            {
                flavor.Call(site, () => throw new DefiniteException());
            }
            catch (DefiniteException)
            {
                seen.Add("definite");
            }
            catch (IndefiniteFaultException e)
            {
                seen.Add(e.Fault.Phase.Wire());
            }
        }

        Assert.Equal(["after", "before", "definite"], seen.Order());
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Cancellation_outranks_after(Flavor flavor)
    {
        // A cancelled operation isn't an outcome: it propagates, and AFTER stays silent.
        var site = new IndefiniteSite("tests.cancelled");
        var seen = new HashSet<string>();
        foreach (var seed in Helpers.Seeds)
        {
            using var _ = Helpers.Inject(seed);
            try
            {
                flavor.Call(site, () => throw new OperationCanceledException("cancelled"));
            }
            catch (IndefiniteFaultException e)
            {
                seen.Add(e.Fault.Phase.Wire());
            }
            catch (OperationCanceledException e) when (e.Message == "cancelled")
            {
                seen.Add("cancelled");
            }
        }

        Assert.Equal(["before", "cancelled"], seen.Order());
    }

    // --- The fault ends the request ---

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_retry_loop_can_catch_the_fault_but_not_undo_it(Flavor flavor)
    {
        var ran = new List<int>();
        var op = flavor.Recorder(ran);
        var faulted = 0;
        foreach (var seed in Helpers.Seeds)
        {
            ran.Clear();
            using var scope = Helpers.Inject(seed);
            var result = "gave up";
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    op(attempt);
                    result = "done";
                    break;
                }
                catch (Exception) // a catch-all retry: it sees the fault, as it would any exception
                {
                }
            }

            if (scope.Injection.Aborted is null)
            {
                Assert.Equal("done", result);
                Assert.Equal([0], ran);
                continue;
            }

            faulted++;
            Assert.Equal("gave up", result);
            Assert.True(ran is [] or [0], "no attempt after the fault may run");
        }

        Assert.True(faulted > 0);
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_retry_that_spares_cancellation_never_sees_the_fault(Flavor flavor)
    {
        // The shape of Polly's default ShouldHandle, and of most hand-rolled retries.
        var ran = new List<int>();
        var op = flavor.Recorder(ran);
        var faulted = 0;
        foreach (var seed in Helpers.Seeds)
        {
            ran.Clear();
            var attempts = 0;
            using var _ = Helpers.Inject(seed);
            try
            {
                for (; attempts < 3; attempts++)
                {
                    try
                    {
                        op(attempts);
                        break;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                    }
                }
            }
            catch (IndefiniteFaultException)
            {
                faulted++;
            }

            Assert.Equal(0, attempts);
        }

        Assert.True(faulted > 0);
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void After_a_fault_every_site_throws_without_running_or_counting(Flavor flavor)
    {
        var ran = new List<int>();
        var op = flavor.Recorder(ran);
        var other = new IndefiniteSite("tests.other");
        var fault = Helpers.FirstFault(() => op(0));
        using var scope = Helpers.Inject(fault.Seed);
        for (var i = 0; i < fault.N; i++)
        {
            op(i);
        }

        Assert.Equal(fault, Assert.Throws<IndefiniteFaultException>(() => op(fault.N)).Fault);
        ran.Clear();
        var calls = scope.Injection.Calls();
        Assert.Equal(fault, Assert.Throws<IndefiniteFaultException>(() => op(99)).Fault);
        Assert.Equal(fault, Assert.Throws<IndefiniteFaultException>(() => flavor.Call(other, () => 1)).Fault);
        Assert.Empty(ran);
        Assert.Equal(calls, scope.Injection.Calls());
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_fault_runs_cleanup(Flavor flavor)
    {
        // The server lives on, so finally blocks run and locks are released, as on cancel.
        var site = new IndefiniteSite("tests.commit");
        var events = new List<string>();
        foreach (var seed in Helpers.Seeds)
        {
            events.Clear();
            Phase? phase = null;
            using var _ = Helpers.Inject(seed);
            try
            {
                try
                {
                    flavor.Call(site, () =>
                    {
                        events.Add("commit");
                        return 0;
                    });
                    events.Add("after");
                }
                finally
                {
                    events.Add("finally");
                }
            }
            catch (IndefiniteFaultException e)
            {
                phase = e.Fault.Phase;
            }

            string[] expected = phase switch
            {
                null => ["commit", "after", "finally"],
                Phase.Before => ["finally"],
                _ => ["commit", "finally"],
            };
            Assert.Equal(expected, events);
        }
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_fault_is_a_cancellation_of_its_request(Flavor flavor)
    {
        var op = flavor.Recorder([]);
        var fault = Helpers.FirstFault(() => op(0));
        using var scope = Helpers.Inject(fault.Seed);
        Assert.False(scope.Injection.Ended.IsCancellationRequested);
        var error = Record.Exception((Action)(() =>
        {
            for (var i = 0; ; i++)
            {
                op(i);
            }
        }));
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(scope.Injection.Ended, cancelled.CancellationToken);
        Assert.True(cancelled.CancellationToken.IsCancellationRequested);
        Assert.Null(cancelled.InnerException);
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_fault_writes_one_line(Flavor flavor)
    {
        var op = flavor.Recorder([], "logged");
        var captured = new Captured();
        var expected = new List<string>();
        foreach (var seed in Helpers.Seeds)
        {
            using var _ = Helpers.Inject(seed, captured);
            try
            {
                op(0);
            }
            catch (IndefiniteFaultException e)
            {
                expected.Add($"indefinite-error: {e.Fault}\n");
                Assert.Throws<IndefiniteFaultException>(() => op(1)); // the request is over: no second line
            }
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected, captured.Lines);
    }

    // --- Replay: same seed, same faults; sites don't interfere ---

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Same_seed_same_outcomes(Flavor flavor)
    {
        foreach (var seed in Helpers.Seeds)
        {
            Assert.Equal(Helpers.Run(seed, flavor.Recorder([])), Helpers.Run(seed, flavor.Recorder([])));
        }
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_request_depends_on_its_seed_alone(Flavor flavor)
    {
        var op = flavor.Recorder([]);
        var first = Helpers.Run(41, op);
        for (var other = 1000; other < 1100; other++)
        {
            Helpers.Run(other, op);
        }

        Assert.Equal(first, Helpers.Run(41, op));
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Sites_are_independent(Flavor flavor)
    {
        var op = flavor.Recorder([]);
        var other = flavor.Recorder([], "other");
        foreach (var seed in Helpers.Seeds)
        {
            var alone = Helpers.Run(seed, op);
            using var _ = Helpers.Inject(seed);
            var mixed = new List<Outcome>();
            for (var i = 0; i < Helpers.Calls; i++)
            {
                Helpers.Attempt(other, i);
                mixed.Add(Helpers.Attempt(op, i));
            }

            Assert.Equal(alone, mixed);
        }
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Seeds_vary_which_phases_a_site_gets(Flavor flavor)
    {
        // Swarm testing: per seed, a site faults never, before-only, after-only, or both.
        var op = flavor.Recorder([]);
        var kinds = Helpers.Seeds
            .Select(seed => string.Join(",", Helpers.Run(seed, op).Where(o => o != Outcome.Ok).Distinct().Order()))
            .ToHashSet();
        Assert.Equal(["", "After", "Before", "Before,After"], kinds.Order(StringComparer.Ordinal));
    }

    // --- What the fault says ---

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void A_fault_names_the_site_call_and_seed(Flavor flavor)
    {
        var op = flavor.Recorder([], "db.commit");
        var fault = Helpers.FirstFault(() => op(0));
        Assert.Equal("db.commit", fault.Site);
        Assert.Equal(0, fault.N);
        Assert.Equal($"{fault.Phase.Wire()} db.commit#0 (seed={fault.Seed})", fault.ToString());
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Calls_count_every_call_faulted_or_not(Flavor flavor)
    {
        var op = flavor.Recorder([], "counted");
        foreach (var seed in Helpers.Seeds)
        {
            using var scope = Helpers.Inject(seed);
            for (var i = 0; i < Helpers.Calls; i++)
            {
                Helpers.Attempt(op, i);
            }

            Assert.Equal(new Dictionary<string, int> { ["counted"] = Helpers.Calls }, scope.Injection.Calls());
        }
    }

    [Theory]
    [MemberData(nameof(Helpers.AllFlavors), MemberType = typeof(Helpers))]
    public void Two_sites_with_one_name_share_a_fault_stream(Flavor flavor)
    {
        var a = flavor.Recorder([], "shared");
        var b = flavor.Recorder([], "shared");
        var c = flavor.Recorder([], "apart");
        using var scope = Helpers.Inject(0);
        foreach (var op in new[] { a, b, c })
        {
            Helpers.Attempt(op, 0);
        }

        Assert.Equal(new Dictionary<string, int> { ["shared"] = 2, ["apart"] = 1 }, scope.Injection.Calls());
    }
}
