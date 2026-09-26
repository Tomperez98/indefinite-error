using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace IndefiniteError.AspNetCore;

/// <summary>What <c>AddIndefiniteErrors</c> registers: where fault lines go.</summary>
internal sealed class IndefiniteErrorsServices(FaultLog log)
{
    public FaultLog Log { get; } = log;
}

/// <summary>
/// Runs each request carrying <see cref="IndefiniteErrorHeaders.Seed"/> inside
/// its own injection, driven by that seed.
/// </summary>
/// <remarks>
/// <para>
/// Requests without the header pass through untouched; a malformed seed gets a
/// <c>400</c>, and the app never sees the request.
/// </para>
/// <para>
/// Once a fault fires, the request is over: <c>HttpContext.RequestAborted</c>
/// is cancelled, the rest of the response is swallowed, and the middleware
/// answers <c>500</c> with an empty body and an <see cref="IndefiniteErrorHeaders.Fault"/>
/// header, whatever the app threw or returned. If the response had already
/// started, it aborts the connection instead, and the client sees it drop
/// mid-response.
/// </para>
/// </remarks>
internal sealed class IndefiniteErrorsMiddleware(RequestDelegate next, IndefiniteErrorsServices services)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(IndefiniteErrorHeaders.Seed, out var values))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        if (!SeedHeader.TryParse(values, out var seed))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            response.ContentType = "text/plain";
            await response.WriteAsync($"{IndefiniteErrorHeaders.Seed} must be a decimal int64", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var outer = response.Headers.ToArray(); // what the middleware outside us set
        var body = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var head = context.Features.GetRequiredFeature<IHttpResponseFeature>();
        var requestAborted = context.RequestAborted;

        var scope = Injection.Enter(seed, services.Log);
        var injection = scope.Injection;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, injection.Ended);
        context.RequestAborted = lifetime.Token;
        context.Features.Set<IHttpResponseBodyFeature>(new GuardedResponseBody(body, injection));
        context.Features.Set<IHttpResponseFeature>(new GuardedResponse(head, injection));

        // Starting callbacks run newest first, so this one runs after every
        // callback the lost request registered, and undoes what they set.
        response.OnStarting(() =>
        {
            if (injection.Aborted is { } lost)
            {
                Answer(response, outer, lost);
            }

            return Task.CompletedTask;
        });
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception) when (injection.Aborted is not null)
        {
            // The fault ended the request, whatever else went wrong after it.
        }
        finally
        {
            scope.Dispose();
            context.Features.Set(body);
            context.Features.Set(head);
            context.RequestAborted = requestAborted;
        }

        if (injection.Aborted is not { } fault)
        {
            return;
        }

        if (response.HasStarted)
        {
            context.Abort(); // too late to answer: drop the connection mid-response
            return;
        }

        response.Clear();
        Answer(response, outer, fault);
    }

    /// <summary>A fresh response for the fault: nothing the lost request set survives it.</summary>
    private static void Answer(HttpResponse response, KeyValuePair<string, StringValues>[] outer, Fault fault)
    {
        response.Headers.Clear();
        foreach (var (name, value) in outer)
        {
            response.Headers[name] = value;
        }

        response.StatusCode = StatusCodes.Status500InternalServerError;
        response.Headers[IndefiniteErrorHeaders.Fault] = fault.ToString();
        response.ContentLength = 0;
    }
}
