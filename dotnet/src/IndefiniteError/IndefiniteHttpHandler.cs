namespace IndefiniteError;

/// <summary>
/// Runs every request an <see cref="HttpClient"/> sends as a call to one
/// <see cref="IndefiniteSite"/>: an outgoing call whose outcome can get lost.
/// </summary>
/// <remarks>
/// <para>
/// Under a seeded request, a <b>before</b> fault means the request is never
/// sent; an <b>after</b> fault means it was sent and answered, and the
/// response is disposed unread. With <c>IHttpClientFactory</c>, add it with
/// <c>AddIndefiniteSite</c> from <c>IndefiniteError.AspNetCore</c>:
/// </para>
/// <code>
/// services.AddHttpClient&lt;PaymentsClient&gt;().AddIndefiniteSite("payments.charge");
/// </code>
/// </remarks>
public sealed class IndefiniteHttpHandler : DelegatingHandler
{
    /// <summary>A handler that runs each request as a call to <paramref name="site"/>.</summary>
    public IndefiniteHttpHandler(IndefiniteSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        Site = site;
    }

    /// <summary>A handler that runs each request as a call to <paramref name="site"/>, then sends it with <paramref name="innerHandler"/>.</summary>
    public IndefiniteHttpHandler(IndefiniteSite site, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(site);
        Site = site;
    }

    /// <summary>The site every request is a call to.</summary>
    public IndefiniteSite Site { get; }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Site.RunAsync(() => base.SendAsync(request, cancellationToken), discard: static response => response.Dispose());

    /// <inheritdoc/>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Site.Run(() => base.Send(request, cancellationToken), discard: static response => response.Dispose());
}
