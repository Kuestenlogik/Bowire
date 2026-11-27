// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Kuestenlogik.Bowire.Tests.Scripting;

/// <summary>
/// #672 — the JavaScript engine is optional: the workbench bundle (and so the
/// bowire tool) carries it, core and <c>Bundle.Minimal</c> do not, so a host
/// that embeds Bowire gains no engine. Checked over the project graph, not by
/// review.
/// </summary>
public sealed class ScriptingDependencyTests
{
    private static readonly string s_src = Path.Combine(RepoRoot(), "src");

    [Theory]
    [InlineData("Kuestenlogik.Bowire")]
    [InlineData("Kuestenlogik.Bowire.Bundle.Minimal")]
    public void An_Embedding_Package_Reaches_No_Script_Engine(string project)
    {
        var (projects, packages) = Closure(project);
        Assert.DoesNotContain("Kuestenlogik.Bowire.Scripting", projects);
        Assert.DoesNotContain("Jint", packages);
    }

    [Fact]
    public void The_Workbench_Bundle_Carries_It()
    {
        var (projects, packages) = Closure("Kuestenlogik.Bowire.Bundle.Workbench");
        Assert.Contains("Kuestenlogik.Bowire.Scripting", projects);
        Assert.Contains("Jint", packages);
    }

    // Every project reachable through ProjectReference, and every package
    // any of them references.
    private static (HashSet<string> Projects, HashSet<string> Packages) Closure(string project)
    {
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(Path.Combine(s_src, project, project + ".csproj"));
        while (pending.Count > 0)
        {
            var path = Path.GetFullPath(pending.Pop());
            if (!projects.Add(Path.GetFileNameWithoutExtension(path))) continue;
            var doc = XDocument.Load(path);
            foreach (var reference in doc.Descendants("ProjectReference"))
                pending.Push(Path.Combine(Path.GetDirectoryName(path)!, ((string?)reference.Attribute("Include") ?? "").Replace('\\', Path.DirectorySeparatorChar)));
            foreach (var package in doc.Descendants("PackageReference"))
                packages.Add((string?)package.Attribute("Include") ?? "");
        }
        return (projects, packages);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kuestenlogik.Bowire.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
