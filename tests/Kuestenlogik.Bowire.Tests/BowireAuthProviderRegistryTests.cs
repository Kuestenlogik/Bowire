// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="BowireAuthProviderRegistry"/> — the assembly-scan
/// registry that maps <c>--auth-provider &lt;id&gt;</c> CLI values onto
/// concrete <see cref="IBowireAuthProvider"/> implementations.
/// </summary>
/// <remarks>
/// The registry walks every loaded <c>Kuestenlogik.Bowire*</c> assembly
/// for non-abstract types that implement <see cref="IBowireAuthProvider"/>.
/// This test assembly's name starts with <c>Kuestenlogik.Bowire</c>, so
/// the <see cref="TestStubAuthProvider"/> below is discoverable without
/// loading any real plugin.
/// </remarks>
public sealed class BowireAuthProviderRegistryTests
{
    [Fact]
    public void Discover_Finds_Test_Stub_Provider()
    {
        var providers = BowireAuthProviderRegistry.Discover();

        Assert.Contains(TestStubAuthProvider.IdConst, providers.Keys);
        Assert.IsType<TestStubAuthProvider>(providers[TestStubAuthProvider.IdConst]);
    }

    [Fact]
    public void Discover_Uses_Case_Insensitive_Keys()
    {
        var providers = BowireAuthProviderRegistry.Discover();
        Assert.True(providers.ContainsKey(TestStubAuthProvider.IdConst.ToUpperInvariant()));
    }

    [Fact]
    public void ApplyAuthentication_Returns_Null_For_Empty_ProviderId()
    {
        // Laptop default — no provider configured, no auth wired.
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().Build();
        var opts = new BowireAuthOptions { ProviderId = null };

        var result = BowireAuthProviderRegistry.ApplyAuthentication(services, cfg, opts);

        Assert.Null(result);
    }

    [Fact]
    public void ApplyAuthentication_Throws_For_Unknown_ProviderId()
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().Build();
        var opts = new BowireAuthOptions { ProviderId = "does-not-exist" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BowireAuthProviderRegistry.ApplyAuthentication(services, cfg, opts));

        // Error message names the offending id + lists what's available
        // so the operator can spot a typo without going to the docs.
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("plugin install", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAuthentication_Wires_Selected_Provider()
    {
        TestStubAuthProvider.AddAuthCalled = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();
        var opts = new BowireAuthOptions { ProviderId = TestStubAuthProvider.IdConst };

        var result = BowireAuthProviderRegistry.ApplyAuthentication(services, cfg, opts);

        Assert.NotNull(result);
        Assert.Equal(TestStubAuthProvider.IdConst, result!.Id);
        Assert.Equal(1, TestStubAuthProvider.AddAuthCalled);

        // The provider should land as a singleton so the rest of the
        // pipeline (UseBowireAuth middleware, &c.) can resolve it.
        var registered = services.BuildServiceProvider().GetService<IBowireAuthProvider>();
        Assert.Same(result, registered);
    }

    [Fact]
    public async Task ApplyAuthentication_Registers_Default_Policy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();
        var opts = new BowireAuthOptions { ProviderId = TestStubAuthProvider.IdConst };

        BowireAuthProviderRegistry.ApplyAuthentication(services, cfg, opts);

        using var sp = services.BuildServiceProvider();
        var policyProvider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(BowireAuthPolicies.Default);
        Assert.NotNull(policy);
    }
}

/// <summary>
/// Test-only auth provider so the assembly-scan path discovers
/// something deterministic in unit tests. Internal because the scan
/// reflects over internal types in the same assembly just fine, and
/// keeping it internal avoids polluting the cross-project surface
/// (CA1515) and the nested-public-type rule (CA1034).
/// </summary>
internal sealed class TestStubAuthProvider : IBowireAuthProvider
{
    internal const string IdConst = "test-stub";

    private static int s_addAuthCalled;

    internal static int AddAuthCalled
    {
        get => Volatile.Read(ref s_addAuthCalled);
        set => Volatile.Write(ref s_addAuthCalled, value);
    }

    public string Id => IdConst;
    public string Name => "Test Stub Provider";

    public void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        Interlocked.Increment(ref s_addAuthCalled);
        // Minimal authentication wiring so AddAuthorization downstream
        // has the dependency graph it expects.
        services.AddAuthentication("Test").AddCookie("Test");
    }
}
