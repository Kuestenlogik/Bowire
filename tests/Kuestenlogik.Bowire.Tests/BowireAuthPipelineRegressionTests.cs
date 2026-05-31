// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Regression coverage for the pipeline-build sequence the standalone
/// <c>bowire</c> CLI runs at startup. The <c>app.UseAuthentication()</c>
/// call landed in BrowserUiHost back when the auth-provider SPI shipped,
/// guarded by a "harmless when no provider is registered" comment that
/// turned out to be false — without <c>services.AddAuthentication()</c>
/// the middleware fails to resolve <c>IAuthenticationSchemeProvider</c>
/// at <c>ApplicationBuilder.Build()</c> time, taking down every CLI
/// invocation that doesn't set <c>--auth-provider</c>.
///
/// <para>
/// The fix lives in <see cref="BowireAuthServiceCollectionExtensions.AddBowireAuth"/>,
/// which now calls <c>AddAuthentication()</c> unconditionally so the
/// scheme provider exists even when no <see cref="IBowireAuthProvider"/>
/// is selected.
/// </para>
/// </summary>
public sealed class BowireAuthPipelineRegressionTests
{
    [Fact]
    public void UseAuthentication_With_AddBowireAuth_And_No_Provider_Builds_Successfully()
    {
        // Mirrors the BrowserUiHost startup shape with no Bowire:Auth
        // section configured. Pre-fix this throw with
        // "Unable to resolve service for type IAuthenticationSchemeProvider";
        // post-fix the build succeeds and the middleware runs as a no-op.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBowireAuth(builder.Configuration);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // Forcing the pipeline to materialise — the failure path was
        // inside ApplicationBuilder.Build, which the host's startup
        // reaches as part of RunAsync. Trigger it directly so the test
        // fails fast without binding a port.
        var serverApp = (app as IApplicationBuilder).Build();
        Assert.NotNull(serverApp);
    }

    [Fact]
    public void AddBowireAuth_Registers_AuthenticationSchemeProvider_When_No_Provider_Selected()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddBowireAuth(builder.Configuration);

        var app = builder.Build();
        var schemeProvider = app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();

        Assert.NotNull(schemeProvider);
    }

    [Fact]
    public void AddBowireAuth_With_Empty_Configuration_Does_Not_Throw()
    {
        // Pre-fix this also threw via AddAuthentication being absent
        // from the chain — proving the registration is configuration-
        // independent (no Bowire:Auth section necessary).
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddBowireAuth(configuration);

        var ex = Record.Exception(act);
        Assert.Null(ex);
    }
}
