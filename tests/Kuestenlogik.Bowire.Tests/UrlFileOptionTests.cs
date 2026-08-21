// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #604 — <c>--url-file</c>.
/// <para>
/// The option was declared on the root command and documented in
/// <c>docs/setup/sidecar.md</c>, but nothing read it: passing it was silently
/// a no-op, so Bowire started, discovered nothing, and the documentation said
/// it should have worked. These pin both halves of the fix — that the URLs
/// arrive, and that every way of getting it wrong says so out loud instead of
/// falling back to quiet.
/// </para>
/// </summary>
[Collection("CwdSerialised")]
public sealed class UrlFileOptionTests : IDisposable
{
    private readonly string _cwdBackup;
    private readonly string _tempDir;

    public UrlFileOptionTests()
    {
        _cwdBackup = Directory.GetCurrentDirectory();
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-urlfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwdBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteUrlFile(string name, string content)
    {
        var path = SafePath.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static BrowserUiOptions Resolve(string[] args) =>
        BowireConfiguration.BuildBrowserUiOptions(BowireConfiguration.Build(args), args);

    [Fact]
    public void UrlsFromTheFile_ReachServerUrls()
    {
        var file = WriteUrlFile("urls.txt", "https://a.example.com\nhttps://b.example.com\n");

        var ui = Resolve(["--url-file", file]);

        Assert.Equal(["https://a.example.com", "https://b.example.com"], ui.ServerUrls);
        // The scalar stays in step with the list, as it does for --url.
        Assert.Equal("https://a.example.com", ui.ServerUrl);
    }

    [Fact]
    public void EqualsForm_IsAccepted()
    {
        var file = WriteUrlFile("urls.txt", "https://a.example.com\n");

        var ui = Resolve([$"--url-file={file}"]);

        Assert.Equal(["https://a.example.com"], ui.ServerUrls);
    }

    [Fact]
    public void BlankLinesAndCommentsAreSkipped()
    {
        // Annotating the list is the whole reason to keep URLs in a file
        // rather than on the command line, so both have to survive.
        var file = WriteUrlFile("urls.txt", """
            # staging fleet
            https://a.example.com

            #  https://disabled.example.com
              https://b.example.com

            """);

        var ui = Resolve(["--url-file", file]);

        Assert.Equal(["https://a.example.com", "https://b.example.com"], ui.ServerUrls);
    }

    [Fact]
    public void MixesWithRepeatedUrlFlags()
    {
        // The acceptance criterion is explicit that both forms work together;
        // a file that silently replaced --url would be its own surprise.
        var file = WriteUrlFile("urls.txt", "https://from-file.example.com\n");

        var ui = Resolve(["--url", "https://from-flag.example.com", "--url-file", file]);

        Assert.Contains("https://from-flag.example.com", ui.ServerUrls);
        Assert.Contains("https://from-file.example.com", ui.ServerUrls);
    }

    [Fact]
    public void SeveralFilesAreAllRead()
    {
        var a = WriteUrlFile("a.txt", "https://a.example.com\n");
        var b = WriteUrlFile("b.txt", "https://b.example.com\n");

        var ui = Resolve(["--url-file", a, "--url-file", b]);

        Assert.Equal(["https://a.example.com", "https://b.example.com"], ui.ServerUrls);
    }

    [Fact]
    public void MissingFile_FailsAndNamesThePath()
    {
        var missing = SafePath.Combine(_tempDir, "nope.txt");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(["--url-file", missing]));

        // Naming the path is the point — this whole option existed for months
        // in a state where nothing at all was said.
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWithNoUsableLines_FailsRatherThanResolvingToNothing()
    {
        // An empty result is indistinguishable from "the flag was ignored",
        // which is the exact failure being fixed. Comments only, so the file
        // is readable and non-empty and still yields nothing.
        var file = WriteUrlFile("urls.txt", "# everything commented out\n\n   \n");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(["--url-file", file]));

        Assert.Contains(file, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheFlag_NothingChanges()
    {
        var ui = Resolve([]);

        Assert.Empty(ui.ServerUrls);
        Assert.Null(ui.ServerUrl);
    }
}
