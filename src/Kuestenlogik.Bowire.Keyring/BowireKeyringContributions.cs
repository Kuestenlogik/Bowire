// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Keyring;

/// <summary>
/// Auto-discovered service registration for the keyring package
/// (#208 Phase 5). Binds <see cref="KeyringOptions"/> from
/// <c>Bowire:Keyring</c> and registers the OS backend + resolver so both
/// the endpoint and the CLI flow resolver share one configured instance.
/// Idempotent via <c>TryAdd</c>.
/// </summary>
public sealed class BowireKeyringServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var opts = new KeyringOptions();
            config.GetSection("Bowire:Keyring").Bind(opts);
            return opts;
        });

        services.TryAddSingleton<IKeyringBackend>(sp =>
            new OsKeyringBackend(sp.GetRequiredService<KeyringOptions>().Backend));

        services.TryAddSingleton(sp => new KeyringResolver(
            sp.GetRequiredService<KeyringOptions>(),
            sp.GetRequiredService<IKeyringBackend>()));
    }
}

/// <summary>
/// Auto-discovered endpoint registration — mounts the keyring endpoints
/// into the workbench's auth-gated route group at <c>MapBowire()</c> time.
/// </summary>
public sealed class BowireKeyringEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
        => endpoints.MapBowireKeyringEndpoints(basePath);
}

/// <summary>
/// Module descriptor so the workbench shell knows the keyring source is
/// installed. The JS resolver's <c>{{keyring.*}}</c> prefetch is gated on
/// this module id showing up in <c>__BOWIRE_CONFIG__.modules</c>, so a
/// host that doesn't reference the package never issues the prefetch call.
/// </summary>
public sealed class BowireKeyringModuleContribution : IBowireModuleContribution
{
    /// <inheritdoc />
    public string Id => "keyring";
    /// <inheritdoc />
    public string DisplayName => "OS Keyring";
    /// <inheritdoc />
    public string Description
        => "Resolve {{keyring.service/account}} vars from the OS credential store — secrets never touch workspace files.";
}
