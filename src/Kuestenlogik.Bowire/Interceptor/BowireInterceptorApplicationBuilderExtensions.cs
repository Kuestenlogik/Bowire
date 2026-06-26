// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// <see cref="IApplicationBuilder"/> extensions that wire the in-process
/// HTTP interceptor (#153) into an ASP.NET host's pipeline. Pair with
/// <see cref="Kuestenlogik.Bowire.BowireServiceCollectionExtensions.AddBowire(IServiceCollection)"/>
/// and
/// <see cref="Kuestenlogik.Bowire.BowireEndpointRouteBuilderExtensions.MapBowire(IEndpointRouteBuilder, string, Action{Kuestenlogik.Bowire.BowireOptions})"/>:
/// register Bowire's services, mount the workbench, then call
/// <c>app.UseBowireInterceptor()</c> as early in the pipeline as you
/// want the interceptor to see the request (typically between
/// <c>UseRouting()</c> and <c>UseEndpoints()</c>).
/// </summary>
/// <remarks>
/// <para>
/// Phase A (this shipment) is pass-through only: every request the host
/// receives is recorded into <see cref="InterceptedFlowStore"/> with
/// method, path, headers, request body, response status, response
/// headers, response body, and end-to-end latency. The workbench's new
/// "Intercepted" rail surfaces the captures live via SSE.
/// </para>
/// <para>
/// Phase B (this shipment) wires the middleware to
/// <see cref="Kuestenlogik.Bowire.Recording.BowireRecordingSession"/>:
/// when the operator starts a Capture-mode recording in the workbench,
/// every intercepted flow auto-appends as a recording step. No client
/// changes, no proxy setup — drive the host from any client and the
/// recording fills.
/// </para>
/// <para>
/// Phase D (#308) — mock injection. <see cref="InterceptorMockStore"/>
/// rules can short-circuit the pipeline before <c>_next</c> runs: the
/// rule's response is served directly to the client. The Workbench's
/// "Mocks" sub-tab in the Intercepted rail is the CRUD surface; rules
/// can also be seeded from any captured flow via the rail's "Mock this
/// route" affordance.
/// </para>
/// <para>
/// Phase C (standalone reverse-proxy mode) remains out of scope —
/// tracked separately.
/// </para>
/// </remarks>
public static class BowireInterceptorApplicationBuilderExtensions
{
    /// <summary>
    /// Activates the Bowire HTTP interceptor on the host's pipeline.
    /// </summary>
    /// <param name="app">The application builder to mount the middleware on.</param>
    /// <param name="configure">
    /// Optional callback to customise <see cref="BowireInterceptorOptions"/>
    /// before the middleware is registered. Mutates the options the
    /// middleware will resolve through <c>IOptions&lt;…&gt;</c>; subsequent
    /// edits to the same instance take effect on the next request.
    /// </param>
    /// <returns>The same <paramref name="app"/> instance, so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Idempotent registration of the supporting services
    /// (<see cref="InterceptedFlowStore"/>,
    /// <see cref="BowireInterceptorOptions"/>) — calling
    /// <c>UseBowireInterceptor</c> twice is safe but only registers the
    /// middleware once.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddBowire();
    /// var app = builder.Build();
    ///
    /// app.UseBowireInterceptor();  // Intercept every request through this host.
    /// app.MapBowire("/bowire");    // Workbench at /bowire.
    /// // ... host's own endpoints ...
    /// app.Run();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseBowireInterceptor(
        this IApplicationBuilder app,
        Action<BowireInterceptorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Ensure the supporting services exist. Embedded hosts that called
        // AddBowire() before us already have BowireRecordingSession; the
        // interceptor's own state (flow store, options) is registered here
        // so the host doesn't have to remember a second AddSomething() call.
        var services = app.ApplicationServices;

        // The store is a process-singleton; resolve through the host's
        // root service provider (the DI container already constructed it
        // on the first AddBowireInterceptorCore call below).
        EnsureCoreServicesRegistered(app);

        if (configure is not null)
        {
            var options = services.GetRequiredService<IOptions<BowireInterceptorOptions>>().Value;
            configure(options);
        }

        return app.UseMiddleware<BowireInterceptorMiddleware>();
    }

    /// <summary>
    /// Registers the interceptor's supporting services into a host's DI
    /// container without mounting the middleware. Useful for tests that
    /// want the store + options resolvable but drive the middleware
    /// directly. Production hosts should call
    /// <see cref="UseBowireInterceptor(IApplicationBuilder, Action{BowireInterceptorOptions}?)"/>
    /// instead — it covers this internally.
    /// </summary>
    public static IServiceCollection AddBowireInterceptorCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InterceptedFlowStore>();
        // Phase D (#308) — mock-injection rule store. Free when no
        // rules are added; the middleware ref-equality-checks the store
        // before forwarding so the per-request cost is one null compare.
        services.TryAddSingleton<InterceptorMockStore>();
        services.AddOptions<BowireInterceptorOptions>();
        return services;
    }

    private static void EnsureCoreServicesRegistered(IApplicationBuilder app)
    {
        // ApplicationServices is the already-built provider — we can't
        // mutate the IServiceCollection from here. Resolving the store
        // is enough to verify someone (the host's startup) registered it
        // before mounting the middleware. If not, fail fast with a clear
        // hint pointing at the canonical AddBowire() / Add*Core() entry
        // points instead of letting ASP.NET throw a generic
        // "Unable to resolve service" error from inside Invoke.
        var store = app.ApplicationServices.GetService<InterceptedFlowStore>();
        if (store is null)
        {
            throw new InvalidOperationException(
                "Bowire interceptor: InterceptedFlowStore was not registered. " +
                "Call services.AddBowire() (which registers the store) or " +
                "services.AddBowireInterceptorCore() before app.UseBowireInterceptor().");
        }
    }
}
