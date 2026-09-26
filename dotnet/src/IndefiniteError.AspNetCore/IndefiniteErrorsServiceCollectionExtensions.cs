using System.Text;
using IndefiniteError;
using IndefiniteError.AspNetCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers indefinite-error injection with a service collection or an <c>HttpClient</c>.</summary>
public static class IndefiniteErrorsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services <c>UseIndefiniteErrors</c> needs. Register them only
    /// when testing: any caller could fault a server that installs the middleware.
    /// </summary>
    /// <remarks>
    /// It also sets Kestrel to write the <see cref="IndefiniteErrorHeaders.Fault"/>
    /// header as UTF-8, so a site with a non-ASCII name can be named in it.
    /// </remarks>
    public static IServiceCollection AddIndefiniteErrors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new IndefiniteErrorsServices(FaultLogs.Stderr));
        services.Configure<KestrelServerOptions>(options =>
        {
            var previous = options.ResponseHeaderEncodingSelector;
            options.ResponseHeaderEncodingSelector = name =>
                string.Equals(name, IndefiniteErrorHeaders.Fault, StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : previous?.Invoke(name);
        });
        return services;
    }

    /// <summary>
    /// Runs every request this client sends as a call to the site named
    /// <paramref name="name"/>: under a seeded request, a <b>before</b> fault
    /// means it is never sent, and an <b>after</b> fault means its response is lost.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> isn't a valid site name.</exception>
    public static IHttpClientBuilder AddIndefiniteSite(this IHttpClientBuilder builder, string name) =>
        builder.AddIndefiniteSite(new IndefiniteSite(name));

    /// <summary>Runs every request this client sends as a call to <paramref name="site"/>.</summary>
    public static IHttpClientBuilder AddIndefiniteSite(this IHttpClientBuilder builder, IndefiniteSite site)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(site);
        return builder.AddHttpMessageHandler(() => new IndefiniteHttpHandler(site));
    }
}
