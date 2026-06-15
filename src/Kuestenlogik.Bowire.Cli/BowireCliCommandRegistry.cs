// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Cli;

/// <summary>
/// Discovers every <see cref="IBowireCliCommand"/> implementation in
/// the current <see cref="AppDomain"/> at startup. Same shape +
/// blacklist contract as <see cref="BowireProtocolRegistry"/> — one
/// pattern for operators to learn (<c>--disable-plugin</c> for
/// protocols, <c>--disable-cli-command</c> for subcommands).
/// </summary>
public sealed class BowireCliCommandRegistry
{
    private readonly List<IBowireCliCommand> _commands = [];

    public IReadOnlyList<IBowireCliCommand> Commands => _commands;

    public void Register(IBowireCliCommand command) => _commands.Add(command);

    /// <summary>
    /// Scan every loaded <c>Kuestenlogik.Bowire*</c> assembly for
    /// concrete <see cref="IBowireCliCommand"/> implementations and
    /// instantiate one of each. Skips ids in
    /// <paramref name="disabledCommandIds"/> so operators can mask
    /// a noisy / heavy command (`--disable-cli-command scan` etc.)
    /// without rebuilding the tool.
    /// </summary>
    public static BowireCliCommandRegistry Discover(
        IEnumerable<string>? disabledCommandIds = null,
        ILogger? logger = null)
    {
        var disabled = disabledCommandIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(disabledCommandIds, StringComparer.OrdinalIgnoreCase);

        var registry = new BowireCliCommandRegistry();
        var bowireAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire") == true);
        foreach (var assembly in bowireAssemblies)
        {
            try
            {
                var commandTypes = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface)
                    .Where(typeof(IBowireCliCommand).IsAssignableFrom);
                foreach (var type in commandTypes)
                {
                    if (Activator.CreateInstance(type) is IBowireCliCommand command)
                    {
                        if (disabled.Contains(command.Id))
                        {
                            if (logger is not null)
                                CliCommandRegistryLog.SkippingDisabledCommand(logger, command.Id);
                            continue;
                        }
                        registry.Register(command);
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // An assembly with broken type-load doesn't get to
                // veto the whole CLI bootstrap; the protocol registry
                // takes the same view.
            }
        }
        return registry;
    }
}

/// <summary>
/// Source-generated logger wrappers for
/// <see cref="BowireCliCommandRegistry"/>. Keeps CA1873 happy — the
/// generator emits the <c>IsEnabled</c>-gated dispatch the analyzer
/// wants in place of the suppressed runtime null-check.
/// </summary>
internal static partial class CliCommandRegistryLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Skipping disabled CLI command '{CommandId}' (--disable-cli-command).")]
    public static partial void SkippingDisabledCommand(ILogger logger, string commandId);
}
