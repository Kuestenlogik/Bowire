// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Projects;

/// <summary>
/// Static entry point to the active <see cref="IBowirePathResolver"/> (#616).
/// </summary>
/// <remarks>
/// <para>
/// Injecting the resolver is the better shape and is what anything with a
/// constructor should do. This exists for the call sites that have no
/// constructor to inject into — static properties read during type
/// initialisation, before any host is built. Without it those keep their own
/// copy of the path logic, which is the thing #616 is about.
/// </para>
/// <para>
/// It mirrors <see cref="Auth.BowireUserContext"/> deliberately: same shape,
/// same swap-at-startup story, so there is one pattern to learn rather than
/// two.
/// </para>
/// </remarks>
public static class BowirePaths
{
    private static IBowirePathResolver _current = new BowirePathResolver();

    /// <summary>
    /// The resolver everything static goes through. A host replaces it at
    /// start-up; tests replace it to redirect a whole tree.
    /// </summary>
    public static IBowirePathResolver Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Resolve under the active resolver. See <see cref="IBowirePathResolver.Resolve"/>.</summary>
    public static string Resolve(BowireStorageScope scope, params string[] segments)
        => _current.Resolve(scope, segments);

    /// <summary>The root for <paramref name="scope"/>, with no segments added.</summary>
    public static string Root(BowireStorageScope scope) => _current.Root(scope);
}
