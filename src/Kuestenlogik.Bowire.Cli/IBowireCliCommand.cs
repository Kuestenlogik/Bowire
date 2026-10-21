// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Cli;

/// <summary>
/// Auto-discovered CLI subcommand contribution. Implement this on a
/// concrete class in any assembly whose name starts with
/// <c>Kuestenlogik.Bowire</c> and the Bowire CLI will attach the built
/// command to its root at startup — same assembly-scan mechanism
/// <see cref="BowireProtocolRegistry"/> uses for <see cref="IBowireProtocol"/>.
///
/// Allows scanner / fuzzer / jwt / proxy / and future third-party
/// subcommands to live in their own projects (or even sibling NuGet
/// packages installed via <c>bowire plugin install</c>) instead of
/// being hard-wired into <c>Kuestenlogik.Bowire.Tool</c>. The Tool
/// project shrinks to CLI-glue + the default browser-UI action.
/// </summary>
public interface IBowireCliCommand
{
    /// <summary>
    /// Stable identifier for this subcommand — used by the
    /// <c>--disable-cli-command &lt;id&gt;</c> flag so operators can
    /// skip a contribution at startup. Convention: the subcommand
    /// verb (<c>"scan"</c>, <c>"fuzz"</c>, <c>"jwt"</c>, …) so it
    /// matches what the user types.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Build a <see cref="Command"/> ready to attach to the root
    /// command. Invoked once during CLI bootstrap; the returned
    /// command is owned by the root and may not survive a process
    /// restart.
    /// </summary>
    Command Build();

    /// <summary>
    /// Build the command with the CLI's configuration (appsettings,
    /// <c>BOWIRE_</c> environment variables, mapped flags) so a contributed
    /// command can read its settings the same way the built-in commands do
    /// (#881). The CLI calls this overload; the default ignores the
    /// configuration and calls <see cref="Build()"/>.
    /// </summary>
    Command Build(IConfiguration? configuration) => Build();
}
