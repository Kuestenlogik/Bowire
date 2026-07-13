// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Builds an <see cref="ISignaler"/> from a <c>--signal &lt;scheme&gt;:&lt;arg&gt;</c>
/// spec. Each outbound channel ships as its own opt-in sibling package
/// (<c>…Monitoring.Slack</c>, <c>…Monitoring.PagerDuty</c>, <c>…Monitoring.Otlp</c>)
/// contributing one factory, so Core + the Core-adjacent Monitoring package gain
/// no third-party dependencies. The CLI discovers factories by assembly scan and
/// reports a clear "install the package" message when a scheme has no factory.
/// Implementations must be zero-config (parameterless ctor) so the registry can
/// <c>Activator.CreateInstance</c> them.
/// </summary>
public interface ISignalerFactory
{
    /// <summary>The <c>--signal</c> scheme this factory handles (e.g. <c>slack</c>).</summary>
    string Scheme { get; }

    /// <summary>
    /// Build the signaler from the argument after <c>scheme:</c> — a webhook URL,
    /// a routing key, an OTLP endpoint, … Throws <see cref="SignalerConfigException"/>
    /// when the argument is missing or malformed.
    /// </summary>
    ISignaler Create(string argument);
}

/// <summary>Thrown by an <see cref="ISignalerFactory"/> when the <c>--signal</c> argument is invalid.</summary>
public sealed class SignalerConfigException : Exception
{
    public SignalerConfigException(string message) : base(message) { }
    public SignalerConfigException(string message, Exception innerException) : base(message, innerException) { }
    public SignalerConfigException() { }
}
