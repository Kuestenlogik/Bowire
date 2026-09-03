// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Projects;

/// <summary>
/// Which root a path belongs under (#616).
/// </summary>
public enum BowireStorageScope
{
    /// <summary>
    /// Whatever <see cref="BowireStorageRoot"/> resolved for this process: a
    /// project's <c>.bowire/</c> when its manifest opts in, the user root
    /// otherwise. Where a person's own work goes — collections, recordings,
    /// environments.
    /// </summary>
    Data = 0,

    /// <summary>
    /// A root that does not depend on which account the process runs as:
    /// <c>%ProgramData%\Bowire</c> on Windows, <c>/var/lib/bowire</c>
    /// elsewhere. For anything a service instance must still find when it runs
    /// as a service account and an admin configured it as themselves.
    /// </summary>
    /// <remarks>
    /// This is the case that produced a real defect in Surgewave
    /// (Kuestenlogik/Surgewave#157): an admin installed a plugin, the tool
    /// confirmed it, and the broker never saw it, because <c>~</c> meant two
    /// different profiles.
    /// </remarks>
    Machine = 1,
}

/// <summary>
/// Resolves where Bowire stores things.
/// </summary>
/// <remarks>
/// <para>
/// Injected rather than called statically, because the answer depends on how
/// the host was configured and that decision should be made once and passed
/// down. Before this existed, fourteen files across six assemblies each wrote
/// <c>Path.Combine(GetFolderPath(UserProfile), ".bowire", …)</c> — which meant
/// the machine scope and the instance segment below could only ever have
/// reached the stores that happened to route through
/// <see cref="BowireUserContext"/>, and silently missed the plugin directory,
/// the proxy CA, the vuln-db cache and the MCP stores.
/// </para>
/// <para>
/// The two environment variables are read here and nowhere else. That is the
/// point of the type: <c>BOWIRE_DATA_DIR</c> is only a usable test override if
/// pointing it at one directory redirects <em>everything</em>, and
/// <c>BOWIRE_INSTANCE</c> only separates instances if it separates all of
/// their state rather than the half that went through one helper.
/// </para>
/// </remarks>
public interface IBowirePathResolver
{
    /// <summary>The root for <paramref name="scope"/>, with no segments added.</summary>
    string Root(BowireStorageScope scope);

    /// <summary>
    /// An absolute path under <paramref name="scope"/>'s root.
    /// </summary>
    /// <param name="scope">Which root to resolve under.</param>
    /// <param name="segments">
    /// Relative segments — <c>"plugins"</c>, <c>"collections.json"</c>. Rooted
    /// segments are rejected rather than silently replacing the root, which is
    /// what <see cref="Path.Combine(string, string)"/> would do.
    /// </param>
    string Resolve(BowireStorageScope scope, params string[] segments);
}

/// <summary>
/// The default <see cref="IBowirePathResolver"/>.
/// </summary>
/// <remarks>
/// Constructible without a container on purpose: several call sites resolve
/// paths from static initialisers that run before any host is built, and a
/// resolver they cannot reach would just mean those keep their own copy of the
/// logic, which is the problem this type exists to end.
/// </remarks>
public sealed class BowirePathResolver : IBowirePathResolver
{
    /// <summary>Redirects every scope at one directory. For test fixtures.</summary>
    public const string DataDirVariable = "BOWIRE_DATA_DIR";

    /// <summary>Adds one path segment under each root, separating co-located instances.</summary>
    public const string InstanceVariable = "BOWIRE_INSTANCE";

