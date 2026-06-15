// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Disposable wrapper for the client- and (optional) CA-certificate pair
/// produced by <see cref="MtlsConfig.TryLoadCertificates"/>. Callers use
/// a <c>using</c> declaration so the analyzer can see disposal on every
/// exit path; once ownership of the inner X509 resources has been handed
/// off to a long-lived owner (<see cref="MtlsHandlerOwner"/>,
/// <see cref="MtlsCertOwner"/>), <see cref="Release"/> turns the wrapper's
/// own Dispose into a no-op so the certificates aren't torn down with the
/// pair going out of scope. Cleaner than three <c>out</c> parameters plus
/// a CA2000 suppression at every call site.
/// </summary>
public sealed class MtlsCertificatePair : IDisposable
{
    private X509Certificate2? _clientCert;
    private X509Certificate2? _caCert;
    private bool _disposed;

    internal MtlsCertificatePair(X509Certificate2 clientCert, X509Certificate2? caCert)
    {
        _clientCert = clientCert;
        _caCert = caCert;
    }

    /// <summary>
    /// The PEM-decoded client certificate. Non-null until <see cref="Release"/>
    /// or <see cref="Dispose"/> runs.
    /// </summary>
    public X509Certificate2 ClientCert => _clientCert
        ?? throw new ObjectDisposedException(nameof(MtlsCertificatePair));

    /// <summary>
    /// The PEM-decoded CA certificate when <see cref="MtlsConfig.CaCertificatePem"/>
    /// was supplied, otherwise null.
    /// </summary>
    public X509Certificate2? CaCert => _caCert;

    /// <summary>
    /// Hand ownership of the inner X509 resources to the caller — disposing
    /// the pair afterwards is a no-op. Invoked from
    /// <see cref="MtlsHandlerOwner"/> + <see cref="MtlsCertOwner"/> factories
    /// once the long-lived owner has taken responsibility for the certs.
    /// </summary>
    public void Release()
    {
        _clientCert = null;
        _caCert = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clientCert?.Dispose();
        _caCert?.Dispose();
        _clientCert = null;
        _caCert = null;
    }
}

/// <summary>
/// Bundles a pre-configured <see cref="HttpMessageHandler"/> with the X509
/// resources whose lifetime must match it. Disposing the owner disposes
/// the handler and every cert it holds — caller places this in the
/// <c>finally</c> next to the per-call HttpClient / GrpcChannel.
/// <para>
/// One owner type rather than one per protocol: REST uses
/// <see cref="HttpClientHandler"/> (its <c>ClientCertificates</c> collection
/// is the natural shape); gRPC uses <see cref="SocketsHttpHandler"/> (its
/// <c>SslOptions</c> property carries the same data via
/// <see cref="SslClientAuthenticationOptions"/>). Both inherit from
/// <see cref="HttpMessageHandler"/>, so callers can hold the abstract base
/// type without caring which factory built it.
/// </para>
/// </summary>
public sealed class MtlsHandlerOwner : IDisposable
{
    public HttpMessageHandler Handler { get; }
    private readonly X509Certificate2 _clientCert;
    private readonly X509Certificate2? _caCert;
    private bool _disposed;

    private MtlsHandlerOwner(HttpMessageHandler handler, X509Certificate2 clientCert, X509Certificate2? caCert)
    {
        Handler = handler;
        _clientCert = clientCert;
        _caCert = caCert;
    }

    /// <summary>
    /// Build an <see cref="HttpClientHandler"/> wired up with this config —
    /// natural fit for REST plugins routing through <see cref="HttpClient"/>.
    /// Returns null on PEM-parse failure with a human-readable error.
    /// </summary>
    public static MtlsHandlerOwner? CreateHttpClientHandler(MtlsConfig config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var pair = config.TryLoadCertificates(out error);
        if (pair is null) return null;

#pragma warning disable CA5400
        // CRL checks default off: most internal PKIs that mTLS tools target
        // (corporate CAs, dev-only roots) don't publish a CRL distribution
        // point, so flipping this on would block every connection.
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            CheckCertificateRevocationList = false
        };
#pragma warning restore CA5400
        handler.ClientCertificates.Add(pair.ClientCert);

