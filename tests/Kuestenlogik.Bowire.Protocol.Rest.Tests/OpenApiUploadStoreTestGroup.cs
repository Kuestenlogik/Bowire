// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// A user-storage root of this test class's own, for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// Since #654 an uploaded OpenAPI document is a file in the identity's slot
/// rather than an entry in a process-wide list, so <c>Clear()</c> alone no
/// longer isolates a test: without a scope it would reach into whatever
/// <c>~/.bowire</c> the machine running the suite happens to have, and leave
/// test documents behind in it.
/// </para>
/// <para>
/// Held per class rather than once for the collection, although the classes
/// here are already serialised. <see cref="BowireUserContext.Enter"/> rides an
/// <see cref="AsyncLocal{T}"/>, which reaches what flows from where it was
/// set; a collection fixture is constructed somewhere else and would be
/// relying on xunit's internal ordering for that to hold. The alternative,
/// assigning <see cref="BowireUserContext.Current"/>, replaces the
/// <em>process</em> default and would leak into the other collections of this
/// assembly running beside it. A field in the constructor is the one that is
/// true by construction.
/// </para>
/// </remarks>
internal sealed class TempUserRoot : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-openapi-" + Guid.NewGuid().ToString("N"));

    private readonly IDisposable _userScope;

    public TempUserRoot()
    {
        Directory.CreateDirectory(_root);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// xunit.v3 collection marker that serialises every test class touching
/// <see cref="OpenApiUploadStore"/>. Both <c>BowireRestProtocolTests</c>
/// and <c>OpenApiUploadStoreTests</c> add to and clear the same store,
/// so without serialisation one class's <c>Clear</c> can race
/// another class's <c>GetAll</c>/<c>Single</c> assertion.
/// </summary>
// xunit1027: collection definition classes must be public so xunit's
// reflection-based discovery can see them. CA1515 prefers internal for
// types not consumed across assemblies — disable it here, the xunit
// analyser wins.
#pragma warning disable CA1515
[CollectionDefinition(nameof(OpenApiUploadStoreTestGroup))]
public sealed class OpenApiUploadStoreTestGroup;
#pragma warning restore CA1515
