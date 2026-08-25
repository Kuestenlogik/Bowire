// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The <c>--port-file</c> handoff (#615).
/// </summary>
/// <remarks>
/// The property under test is narrow and load-bearing: <b>the file exists if
/// and only if the workbench is bound</b>. A caller polls for it and then
/// trusts what it reads, so every way the file could exist and be wrong —
/// left by a dead process, half written, carrying a previous run's port —
/// is a caller opening a dead page.
/// </remarks>
public sealed class PortFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bowire-portfile-" + Guid.NewGuid().ToString("N"));

    private string Path_(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Write_Creates_The_Document_With_Url_And_Pid()
    {
        var path = Path_("port.json");

        Assert.True(PortFile.Write(path, "http://127.0.0.1:51234/", pid: 4242));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("http://127.0.0.1:51234/", doc.RootElement.GetProperty("url").GetString());
        // The pid is what lets a reader tell a live file from one a killed
        // process left behind — the single case in-process cleanup cannot cover.
        Assert.Equal(4242, doc.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public void Write_Creates_Missing_Directories()
    {
        // Callers name a path, not a directory they prepared: an editor plugin
        // pointing into its own storage folder on a first run has nothing there yet.
        var path = Path_(Path.Combine("nested", "deeper", "port.json"));

        Assert.True(PortFile.Write(path, "http://127.0.0.1:1/", pid: 1));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_Replaces_A_Previous_Runs_Document_Entirely()
    {
        var path = Path_("port.json");
        PortFile.Write(path, "http://127.0.0.1:1111/", pid: 1);

        PortFile.Write(path, "http://127.0.0.1:2222/", pid: 2);

        // Not appended, not merged: the old port must not survive anywhere in
        // the file, or a lenient reader could find it and connect to nothing.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("1111", text, StringComparison.Ordinal);
        Assert.Contains("2222", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Leaves_No_Temp_File_Behind()
    {
        // The write goes through a sibling temp file so a polling reader can
        // never open a half-written document. That temp must not linger — the
        // directory is often the caller's own, and debris in it is confusing
        // at best and picked up by a glob at worst.
        var path = Path_("port.json");

        PortFile.Write(path, "http://127.0.0.1:1/", pid: 1);

        Assert.Equal(["port.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Write_Returns_False_Rather_Than_Throwing_On_An_Impossible_Path()
    {
        // A bad --port-file must not take the workbench down with it: the
        // server is up and usable, and the caller gets told on stderr.
        var impossible = Path_("port.json") + "\0invalid";

        Assert.False(PortFile.Write(impossible, "http://127.0.0.1:1/", pid: 1));
    }

    [Fact]
    public void Clear_Removes_The_File()
    {
        var path = Path_("port.json");
        PortFile.Write(path, "http://127.0.0.1:1/", pid: 1);

        PortFile.Clear(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Clear_Is_Silent_When_There_Is_Nothing_To_Remove()
    {
        // Called before every bind, where "no file" is the normal case.
        PortFile.Clear(Path_("never-existed.json"));
        PortFile.Clear(null);
        PortFile.Clear("   ");
    }
}
