// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Scripting;

/// <summary>
/// Runs a request's pre-request and post-response scripts outside the
/// browser (#672): in <c>bowire test</c>, and anywhere else a call is made on
/// the server side.
/// </summary>
/// <remarks>
/// <para>
/// The scripts are the ones a person writes in the workbench, against the
/// <c>ctx</c> surface of <c>wwwroot/js/scripts.js</c>. An implementation must
/// give them the same <c>ctx</c> and the same outcome as the browser; the one
/// in <c>Kuestenlogik.Bowire.Scripting</c> runs that very file in a sandboxed
/// JavaScript engine, and a conformance suite holds both to the same fixtures.
/// </para>
/// <para>
/// Core carries only this contract, so a host that embeds Bowire gains no
/// JavaScript engine. Where none is installed, <see cref="BowireScriptRunners.Find"/>
/// answers <c>null</c>, and a caller with a script to run must fail the call
/// saying so, never skip the script.
/// </para>
/// </remarks>
public interface IBowireScriptRunner
{
    /// <summary>Run a pre-request script; it may change <see cref="BowireScriptCall.Request"/>.</summary>
    BowireScriptResult RunPreRequest(BowireScriptCall script);

    /// <summary>Run a post-response script against <see cref="BowireScriptCall.Response"/>.</summary>
    BowireScriptResult RunPostResponse(BowireScriptCall script);
}

/// <summary>One script run: the script, the request it belongs to and the scope it may read and write.</summary>
public sealed class BowireScriptCall
{
    /// <summary>The script's source.</summary>
    public required string Source { get; init; }

    /// <summary>The protocol shape that decides the <c>ctx</c> members: <c>rest</c>, <c>grpc</c>, <c>mqtt</c> or <c>websocket</c>.</summary>
    public string Shape { get; init; } = "rest";

    /// <summary>The service the call goes to, as <c>ctx.service</c>.</summary>
    public string Service { get; init; } = "";

    /// <summary>The method the call invokes, as <c>ctx.method</c>.</summary>
    public string Method { get; init; } = "";

    /// <summary>The request. A pre-request script's changes are written back here.</summary>
    public BowireScriptRequest Request { get; init; } = new();

    /// <summary>The response, for a post-response script.</summary>
    public BowireScriptResponse? Response { get; init; }

    /// <summary>The environment, as <c>ctx.env</c>. <c>ctx.env.set</c> writes here.</summary>
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The values scripts share across the calls of one run, as
    /// <c>ctx.vars.captured</c>, as a JSON object. Written back after the run.
    /// </summary>
    public string CapturedJson { get; set; } = "{}";

    /// <summary>How long and how much the script may run.</summary>
    public BowireScriptLimits Limits { get; init; } = new();
}

/// <summary>
/// The sandbox's limits. A script that reaches one is stopped and reported,
/// so a runaway script fails its call rather than the run. The caller fills
/// them from configuration (<c>Bowire:Scripting:*</c>); these are the values
/// when it sets none.
/// </summary>
public sealed record BowireScriptLimits
{
    /// <summary>Wall-clock time per script.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Memory the script may allocate, in bytes.</summary>
    public long MemoryBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Statements the script may execute.</summary>
    public int MaxStatements { get; init; } = 10_000_000;
}

/// <summary>The request a script sees and, before it is sent, may change.</summary>
public sealed class BowireScriptRequest
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "";
    public string Body { get; set; } = "{}";
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, string> Query { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>gRPC: the deadline a script set with <c>ctx.deadline.setSeconds</c>, or 0.</summary>
    public double DeadlineSeconds { get; set; }
}

/// <summary>The response a post-response script reads.</summary>
public sealed class BowireScriptResponse
{
    public string? Body { get; init; }
    public string? Status { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>What a script run came to.</summary>
/// <param name="Ok">The script ran to its end.</param>
/// <param name="Error">Why it did not: the exception, the failed assertion, or the limit it hit.</param>
/// <param name="AssertFail">It stopped at a <c>ctx.assert</c>.</param>
/// <param name="Log">What it wrote with <c>ctx.log</c>, in order.</param>
public sealed record BowireScriptResult(bool Ok, string? Error, bool AssertFail, IReadOnlyList<string> Log);

/// <summary>Finds the installed <see cref="IBowireScriptRunner"/>.</summary>
public static class BowireScriptRunners
{
    // The one implementation Bowire ships, named rather than scanned for:
    // the package is optional, and a type lookup is all it takes to know
    // whether it is there.
    private const string JintRunner = "Kuestenlogik.Bowire.Scripting.JintScriptRunner, Kuestenlogik.Bowire.Scripting";

    private static readonly Lazy<IBowireScriptRunner?> s_default = new(() =>
        Type.GetType(JintRunner, throwOnError: false) is { } type
            ? Activator.CreateInstance(type) as IBowireScriptRunner
            : null);

    /// <summary>
    /// The installed runner, or <c>null</c> when <c>Kuestenlogik.Bowire.Scripting</c>
    /// is not part of this host.
    /// </summary>
    public static IBowireScriptRunner? Find() => s_default.Value;

    /// <summary>The reason to give when a script has to run and <see cref="Find"/> found nothing.</summary>
    public const string NotInstalled =
        "this request has a script, and no script engine is installed: add the Kuestenlogik.Bowire.Scripting package (the bowire tool ships it)";
}
