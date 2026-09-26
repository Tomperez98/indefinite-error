using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace IndefiniteError;

/// <summary>
/// An operation whose outcome can be indefinite: a database commit, a call to
/// another service, a message you publish.
/// </summary>
/// <remarks>
/// <para>Declare each site once, and run the operation through it:</para>
/// <code>
/// static readonly IndefiniteSite Commit = new("db.commit");
///
/// await Commit.RunAsync(() => tx.CommitAsync(ct));
/// </code>
/// <para>
/// Outside a request that <c>UseIndefiniteErrors</c> seeded, <c>Run</c> just
/// calls the operation. Inside one, each call either passes, returning or
/// throwing what the operation did, or faults:
/// </para>
/// <list type="bullet">
/// <item><description><b>before</b>: the operation never runs, and the request ends.</description></item>
/// <item><description><b>after</b>: the operation runs to completion, its outcome is discarded, and the request ends: it happened, and nobody was told.</description></item>
/// </list>
/// <para>
/// A fault writes one line to stderr and throws an <see cref="OperationCanceledException"/>.
/// Once one fires, every later site call in the request throws again without
/// running, and the middleware answers <c>500</c> whatever the handler does
/// with the exception. An <see cref="OperationCanceledException"/> the
/// operation throws outranks an <b>after</b> fault and propagates unchanged.
/// </para>
/// <para>
/// The name goes on the fault line, and each name draws its own faults, so
/// calls to one site never shift another's. Two sites with one name share one
/// fault stream.
/// </para>
/// </remarks>
public sealed class IndefiniteSite
{
    /// <summary>Creates the site called <paramref name="name"/>.</summary>
    /// <param name="name">Non-empty and printable, with no whitespace: it goes on the fault line.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> isn't a valid site name.</exception>
    public IndefiniteSite(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (SiteName.Problem(name) is { } problem)
        {
            throw new ArgumentException($"indefinite: {problem}", nameof(name));
        }

        Name = name;
    }

    /// <summary>The site's name, as it appears on the fault line.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Runs <paramref name="operation"/>, unless the current request faults this call.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="operation"/> is an <c>async void</c> lambda: use <see cref="RunAsync(Func{Task})"/>.</exception>
    public void Run(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var injection = Injection.Current;
        if (injection is null)
        {
            operation();
            return;
        }

        if (operation.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
        {
            throw new InvalidOperationException(
                $"indefinite: {Name} was given an async void lambda, so the operation hasn't happened when it returns. Use RunAsync.");
        }

        _ = Seeded<bool>(injection, () =>
        {
            operation();
            return true;
        }, discard: null);
    }

    /// <summary>Runs <paramref name="operation"/> and returns its result, unless the current request faults this call.</summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is a <see cref="Task"/> or another deferred result, so the
    /// operation hasn't happened when it returns: use <see cref="RunAsync{T}(Func{Task{T}})"/>.
    /// </exception>
    public T Run<T>(Func<T> operation) => Run(operation, discard: null);

    /// <summary>Runs <paramref name="operation"/>, unless the current request faults this call.</summary>
    public Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var injection = Injection.Current;
        if (injection is null)
        {
            return operation();
        }

        var draw = injection.Next(Name);
        return draw.Kind == DrawKind.Pass ? operation() : SeededAsync(injection, draw, async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, discard: null);
    }

    /// <summary>Runs <paramref name="operation"/> and returns its result, unless the current request faults this call.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is itself a deferred result, such as a <see cref="Task"/>.</exception>
    public Task<T> RunAsync<T>(Func<Task<T>> operation) => RunAsync(operation, discard: null);

    /// <summary>
    /// <see cref="Run{T}(Func{T})"/>, calling <paramref name="discard"/> on a
    /// result an <b>after</b> fault throws away: a response nobody will read.
    /// </summary>
    internal T Run<T>(Func<T> operation, Action<T>? discard)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Deferred<T>.ThrowIfDeferred(Name, "Use RunAsync.");
        var injection = Injection.Current;
        return injection is null ? operation() : Seeded(injection, operation, discard);
    }

    internal Task<T> RunAsync<T>(Func<Task<T>> operation, Action<T>? discard)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Deferred<T>.ThrowIfDeferred(Name, "Await it inside the operation.");
        var injection = Injection.Current;
        if (injection is null)
        {
            return operation();
        }

        var draw = injection.Next(Name);
        return draw.Kind == DrawKind.Pass ? operation() : SeededAsync(injection, draw, operation, discard);
    }

    private T Seeded<T>(Injection injection, Func<T> operation, Action<T>? discard)
    {
        var draw = injection.Next(Name);
        switch (draw.Kind)
        {
            case DrawKind.Pass:
                return operation();
            case DrawKind.Over:
                throw injection.Over(draw.Fault!);
        }

        var fault = draw.Fault!;
        if (fault.Phase == Phase.Before)
        {
            throw injection.Over(fault); // Next has ended the request
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        try
        {
            result = operation();
        }
        catch (Exception e) when (e is not OperationCanceledException) // cancellation outranks the fault
        {
            failure = ExceptionDispatchInfo.Capture(e); // it ran and threw: that outcome is what gets lost
        }

        return Lose(injection, fault, result, failure, discard);
    }

    private static async Task<T> SeededAsync<T>(Injection injection, Draw draw, Func<Task<T>> operation, Action<T>? discard)
    {
        var fault = draw.Fault!;
        if (draw.Kind == DrawKind.Over || fault.Phase == Phase.Before)
        {
            throw injection.Over(fault); // a before fault: Next has ended the request
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        try
        {
            result = await operation().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException) // cancellation outranks the fault
        {
            failure = ExceptionDispatchInfo.Capture(e); // it ran and threw: that outcome is what gets lost
        }

        return Lose(injection, fault, result, failure, discard);
    }

    /// <summary>An <b>after</b> fault: the operation ran, and now its outcome is lost.</summary>
    private static T Lose<T>(Injection injection, Fault fault, T result, ExceptionDispatchInfo? failure, Action<T>? discard)
    {
        if (injection.Abort(fault) is not { } after)
        {
            // The request ended while the operation ran: nobody is left to fault.
            failure?.Throw();
            return result;
        }

        if (failure is null)
        {
            discard?.Invoke(result);
        }

        throw after;
    }

    /// <summary>
    /// A result that isn't there yet when the operation returns: the operation
    /// hasn't happened, so an <b>after</b> fault would lie.
    /// </summary>
    private static class Deferred<T>
    {
        private static readonly string? Kind = KindOf(typeof(T));

        public static void ThrowIfDeferred(string site, string advice)
        {
            if (Kind is not null)
            {
                throw new InvalidOperationException(
                    $"indefinite: {site} returns {Kind}, so the operation hasn't happened when it returns. {advice}");
            }
        }

        private static string? KindOf(Type type)
        {
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
            if (typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask) || definition == typeof(ValueTask<>))
            {
                return $"an awaitable {type.Name}";
            }

            if (typeof(System.Linq.IQueryable).IsAssignableFrom(type))
            {
                return $"a query ({type.Name}); materialize it inside the operation";
            }

            return definition == typeof(IAsyncEnumerable<>)
                ? $"an async stream ({type.Name}); materialize it inside the operation"
                : null;
        }
    }
}
