// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A user-storage root of this test class's own, for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// For any suite that touches a disk-backed store — since #654 that includes
/// an uploaded <c>.proto</c> or OpenAPI document, which used to be a
/// process-wide list. Without a root of its own such a test writes into
/// whatever <c>~/.bowire</c> the machine running the suite has, and leaves
/// its fixtures behind there.
/// </para>
/// <para>
/// This scopes rather than assigning <see cref="BowireUserContext.Current"/>,
/// which several suites here do: that replaces the <em>process</em> default
/// and is why those suites had to be serialised into one collection. A scope
/// reaches only what flows from it, so a class holding one stays parallel
/// with the rest.
/// </para>
/// <para>
/// The catch, and the reason this type carries a comment at all: a scope is
/// an <see cref="AsyncLocal{T}"/>, and <c>TestServer</c> drops the caller's
/// execution context unless <c>PreserveExecutionContext</c> is set. A suite
/// whose store is touched inside a request handler must set that on its host,
/// or the handler quietly resolves against the real user directory instead.
/// <see cref="BowireTestFixture"/> sets it.
/// </para>
/// </remarks>
internal sealed class TempUserRoot : IDisposable
{
    private readonly string _root;
    private readonly IDisposable _scope;

    public TempUserRoot(string label)
    {
        _root = Path.Combine(Path.GetTempPath(), $"bowire-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _scope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