        var validator = config.BuildServerValidator(pair.CaCert);
        if (validator is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errs) =>
                validator(req, cert, chain, errs);
        }

        var owner = new MtlsHandlerOwner(handler, pair.ClientCert, pair.CaCert);
        // Ownership of both certs has moved into `owner`; the pair's
        // implicit Dispose at end-of-method becomes a no-op so we don't
        // tear down the certs the handler is now using.
        pair.Release();
        return owner;
    }

    /// <summary>
    /// Build a <see cref="SocketsHttpHandler"/> wired up with this config —
    /// natural fit for gRPC plugins routing through <see cref="System.Net.Http.HttpMessageHandler"/>
    /// on top of HTTP/2. The cert lands on
    /// <see cref="SocketsHttpHandler.SslOptions"/>.<see cref="SslClientAuthenticationOptions.ClientCertificates"/>;
    /// the same TLS handshake mechanics as REST, just expressed via the
    /// SocketsHttpHandler API.
    /// </summary>
    public static MtlsHandlerOwner? CreateSocketsHttpHandler(MtlsConfig config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var pair = config.TryLoadCertificates(out error);
        if (pair is null) return null;

        var sslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = [pair.ClientCert]
        };

        var validator = config.BuildServerValidator(pair.CaCert);
        if (validator is not null)
        {
            sslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errs) =>
                validator(sender, cert as X509Certificate2, chain, errs);
        }

        var handler = new SocketsHttpHandler
        {
            SslOptions = sslOptions,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        var owner = new MtlsHandlerOwner(handler, pair.ClientCert, pair.CaCert);
        // Ownership of both certs has moved into `owner`; the pair's
        // implicit Dispose at end-of-method becomes a no-op so we don't
        // tear down the certs the handler is now using.
        pair.Release();
        return owner;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Handler.Dispose();
        _clientCert.Dispose();
        _caCert?.Dispose();
    }
}

/// <summary>
/// Lightweight cert-only owner for transports that don't go through an
/// <see cref="HttpMessageHandler"/> — primarily <see cref="System.Net.WebSockets.ClientWebSocket"/>,
/// which exposes its own <c>ClientCertificates</c> + <c>RemoteCertificateValidationCallback</c>
/// on <c>ClientWebSocketOptions</c>. Disposes the loaded X509 resources
/// when the channel goes away.
/// </summary>
public sealed class MtlsCertOwner : IDisposable
{
    public X509Certificate2 ClientCert { get; }
    public X509Certificate2? CaCert { get; }
    public Func<object?, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? Validator { get; }
    private bool _disposed;

    private MtlsCertOwner(
        X509Certificate2 clientCert,
        X509Certificate2? caCert,
        Func<object?, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? validator)
    {
        ClientCert = clientCert;
        CaCert = caCert;
        Validator = validator;
    }

    /// <summary>
    /// Load the configured certs and pre-build the server-validation
    /// callback (allow-self-signed → accept-anything; CA pem → CA-pinning
    /// validator). Returns null on PEM-parse failure with a clean error.
    /// </summary>
    public static MtlsCertOwner? Load(MtlsConfig config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var pair = config.TryLoadCertificates(out error);
        if (pair is null) return null;

        var validator = config.BuildServerValidator(pair.CaCert);
        var owner = new MtlsCertOwner(pair.ClientCert, pair.CaCert, validator);
        // Ownership of both certs has moved into `owner`; the pair's
        // implicit Dispose at end-of-method becomes a no-op so we don't
        // tear down the certs the owner is now using.
        pair.Release();
        return owner;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClientCert.Dispose();
        CaCert?.Dispose();
    }
}