    /// <summary>
    /// Names an instance may not take.
    /// </summary>
    /// <remarks>
    /// With no instance set the scope <em>is</em> the root, so an instance
    /// named after something the root already contains would share state with
    /// an unnamed one — which is the exact opposite of what setting it was
    /// meant to achieve, and would fail quietly.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReservedInstanceNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plugins", "workspaces", "recordings", "flows", "cache", "certs", "logs", "presets", "mocks",
            "users", "scim", "audit",
        };

    private readonly Func<string, string?> _environment;
    private readonly Func<string> _dataRoot;

    /// <summary>A resolver reading the real environment.</summary>
    public BowirePathResolver()
        : this(Environment.GetEnvironmentVariable, () => BowireUserContext.Current is IBowireStorageRootProvider s
            ? s.StorageRoot
            : DefaultBowireUserStore.UserProfileRoot)
    {
    }

    /// <summary>
    /// A resolver with both of its inputs supplied.
    /// </summary>
    /// <param name="environment">Reads an environment variable, or null.</param>
    /// <param name="dataRoot">
    /// The <see cref="BowireStorageScope.Data"/> root before an instance
    /// segment is applied — normally whatever <see cref="BowireStorageRoot"/>
    /// decided for this process.
    /// </param>
    public BowirePathResolver(Func<string, string?> environment, Func<string> dataRoot)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
    }

    /// <inheritdoc />
    public string Root(BowireStorageScope scope)
    {
        var root = BaseRoot(scope);

        var instance = InstanceSegment();
        return instance is null ? root : Path.Combine(root, instance);
    }

    /// <inheritdoc />
    public string Resolve(BowireStorageScope scope, params string[] segments)
    {
        var root = Root(scope);
        if (segments is null || segments.Length == 0) return root;

        // SafePath rather than Path.Combine: a rooted segment would silently
        // discard everything before it, so a caller that passes one gets an
        // error instead of a file written somewhere nobody expected.
        var relative = Path.Combine(segments);
        return SafePath.Combine(root, relative);
    }

    /// <summary>
    /// The <c>BOWIRE_DATA_DIR</c> override, or <c>null</c> when it is unset
    /// (#643).
    /// </summary>
    /// <param name="environment">
    /// How to read it. Defaults to the process environment; tests pass their
    /// own so they need not mutate a process-global.
    /// </param>
    /// <remarks>
    /// Public because <see cref="BowireStorageRoot"/> has to ask the same
    /// question, and asking it twice is how the two answers came apart:
    /// this resolver honoured the variable while the user store — and so
    /// every workspace-scoped path — did not. A run that believed it was
    /// isolated wrote into the real <c>~/.bowire</c>.
    /// </remarks>
    public static string? DataDirOverride(Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(DataDirVariable);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private string BaseRoot(BowireStorageScope scope)
    {
        // One directory for everything, which is what makes a test fixture
        // able to create one tree and delete one tree rather than hunting for
        // state beside whichever output directory the run used.
        var redirected = DataDirOverride(_environment);
        if (redirected is not null) return redirected;

        return scope switch
        {
            BowireStorageScope.Machine => MachineRoot(),
            _ => Absolute(_dataRoot()),
        };
    }

    /// <summary>
    /// A root that is safe to write to, whatever the platform reported.
    /// </summary>
    /// <remarks>
    /// An environment with no user profile — a locked-down service account, a
    /// scratch container — makes <c>GetFolderPath(UserProfile)</c> return "",
    /// and the naive combine then yields the relative path <c>.bowire</c>,
    /// which lands beside whatever the working directory happens to be and
    /// moves when it changes. Three call sites each had their own guard
    /// against this (two fell back to temp, one returned ""); the guard
    /// belongs here, once, so they can stop carrying it.
    /// </remarks>
    private static string Absolute(string root)
        => string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)
            ? Path.Combine(Path.GetTempPath(), "bowire")
            : root;

    /// <summary>
    /// The account-independent root.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than through
    /// <see cref="Environment.SpecialFolder.CommonApplicationData"/>, which
    /// maps to <c>/usr/share</c> on .NET for Unix — that is for static package
    /// data shipped by a package manager, not for state a service writes at
    /// runtime. <c>/var/lib</c> is where the latter belongs.
    /// </remarks>
    private static string MachineRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.None);
            return string.IsNullOrEmpty(programData)
                ? Path.Combine(Path.GetTempPath(), "Bowire")
                : Path.Combine(programData, "Bowire");
        }

        return "/var/lib/bowire";
    }

    /// <summary>The validated instance segment, or null when there is none.</summary>
    private string? InstanceSegment()
    {
        var value = _environment(InstanceVariable);
        if (string.IsNullOrWhiteSpace(value)) return null;

        var instance = value.Trim();
        ValidateInstance(instance);
        return instance;
    }

    /// <summary>
    /// Reject an instance name that cannot do its job, loudly.
    /// </summary>
    /// <remarks>
    /// Loudly, because every failure here is silent otherwise: a name with a
    /// separator writes outside the root, and a reserved name shares state
    /// with the unnamed instance. Both look like they worked.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The name is unusable.</exception>
    public static void ValidateInstance(string instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);

        if (instance.Contains('/', StringComparison.Ordinal)
            || instance.Contains('\\', StringComparison.Ordinal)
            || instance.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(instance))
        {
            throw new InvalidOperationException(
                $"{InstanceVariable}='{instance}' is not a single path segment. "
                + "It names one directory under the storage root, so it cannot contain a separator or '..'.");
        }

        if (instance.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"{InstanceVariable}='{instance}' contains characters that are not valid in a directory name.");
        }

        if (ReservedInstanceNames.Contains(instance))
        {
            throw new InvalidOperationException(
                $"{InstanceVariable}='{instance}' collides with a directory Bowire already keeps under its storage root. "
                + $"With no instance set the root itself is the scope, so this instance would share state with an unnamed one. "
                + $"Reserved: {string.Join(", ", ReservedInstanceNames.Order(StringComparer.Ordinal))}.");
        }
    }
}
