using System.Globalization;

namespace IndefiniteError;

/// <summary>One injected fault: the <see cref="N"/>-th call to <see cref="Site"/> in the request seeded <see cref="Seed"/>.</summary>
internal sealed record Fault(long Seed, string Site, int N, Phase Phase)
{
    /// <summary>The fault line's payload: <c>after app.get#4 (seed=13)</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Phase.Wire()} {Site}#{N} (seed={Seed})");
}

/// <summary>
/// What a faulted call throws. It ends the request: the middleware answers
/// for it, whether it propagates or not.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no exception a <c>catch (Exception)</c> can't see, so instead of
/// hiding the fault the injection remembers it: once one fires, every later
/// marked call in the request throws again without running, and the
/// middleware replaces the request's response with the fault's.
/// </para>
/// <para>
/// It is an <see cref="OperationCanceledException"/>, because a faulted
/// request is a cancelled one. Retry policies (Polly's by default) and the
/// usual <c>catch (Exception e) when (e is not OperationCanceledException)</c>
/// let it through, and its <see cref="OperationCanceledException.CancellationToken"/>
/// is the one the middleware links into <c>HttpContext.RequestAborted</c>.
/// </para>
/// <para>Internal: nothing outside this package can name it to catch it.</para>
/// </remarks>
internal sealed class IndefiniteFaultException : OperationCanceledException
{
    public IndefiniteFaultException(Fault fault, CancellationToken token)
        : base($"indefinite-error: {fault}: this request has ended; let the exception reach UseIndefiniteErrors", token)
    {
        Fault = fault;
    }

    public Fault Fault { get; }
}
