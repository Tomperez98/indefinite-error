namespace IndefiniteError;

/// <summary>The HTTP headers the middleware reads and writes.</summary>
public static class IndefiniteErrorHeaders
{
    /// <summary>
    /// Carries a request's seed: exactly one decimal int64, such as <c>13</c>
    /// or <c>-7</c>. A malformed, out-of-range, or repeated value gets a <c>400</c>.
    /// </summary>
    public const string Seed = "X-Indefinite-Seed";

    /// <summary>
    /// Names the fault that ended a request: <c>after db.commit#0 (seed=13)</c>.
    /// It is for debugging; a test's assertions must not read it.
    /// </summary>
    public const string Fault = "X-Indefinite-Fault";
}
