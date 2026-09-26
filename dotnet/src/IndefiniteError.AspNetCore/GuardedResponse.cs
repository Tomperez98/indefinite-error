using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace IndefiniteError.AspNetCore;

/// <summary>
/// The response of a seeded request, as everything inside the middleware
/// sees it. Once a fault ends the request, its status is <c>500</c>, and
/// writes to it are ignored: code that keeps running after the fault, such as
/// a cache deciding whether to store the response, sees the request as failed.
/// </summary>
internal sealed class GuardedResponse(IHttpResponseFeature inner, Injection injection) : IHttpResponseFeature
{
    private bool Lost => injection.Aborted is not null;

    public int StatusCode
    {
        get => Lost ? StatusCodes.Status500InternalServerError : inner.StatusCode;
        set
        {
            if (!Lost)
            {
                inner.StatusCode = value;
            }
        }
    }

    public string? ReasonPhrase
    {
        get => inner.ReasonPhrase;
        set => inner.ReasonPhrase = value;
    }

    public IHeaderDictionary Headers
    {
        get => inner.Headers;
        set => inner.Headers = value;
    }

    [Obsolete("Use IHttpResponseBodyFeature.Stream.")]
    public Stream Body
    {
        get => inner.Body;
        set => inner.Body = value;
    }

    public bool HasStarted => inner.HasStarted;

    public void OnStarting(Func<object, Task> callback, object state) => inner.OnStarting(callback, state);

    public void OnCompleted(Func<object, Task> callback, object state) => inner.OnCompleted(callback, state);
}
