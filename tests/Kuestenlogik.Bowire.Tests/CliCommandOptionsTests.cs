// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Smoke tests for the four CLI option POCOs (CliCommandOptions /
/// MockCliOptions / PluginCliOptions / TestCliOptions). Each is a
/// passive container — the value comes from defaults + property setters
/// that the System.CommandLine binder fills in. Guarding the defaults
/// here means a refactor that drops a default (e.g. switching MockPort
/// from 6000 to 0) shows up here long before it ships.
/// </summary>
public sealed class CliCommandOptionsTests
{
    [Fact]
    public void CliCommandOptions_Defaults_MatchDocumentedValues()
    {
        var o = new CliCommandOptions();

        Assert.Equal("https://localhost:5001", o.Url);
        Assert.False(o.Plaintext);
        Assert.False(o.Verbose);
        Assert.False(o.Compact);
        Assert.Null(o.Target);
        Assert.NotNull(o.Data);
        Assert.Empty(o.Data);
        Assert.NotNull(o.Headers);
        Assert.Empty(o.Headers);
    }

    [Fact]
    public void CliCommandOptions_Setters_RoundTrip()
    {
        var o = new CliCommandOptions
        {
            Url = "http://api",
            Plaintext = true,
            Verbose = true,
            Compact = true,
            Target = "users.UserService/Get",
        };
        o.Data.Add("{\"id\":1}");
        o.Data.Add("@payload.json");
        o.Headers.Add("authorization: bearer x");

        Assert.Equal("http://api", o.Url);
        Assert.True(o.Plaintext);
        Assert.True(o.Verbose);
        Assert.True(o.Compact);
        Assert.Equal("users.UserService/Get", o.Target);
        Assert.Equal(2, o.Data.Count);
        Assert.Single(o.Headers);
    }

    [Fact]
    public void MockCliOptions_Defaults_MatchDocumentedValues()
    {
        var o = new MockCliOptions();

        Assert.Null(o.RecordingPath);
        Assert.Null(o.SchemaPath);
        Assert.Null(o.GrpcSchemaPath);
        Assert.Null(o.GraphQlSchemaPath);
        Assert.Equal("127.0.0.1", o.Host);
        Assert.Equal(6000, o.Port);
        Assert.Null(o.Select);
        Assert.Null(o.Chaos);
        Assert.Null(o.CaptureMissPath);
        Assert.False(o.NoWatch);
        Assert.False(o.Stateful);
        Assert.False(o.StatefulOnce);
        Assert.Equal(1.0, o.ReplaySpeed);
        Assert.Null(o.ControlToken);
        Assert.False(o.Loop);
        Assert.False(o.AutoInstall);
    }

    [Fact]
    public void MockCliOptions_Setters_RoundTrip()
    {
        var o = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
            GrpcSchemaPath = "fds.pb",
            GraphQlSchemaPath = "schema.graphql",
            Host = "0.0.0.0",
            Port = 7777,
            Select = "scenario-A",
            Chaos = "latency:50-200",
            CaptureMissPath = "miss.log",
            NoWatch = true,
            Stateful = true,
            StatefulOnce = true,
            ReplaySpeed = 2.5,
            ControlToken = "secret",
            Loop = true,
            AutoInstall = true,
        };

        Assert.Equal("rec.json", o.RecordingPath);
        Assert.Equal("openapi.yml", o.SchemaPath);
        Assert.Equal("fds.pb", o.GrpcSchemaPath);
        Assert.Equal("schema.graphql", o.GraphQlSchemaPath);
        Assert.Equal("0.0.0.0", o.Host);
        Assert.Equal(7777, o.Port);
        Assert.Equal("scenario-A", o.Select);
        Assert.Equal("latency:50-200", o.Chaos);
        Assert.Equal("miss.log", o.CaptureMissPath);
        Assert.True(o.NoWatch);
        Assert.True(o.Stateful);
        Assert.True(o.StatefulOnce);
        Assert.Equal(2.5, o.ReplaySpeed);
        Assert.Equal("secret", o.ControlToken);
        Assert.True(o.Loop);
        Assert.True(o.AutoInstall);
    }

    [Fact]
    public void PluginCliOptions_Defaults_MatchDocumentedValues()
    {
        var o = new PluginCliOptions();

        Assert.Equal("", o.Command);
        Assert.Equal("", o.PackageId);
        Assert.Null(o.Version);
        Assert.Null(o.File);
        Assert.Null(o.OutputDir);
        Assert.NotNull(o.Sources);
        Assert.Empty(o.Sources);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void PluginCliOptions_Setters_RoundTrip()
    {
        var o = new PluginCliOptions
        {
            Command = "install",
            PackageId = "MyCo.Plugin",
            Version = "1.2.3",
            File = "./local.nupkg",
            OutputDir = "./bundle",
            Verbose = true,
        };
        o.Sources.Add("https://api.nuget.org/v3/index.json");
        o.Sources.Add("./local-feed/");

        Assert.Equal("install", o.Command);
        Assert.Equal("MyCo.Plugin", o.PackageId);
        Assert.Equal("1.2.3", o.Version);
        Assert.Equal("./local.nupkg", o.File);
        Assert.Equal("./bundle", o.OutputDir);
        Assert.Equal(2, o.Sources.Count);
        Assert.True(o.Verbose);
    }

    [Fact]
    public void TestCliOptions_Defaults_MatchDocumentedValues()
    {
        var o = new TestCliOptions();

        Assert.Null(o.CollectionPath);
        Assert.Null(o.ReportPath);
        Assert.Null(o.JUnitPath);
    }

    [Fact]
    public void TestCliOptions_Setters_RoundTrip()
    {
        var o = new TestCliOptions
        {
            CollectionPath = "tests.json",
            ReportPath = "report.html",
            JUnitPath = "junit.xml",
        };

        Assert.Equal("tests.json", o.CollectionPath);
        Assert.Equal("report.html", o.ReportPath);
        Assert.Equal("junit.xml", o.JUnitPath);
    }
}
