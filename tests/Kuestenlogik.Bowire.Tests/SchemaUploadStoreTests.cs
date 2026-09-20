// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <see cref="SchemaUploadStore"/> — where an uploaded schema lives (#654).
/// </summary>
/// <remarks>
/// The store replaced two process-wide static lists, and the four things
/// wrong with those are what this pins: an upload has to survive a restart,
/// stay inside its identity, stay inside its workspace, and be a file the
/// CLI can reach. Only the first of those was a nuisance; the second was a
/// leak, because on a multi-user host one static meant one person's internal
/// service shape was published to everybody on the box.
/// </remarks>
public sealed class SchemaUploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-schemas-" + Guid.NewGuid().ToString("N"));

    private readonly IDisposable _userScope;

    public SchemaUploadStoreTests()
    {
        Directory.CreateDirectory(_root);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private const string Proto = """
        syntax = "proto3";
        service Greeter { rpc Hello (Req) returns (Res); }
        """;

    // ---- it is a file, and that is the point ----

    [Fact]
    public void An_Upload_Lands_As_A_File_Under_Its_Own_Name()
    {
        // The whole reason for a directory instead of one JSON envelope: in a
        // git-native workspace this is what a reviewer sees in the diff.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "routeguide.proto");

        var file = Path.Combine(SchemaUploadStore.RootPath(), "routeguide.proto");
        Assert.True(File.Exists(file));
        Assert.Equal(Proto, File.ReadAllText(file));
    }

    [Fact]
    public void A_Document_Survives_Anything_That_Forgets_Memory()
    {
        // There is no state to reset here — the store holds none. Reading it
        // back is reading the disk, which is what a restart leaves behind.
        var id = SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, "{\"openapi\":\"3.0.0\"}", "petstore.json");

        var stored = Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
        Assert.Equal(id, stored.Id);
        Assert.Equal("petstore.json", stored.SourceName);
        Assert.Contains("openapi", stored.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Name_With_No_Extension_Gets_The_One_Its_Kind_Implies()
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "greeter");
        Assert.True(File.Exists(Path.Combine(SchemaUploadStore.RootPath(), "greeter.proto")));
    }

    [Fact]
    public void Two_Uploads_Of_The_Same_Name_Do_Not_Overwrite_Each_Other()
    {
        // Uploading petstore.json twice is an ordinary thing to do, and the
        // second one silently replacing the first would lose a document the
        // index still lists.
        SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, """{"v":1}""", "petstore.json");
        SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, """{"v":2}""", "petstore.json");

        var all = SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind);
        Assert.Equal(2, all.Count);
        Assert.Equal("""{"v":1}""", all[0].Content);
        Assert.Equal("""{"v":2}""", all[1].Content);
    }

    // ---- the leak this ticket is really about ----

    [Fact]
    public void One_Identity_Cannot_See_Another_Identitys_Upload()
    {
        // The multi-user host: whoever uploaded the spec for an internal
        // service had published its shape to every other identity on the box.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "internal.proto");

        var otherRoot = Path.Combine(Path.GetTempPath(), "bowire-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherRoot);
        try
        {
            using (BowireUserContext.Enter(new DefaultBowireUserStore(otherRoot)))
            {
                Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
                Assert.False(SchemaUploadStore.Has(SchemaUploadStore.ProtoKind));
            }

            // And the first identity still has it — the second one looking is
            // not what removes it.
            Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        }
        finally
        {
            try { Directory.Delete(otherRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Two_Workspaces_Pointed_At_Different_Services_Do_Not_Share_A_Pile()
    {
        using (BowirePluginSettingsScope.Enter("ws_lab"))
            SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "lab.proto");

        using (BowirePluginSettingsScope.Enter("ws_production"))
        {
            Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
            SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "production.proto");
        }

        using (BowirePluginSettingsScope.Enter("ws_lab"))
        {
            var lab = Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
            Assert.Equal("lab.proto", lab.SourceName);
        }

        // And the workspace-less caller — the CLI, a host that predates
        // workspaces — sees neither rather than a merge of both.
        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
    }

    // ---- the two kinds share a directory ----

    [Fact]
    public void Each_Kind_Only_Sees_Its_Own()
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "a.proto");
        SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, "{}", "b.json");

        Assert.Equal("a.proto", Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind)).SourceName);
        Assert.Equal("b.json", Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind)).SourceName);
    }

    [Fact]
    public void Clearing_One_Kind_Leaves_The_Other_Standing()
    {
        // "Clear the uploaded protos" is a button in the sidebar, and the two
        // kinds share a directory — taking the OpenAPI documents with it would
        // be a data loss nobody asked for.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "a.proto");
        SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, "{}", "b.json");

        SchemaUploadStore.Clear(SchemaUploadStore.ProtoKind);

        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
        Assert.False(File.Exists(Path.Combine(SchemaUploadStore.RootPath(), "a.proto")));
        Assert.True(File.Exists(Path.Combine(SchemaUploadStore.RootPath(), "b.json")));
    }

    // ---- the name comes from outside ----

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\evil.proto")]
    [InlineData("/absolute/path.proto")]
    [InlineData("C:\\Windows\\hosts")]
    public void A_Name_Cannot_Escape_The_Schemas_Directory(string hostile)
    {
        // ?name= is whatever the caller sent. The assertion is on where the
        // bytes landed, not on what the name was rewritten to: a check that
        // only says "the name looks clean" is the one that misses a case.
        //
        // Worth knowing before trusting this: the store has three layers here
        // — Path.GetFileName, the character filter, and the anchored pattern —
        // and each one alone stops every input below, so removing any single
        // one leaves this test green. Bypassing all three fails eight of the
        // tests in this class. The layering is deliberate; what this pins is
        // the outcome, not any one of them.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, hostile);

        var root = SchemaUploadStore.RootPath();
        var written = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

        Assert.All(written, f => Assert.Equal(
            Path.GetFullPath(root),
            Path.GetFullPath(Path.GetDirectoryName(f)!)));
        Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
    }

    [Fact]
    public void A_Name_That_Survives_Nothing_Still_Produces_A_File()
    {
        // Every character stripped: the upload must not be lost because the
        // operator's file was called "???".
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "???");

        var stored = Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        Assert.Equal(Proto, stored.Content);
    }

    [Fact]
    public void A_Very_Long_Name_Is_Cut_To_Something_A_Filesystem_Accepts()
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, new string('a', 500) + ".proto");

        var name = Path.GetFileName(Assert.Single(Directory.GetFiles(SchemaUploadStore.RootPath(), "*.proto")));
        Assert.True(name.Length <= 96, $"file name is {name.Length} characters: {name}");
        Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
    }

    // ---- a workspace is a directory other things also write to ----

    [Fact]
    public void A_Corrupt_Index_Reads_As_Empty_Rather_Than_Throwing()
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "a.proto");
        File.WriteAllText(Path.Combine(SchemaUploadStore.RootPath(), "index.json"), "{ not json");

        // The workbench still opens. An error page in front of everything
        // else the person has is the worse outcome.
        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        Assert.False(SchemaUploadStore.Has(SchemaUploadStore.ProtoKind));
    }

    [Fact]
    public void An_Index_Entry_Whose_File_Is_Gone_Is_Skipped_Not_Fatal()
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "a.proto");
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "b.proto");
        File.Delete(Path.Combine(SchemaUploadStore.RootPath(), "a.proto"));

        var left = Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        Assert.Equal("b.proto", left.SourceName);
    }

    [Fact]
    public void An_Index_Naming_A_File_Outside_The_Directory_Is_Refused()
    {
        // The index is a file in a workspace, and a git-native workspace is a
        // repository somebody can send a pull request to. A hand-written entry
        // pointing at ../secret.txt must not be read back.
        //
        // The secret is really written, and the assertion is that its content
        // does not come back. Asserting only on an empty result would pass
        // with the guard removed as well — the path would resolve to a file
        // that happens not to exist, and "nothing was returned" would be
        // proving the wrong thing.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "a.proto");

        const string secret = "TOP-SECRET-CONTENT";
        File.WriteAllText(Path.Combine(_root, "secret.txt"), secret);

        var escape = Path.GetRelativePath(SchemaUploadStore.RootPath(), Path.Combine(_root, "secret.txt"))
            .Replace('\\', '/');
        Assert.StartsWith("..", escape, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(SchemaUploadStore.RootPath(), escape)),
            "the escaping path has to reach the secret, or this test proves nothing");

        File.WriteAllText(
            Path.Combine(SchemaUploadStore.RootPath(), "index.json"),
            $$"""{"schemas":[{"id":"x","file":"{{escape}}","sourceName":"a.proto","kind":"proto"}]}""");

        var all = SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind);
        Assert.DoesNotContain(all, s => s.Content.Contains(secret, StringComparison.Ordinal));
        Assert.Empty(all);
    }

    // ---- handed over instead of stored ----

    [Fact]
    public void Documents_Handed_Over_Replace_What_Is_On_Disk()
    {
        // The CLI's case: one invocation whose inputs were named on the line.
        // Replacing rather than merging is the point — a pipeline that said
        // which schema to use should not also get whatever this machine's
        // identity happens to have uploaded.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "stored.proto");

        using (SchemaUploadStore.EnterExplicit(
            [new SchemaUploadStore.ExplicitSchema(SchemaUploadStore.ProtoKind, Proto, "named.proto")]))
        {
            var one = Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
            Assert.Equal("named.proto", one.SourceName);
        }

        Assert.Equal("stored.proto",
            Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind)).SourceName);
    }

    [Fact]
    public void A_Handed_Over_Set_Is_Still_Split_By_Kind()
    {
        using var _ = SchemaUploadStore.EnterExplicit([
            new SchemaUploadStore.ExplicitSchema(SchemaUploadStore.ProtoKind, Proto, "a.proto"),
            new SchemaUploadStore.ExplicitSchema(SchemaUploadStore.OpenApiKind, "{}", "b.json"),
        ]);

        Assert.Equal("a.proto", Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind)).SourceName);
        Assert.Equal("b.json", Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind)).SourceName);
    }

    [Fact]
    public void Same_Name_Different_Content_Are_Different_Documents()
    {
        // Callers cache their parse against these ids. A name-only id would
        // let a second run read the first one's parse back.
        string IdOf(string content)
        {
            using var _ = SchemaUploadStore.EnterExplicit(
                [new SchemaUploadStore.ExplicitSchema(SchemaUploadStore.ProtoKind, content, "same.proto")]);
            return Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind)).Id;
        }

        Assert.NotEqual(IdOf(Proto), IdOf(Proto + "\n// changed"));
        Assert.Equal(IdOf(Proto), IdOf(Proto));
    }

    [Fact]
    public void Handing_Documents_Over_Does_Not_Touch_What_Is_Stored()
    {
        // Reads only: a command that was handed its schemas has nothing to
        // store and no business clearing what somebody else uploaded.
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, Proto, "stored.proto");

        using (SchemaUploadStore.EnterExplicit([]))
        {
            Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        }

        Assert.True(File.Exists(Path.Combine(SchemaUploadStore.RootPath(), "stored.proto")));
        Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
    }

    [Fact]
    public void Nothing_Uploaded_Is_An_Empty_List_And_No_Directory_Demanded()
    {
        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind));
        Assert.False(SchemaUploadStore.Has(SchemaUploadStore.OpenApiKind));
        // Clearing what was never there is not an error either.
        SchemaUploadStore.Clear(SchemaUploadStore.ProtoKind);
    }
}
