using IndefiniteError;
using IndefiniteError.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Installs the indefinite-error middleware.</summary>
public static class IndefiniteErrorsApplicationBuilderExtensions
{
    /// <summary>
    /// Runs each request carrying <see cref="IndefiniteErrorHeaders.Seed"/>
    /// inside its own injection: its <see cref="IndefiniteSite"/> calls may
    /// fault, and a faulted request gets a <c>500</c> naming the fault in
    /// <see cref="IndefiniteErrorHeaders.Fault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Install it early, before routing and endpoints, behind a flag that is
    /// off in production: any caller could fault your server. Requests without
    /// the header pass through untouched; a malformed seed gets a <c>400</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>AddIndefiniteErrors</c> wasn't called.</exception>
    public static IApplicationBuilder UseIndefiniteErrors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService(typeof(IndefiniteErrorsServices)) is null)
        {
            throw new InvalidOperationException(
                "Unable to find the required services. Add them by calling 'IServiceCollection.AddIndefiniteErrors' in the application startup code.");
        }

        return app.UseMiddleware<IndefiniteErrorsMiddleware>();
    }
}
