// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Clears the two environment variables that make <c>BrowserUiHost</c>
/// suppress its browser launch, and puts them back on dispose.
/// </summary>
/// <remarks>
/// <para>
/// <c>RunAsync</c> refuses to launch a browser when <c>CI</c> or
/// <c>DOTNET_RUNNING_IN_CONTAINER</c> is set, or when the process is not
/// user-interactive — all true on a build runner, none true on a developer's
/// machine. A test that asserts the opened URL without clearing them passes
/// locally and fails on every runner, which is what #684's adopt-the-running-
/// instance test did: two other tests carried their own copy of the
/// save/clear/restore dance, the third shipped without one.
/// </para>
/// <para>
/// <see cref="LaunchExpected"/> carries the half no environment variable can
/// override: a headless runner reports <c>UserInteractive == false</c> and
/// suppresses the launch on its own, so a test that wants to assert the URL
/// has to ask first and settle for the exit code otherwise.
/// </para>
/// <para>
/// The environment is process-global, so this is only safe inside
/// <see cref="BrowserUiHostTests.CollectionName"/>, whose members run
/// serially — the same reason the seams themselves need that collection.
/// </para>
/// </remarks>
internal sealed class BrowserLaunchEnvironment : IDisposable
{
    private const string CiVariable = "CI";
    private const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    private readonly string? _ci;
    private readonly string? _container;

    private BrowserLaunchEnvironment()
    {
        _ci = Environment.GetEnvironmentVariable(CiVariable);
        _container = Environment.GetEnvironmentVariable(ContainerVariable);
        Environment.SetEnvironmentVariable(CiVariable, null);
        Environment.SetEnvironmentVariable(ContainerVariable, null);
    }

    /// <summary>
    /// Lets the browser branch be reached for the lifetime of the returned
    /// scope, wherever the test happens to run.
    /// </summary>
    public static BrowserLaunchEnvironment Allow() => new();

    /// <summary>
    /// Whether the runtime would let a browser be spawned at all. False on a
    /// headless runner, where only the exit code is assertable.
    /// </summary>
    public static bool LaunchExpected => Environment.UserInteractive;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CiVariable, _ci);
        Environment.SetEnvironmentVariable(ContainerVariable, _container);
    }
}
