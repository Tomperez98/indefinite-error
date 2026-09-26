using System.Runtime.CompilerServices;

namespace IndefiniteError.Tests;

/// <summary>Naming, misuse, how the injection flows, and when it closes.</summary>
public sealed class SiteApiTests
{
    private static readonly IndefiniteSite Site = new("tests.api");

    /// <summary>A synchronous rejection, even of a call that returns a task.</summary>
    private static InvalidOperationException Rejects(Action call) => Throws<InvalidOperationException>(call);

    /// <summary>Argument checks throw synchronously, as .NET async methods do.</summary>
    private static T Throws<T>(Action call)
        where T : Exception => Assert.Throws<T>(call);

    // --- Naming ---

    [Fact]
    public void A_site_is_its_name()
    {
        var site = new IndefiniteSite("db.commit");
        Assert.Equal("db.commit", site.Name);
        Assert.Equal("db.commit", site.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("db commit")]
    [InlineData("db\ncommit")]
    public void An_invalid_name_is_rejected_on_construction(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => new IndefiniteSite(name));
        Assert.Equal("name", error.ParamName);
        Assert.StartsWith("indefinite: a site name must be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_name_or_operation_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new IndefiniteSite(null!));
        Assert.Throws<ArgumentNullException>(() => Site.Run(null!));
        Assert.Throws<ArgumentNullException>(() => Site.Run<int>(null!));
        Throws<ArgumentNullException>(() => Site.RunAsync(null!));
        Throws<ArgumentNullException>(() => Site.RunAsync<int>(null!));
    }

    // --- A result that hasn't happened yet: the operation must finish before the call returns ---

    [Fact]
    public void Run_rejects_a_task_even_outside_an_injection()
    {
        var ran = false;
        var error = Rejects(() => Site.Run(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }));
        Assert.Contains("hasn't happened when it returns. Use RunAsync.", error.Message, StringComparison.Ordinal);
        Assert.False(ran, "rejected before it runs");
    }

    [Fact]
    public void Run_rejects_an_async_lambda()
    {
        // `async () => ...` binds to Run<Task>, not to Run(Action) as async void.
        Rejects(() => Site.Run(async () => await Task.Yield()));
    }

    [Fact]
    public void Run_rejects_other_deferred_results()
    {
#pragma warning disable CA2012 // the point: a ValueTask the site must refuse
        Rejects(() => Site.Run(() => ValueTask.CompletedTask));
        Rejects(() => Site.Run(() => ValueTask.FromResult(1)));
#pragma warning restore CA2012
        Rejects(() => Site.Run(() => Task.FromResult(1)));
        Rejects(() => Site.Run(() => new[] { 1 }.AsQueryable()));
        Rejects(() => Site.Run(Numbers));
        Rejects(() => Site.RunAsync(() => Task.FromResult(Task.CompletedTask)));
    }

    private static async IAsyncEnumerable<int> Numbers()
    {
        await Task.Yield();
        yield return 1;
    }

    [Fact]
    public void Run_rejects_an_async_void_delegate_in_a_seeded_request()
    {
        Action asyncVoid = AsyncVoid;
        Assert.True(asyncVoid.Method.IsDefined(typeof(AsyncStateMachineAttribute), false));
        Site.Run(() => { }); // an ordinary Action is fine
        using var _ = Helpers.Inject(0);
        var error = Assert.Throws<InvalidOperationException>(() => Site.Run(asyncVoid));
        Assert.Contains("async void", error.Message, StringComparison.Ordinal);
    }

    private static async void AsyncVoid() => await Task.Yield(); // the point: an async void the site must refuse

    [Fact]
    public void Run_passes_a_materialized_result_through() =>
        Assert.Equal([1, 2], Site.Run(() => new List<int> { 1, 2 }));

    // --- Async shape: faults surface through the task, never synchronously ---

    [Fact]
    public async Task A_before_fault_is_a_cancelled_task_not_a_synchronous_throw()
    {
        var site = new IndefiniteSite("tests.async");
        var seed = Helpers.SeedWhere(site.Name, Mode.Before);
        using var _ = Helpers.Inject(seed);
        var ran = 0;
        Task? task = null;
        for (var i = 0; i < 200 && task?.IsCanceled != true; i++)
        {
            task = site.RunAsync(() =>
            {
                ran++;
                return Task.CompletedTask;
            });
        }

        Assert.True(task!.IsCanceled);
        await Assert.ThrowsAsync<IndefiniteFaultException>(() => task);
        Assert.Equal(Injection.Current!.Calls()["tests.async"] - 1, ran);
    }

    [Fact]
    public async Task Outside_an_injection_RunAsync_returns_the_operations_own_task()
    {
        var task = Task.FromResult(7);
        Assert.Same(task, Site.RunAsync(() => task));
        Task plain = Task.CompletedTask;
        Assert.Same(plain, Site.RunAsync(() => plain));
        Assert.Equal(7, await Site.RunAsync(() => task));
    }

    // --- How the injection flows ---

