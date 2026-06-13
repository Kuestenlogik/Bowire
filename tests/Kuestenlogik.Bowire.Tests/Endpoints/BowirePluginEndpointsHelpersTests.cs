// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Direct coverage for the BowirePluginEndpoints internal helpers
/// (<c>SafeIdentifier</c> + <c>IsBowirePluginAssemblyName</c>) that
/// the new lifecycle endpoints depend on. The endpoints themselves
/// shell out to the <c>bowire</c> CLI via <see cref="System.Diagnostics.ProcessStartInfo"/>
/// — unit-testing that path would mean mocking out the child-process
/// boundary, which costs more than it returns. The helpers below are
/// pure-function-shaped and carry the actual policy (which identifiers
/// are accepted, which assembly names count as bundled), so pinning
/// them is where the real value sits.
/// </summary>
public sealed class BowirePluginEndpointsHelpersTests
{
    // Pull the internal members via reflection on the test assembly's
    // InternalsVisibleTo grant — the field is a static readonly Regex,
    // the method is a static bool function.
    private static readonly System.Text.RegularExpressions.Regex SafeIdentifier =
        (System.Text.RegularExpressions.Regex)typeof(BowirePluginEndpoints)
            .GetField("SafeIdentifier", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static bool IsBowirePluginAssemblyName(string name) =>
        (bool)typeof(BowirePluginEndpoints)
            .GetMethod("IsBowirePluginAssemblyName", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [name])!;

    // -------------------- SafeIdentifier whitelist --------------------

    [Theory]
    [InlineData("Kuestenlogik.Bowire.Protocol.Amqp")]
    [InlineData("Kuestenlogik.Bowire.Tool")]
    [InlineData("Bowire.Plugin1")]
    [InlineData("X")]
    [InlineData("X1")]
    [InlineData("a.b-c_d")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.5.0-rc.2+abc123")]
    public void SafeIdentifier_accepts_NuGet_shaped_identifiers(string s)
    {
        Assert.True(SafeIdentifier.IsMatch(s),
            $"expected '{s}' to be accepted by the whitelist");
    }

    [Theory]
    // Shell metacharacters that would let an attacker break out of the
    // child-process argv if String.Format-style interpolation were used.
    [InlineData("foo; rm -rf /")]
    [InlineData("foo && bad")]
    [InlineData("foo | nc")]
    [InlineData("foo`bad`")]
    [InlineData("foo$(bad)")]
    // Path traversal — must not reach the bowire CLI as a package id.
    [InlineData("../foo")]
    [InlineData("/etc/passwd")]
    [InlineData("foo\\bar")]
    // Whitespace and quotes.
    [InlineData("foo bar")]
    [InlineData("foo\"bar")]
    [InlineData("foo'bar")]
    // CLI flag injection — '--source evil.com/feed' would steer
    // restore to a hostile feed.
    [InlineData("--prerelease")]
    [InlineData("-v")]
    // Leading non-alphanumeric is rejected on principle.
    [InlineData(".hidden")]
    [InlineData("_underscore-first")]
    [InlineData("-dash-first")]
    [InlineData("")]
    public void SafeIdentifier_rejects_unsafe_input(string s)
    {
        Assert.False(SafeIdentifier.IsMatch(s),
            $"expected '{s}' to be rejected by the whitelist");
    }

    // -------------------- IsBowirePluginAssemblyName --------------------

    [Theory]
    [InlineData("Kuestenlogik.Bowire.Protocol.Grpc", true)]
    [InlineData("Kuestenlogik.Bowire.Protocol.Amqp", true)]
    [InlineData("Kuestenlogik.Bowire.Protocol.TacticalApi", true)]
    [InlineData("Kuestenlogik.Bowire.Extension.MapLibre", true)] // legacy .Extension.* prefix still recognised
    [InlineData("Kuestenlogik.Bowire.Map", true)]                 // v2.0 rename
    [InlineData("Kuestenlogik.Bowire.Ai", true)]
    [InlineData("Kuestenlogik.Bowire.Help", true)]
    [InlineData("Kuestenlogik.Bowire.Telemetry", true)]
    [InlineData("Kuestenlogik.Bowire.AsyncApi", true)]
    [InlineData("Kuestenlogik.Bowire.Mcp", true)]
    [InlineData("Kuestenlogik.Bowire.Mock", true)]
    [InlineData("Kuestenlogik.Bowire.Security.Scanner", true)]
    // Core itself is not a plugin (it's the host)
    [InlineData("Kuestenlogik.Bowire", false)]
    // Tool is not a plugin (it's the CLI host)
    [InlineData("Kuestenlogik.Bowire.Tool", false)]
    // Random third-party assembly
    [InlineData("System.Text.Json", false)]
    [InlineData("RabbitMQ.Client", false)]
    public void IsBowirePluginAssemblyName_recognises_first_party_plugin_assemblies(string name, bool expected)
    {
        Assert.Equal(expected, IsBowirePluginAssemblyName(name));
    }
}