    [Fact]
    public async Task The_injection_flows_through_await_task_run_and_when_all()
    {
        using var scope = Helpers.Inject(0);
        await Task.Yield();
        Assert.Same(scope.Injection, Injection.Current);
        Assert.Same(scope.Injection, await Task.Run(() => Injection.Current));
        var seen = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await Task.Delay(1);
            return Injection.Current;
        }));
        Assert.All(seen, s => Assert.Same(scope.Injection, s));

        Injection? onThread = null;
        var thread = new Thread(() => onThread = Injection.Current);
        thread.Start();
        thread.Join();
        Assert.Same(scope.Injection, onThread);
    }

    [Fact]
    public async Task Work_queued_without_the_execution_context_does_not_see_the_injection()
    {
        using var scope = Helpers.Inject(0);
        var seen = new TaskCompletionSource<Injection?>();
        ThreadPool.UnsafeQueueUserWorkItem(_ => seen.SetResult(Injection.Current), null);
        Assert.Null(await seen.Task);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_share_counters()
    {
        var op = Flavor.AsyncTaskOfT.Recorder([], "tests.concurrent");
        var alone = Helpers.Run(3, op);
        var together = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Helpers.Run(3, op))));
        Assert.All(together, outcomes => Assert.Equal(alone, outcomes));
    }

    // --- Lifecycle ---

    [Fact]
    public void An_injection_inside_an_open_one_is_a_bug()
    {
        using (Helpers.Inject(1))
        {
            var error = Assert.Throws<InvalidOperationException>(() => Helpers.Inject(2));
            Assert.Equal("indefinite: a request seeded 2 inside one seeded 1: is UseIndefiniteErrors installed twice?", error.Message);
        }

        Assert.Null(Injection.Current);
    }

    [Fact]
    public async Task A_task_that_outlives_its_request_calls_through()
    {
        var site = new IndefiniteSite("tests.late");
        var seed = Helpers.SeedWhere(site.Name, Mode.Before);
        var release = new TaskCompletionSource();
        Task<int> late;
        using (var scope = Helpers.Inject(seed))
        {
            late = Task.Run(async () =>
            {
                await release.Task;
                return Enumerable.Range(0, 200).Sum(i => site.Run(() => 1));
            });
        }

        release.SetResult();
        Assert.Equal(200, await late);
    }

    [Fact]
    public async Task An_after_fault_whose_request_ended_while_it_ran_keeps_the_real_outcome()
    {
        var site = new IndefiniteSite("tests.slow");
        var seed = Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, site.Name), site.Name, 0) == Phase.After);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var captured = new Captured();
        var scope = Helpers.Inject(seed, captured);
        var call = site.RunAsync(async () =>
        {
            started.SetResult();
            await release.Task;
            return 42;
        });
        await started.Task;
        scope.Dispose(); // the request ends while call 0, drawn to fault after, is in flight
        release.SetResult();
        Assert.Equal(42, await call);
        Assert.Empty(captured.Lines);
    }

    [Fact]
    public async Task An_after_fault_whose_request_ended_while_it_ran_keeps_the_real_exception()
    {
        var site = new IndefiniteSite("tests.slow");
        var seed = Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, site.Name), site.Name, 0) == Phase.After);
        var release = new TaskCompletionSource();
        var scope = Helpers.Inject(seed);
        var call = site.RunAsync(async () =>
        {
            await release.Task;
            throw new DefiniteException();
        });
        scope.Dispose();
        release.SetResult();
        await Assert.ThrowsAsync<DefiniteException>(() => call);
    }

    [Fact]
    public async Task Concurrent_calls_report_the_fault_that_ended_the_request_first()
    {
        var slow = new IndefiniteSite("tests.slow");
        var fast = new IndefiniteSite("tests.fast");
        static bool Faults(long s, string site, Phase phase) =>
            Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, site), site, 0) == phase;
        var seed = Helpers.Seeds.First(s => Faults(s, slow.Name, Phase.After) && Faults(s, fast.Name, Phase.Before));
        var captured = new Captured();
        using var scope = Helpers.Inject(seed, captured);
        var release = new TaskCompletionSource();
        var first = slow.RunAsync(async () =>
        {
            await release.Task;
            return 1;
        });
        var second = await Assert.ThrowsAsync<IndefiniteFaultException>(() => fast.RunAsync(() => Task.FromResult(2)));
        release.SetResult();
        var late = await Assert.ThrowsAsync<IndefiniteFaultException>(() => first);
        Assert.Equal(second.Fault, late.Fault);
        Assert.Equal("tests.fast", late.Fault.Site);
        Assert.Single(captured.Lines);
    }

    [Fact]
    public void Disposing_the_scope_closes_and_restores()
    {
        var scope = Helpers.Inject(5);
        Assert.False(scope.Injection.Closed);
        scope.Dispose();
        Assert.True(scope.Injection.Closed);
        Assert.Null(Injection.Current);
        Assert.Equal(1, Site.Run(() => 1));
    }

    [Fact]
    public void An_injection_describes_its_seed_rate_and_sites()
    {
        using var scope = Helpers.Inject(3);
        Helpers.Attempt(Flavor.Func.Recorder([], "b.site"), 0);
        Helpers.Attempt(Flavor.Func.Recorder([], "a.site"), 0);
        var modes = scope.Injection.Modes();
        Assert.Equal(
            $"Injection(seed=3, rate={scope.Injection.Rate}, sites=[a.site={modes["a.site"].Wire()}, b.site={modes["b.site"].Wire()}])",
            scope.Injection.ToString());
    }

    // --- Nesting: a fault inside an operation is the request's fault ---

    [Fact]
    public void A_fault_inside_an_operation_outranks_the_outer_after()
    {
        var outer = new IndefiniteSite("tests.outer");
        var inner = new IndefiniteSite("tests.inner");
        var seed = Helpers.Seeds.First(s =>
            Schedule.ModeOf(s, outer.Name) == Mode.After && Schedule.ModeOf(s, inner.Name) == Mode.Before);
        var captured = new Captured();
        using var scope = Helpers.Inject(seed, captured);
        var error = Assert.Throws<IndefiniteFaultException>(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                outer.Run(() => inner.Run(() => i));
            }
        });
        Assert.Single(captured.Lines);
        Assert.Equal(scope.Injection.Aborted, error.Fault);
    }
}
