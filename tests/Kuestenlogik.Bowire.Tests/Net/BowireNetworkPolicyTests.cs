// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Net;

[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class BowireNetworkCollectionDefinition
{
    public const string Name = "BowireNetworkPolicy";
}

/// <summary>
/// How the network settings layer and resolve (#680): workspace over global,
/// the host configuration over both, loopback always direct, and a CA bundle
/// that rescues exactly a chain error.
/// </summary>
[Collection(BowireNetworkCollectionDefinition.Name)]
public sealed class BowireNetworkPolicyTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _root;

    public BowireNetworkPolicyTests()
    {
        _originalStore = BowireUserContext.Current;
        _root = Path.Combine(Path.GetTempPath(), "bowire-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new TempUserStore(_root);
        BowireNetworkPolicy.UseConfiguration(null);
        BowireNetworkPolicy.UseKeyring(null);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        BowireNetworkPolicy.UseConfiguration(null);
        BowireNetworkPolicy.UseKeyring(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static readonly Uri Remote = new("https://api.example.test/orders");

    [Fact]
    public void Without_Settings_The_System_Proxy_Applies()
    {
        var effective = BowireNetworkPolicy.For(null);
        Assert.Equal(BowireProxyMode.System, effective.Mode);
        Assert.Empty(effective.Problems);
    }

    [Fact]
    public void The_Workspace_Layer_Wins_Over_The_Global_One()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { Mode = "manual", ProxyUrl = "http://proxy.corp:3128" }, null);
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { Mode = "none" }, "ws_lab");

        Assert.Equal(new Uri("http://proxy.corp:3128"), BowireNetworkPolicy.For(null).ProxyFor(Remote));
        var lab = BowireNetworkPolicy.For("ws_lab");
        Assert.Null(lab.ProxyFor(Remote));
        Assert.Equal("workspace", lab.Sources["mode"]);
        Assert.Equal("global", lab.Sources["proxyUrl"]);
    }

    [Fact]
    public void The_Current_Call_Follows_The_Workspace_It_Came_From()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { ProxyUrl = "http://proxy.corp:3128" }, "ws_a");

        Assert.Equal(BowireProxyMode.System, BowireNetworkPolicy.Current.Mode);
        using (BowirePluginSettingsScope.Enter("ws_a"))
        {
            Assert.Equal(new Uri("http://proxy.corp:3128"), BowireNetworkPolicy.Proxy.GetProxy(Remote));
            Assert.False(BowireNetworkPolicy.Proxy.IsBypassed(Remote));
        }
    }

    [Fact]
    public void The_Host_Configuration_Wins_Over_The_Files()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { ProxyUrl = "http://proxy.corp:3128" }, null);
        BowireNetworkPolicy.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bowire:Network:ProxyUrl"] = "none" })
            .Build());

        var effective = BowireNetworkPolicy.For(null);
        Assert.Equal(BowireProxyMode.None, effective.Mode);
        Assert.Null(effective.ProxyFor(Remote));
    }

    [Fact]
    public void Proxy_Url_System_From_The_Command_Line_Overrides_A_Saved_Manual_Mode()
    {
        // `bowire --proxy-url system` names a mode. It has to beat the saved
        // "manual" mode, not just replace the saved URL and leave manual on.
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { Mode = "manual", ProxyUrl = "http://proxy.corp:3128" }, null);
        BowireNetworkPolicy.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bowire:Network:ProxyUrl"] = "system" })
            .Build());

        var effective = BowireNetworkPolicy.For(null);
        Assert.Equal(BowireProxyMode.System, effective.Mode);
        Assert.Equal("host", effective.Sources["mode"]);
        Assert.Empty(effective.Problems);
    }

    [Fact]
    public void A_Proxy_Url_Alone_Means_Manual_And_Loopback_And_The_Bypass_List_Go_Direct()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings
        {
            ProxyUrl = "http://proxy.corp:3128",
            NoProxy = ".internal.test",
        }, null);

        var effective = BowireNetworkPolicy.For(null);
        Assert.Equal(BowireProxyMode.Manual, effective.Mode);
        Assert.NotNull(effective.ProxyFor(Remote));
        Assert.Null(effective.ProxyFor(new Uri("http://localhost:5000/")));
        Assert.Null(effective.ProxyFor(new Uri("http://127.0.0.1:5000/")));
        Assert.Null(effective.ProxyFor(new Uri("https://billing.internal.test/")));
        Assert.Equal(new Uri("http://proxy.corp:3128"), effective.ProxyFor(new Uri("wss://api.example.test/socket")));
    }

    [Fact]
    public void Manual_Without_A_Url_Is_Reported_And_Goes_Direct()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { Mode = "manual" }, null);
        var effective = BowireNetworkPolicy.For(null);
        Assert.Null(effective.ProxyFor(Remote));
        Assert.Contains(effective.Problems, p => p.Contains("proxy URL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hunter2")]
    [InlineData("plain:hunter2")]
    public void A_Password_Is_Never_Accepted_Only_A_Reference(string passwordRef)
    {
        var ex = Assert.Throws<ArgumentException>(() => BowireNetworkSettingsStore.Save(
            new BowireNetworkSettings { ProxyUser = "alice", ProxyPasswordRef = passwordRef }, null));
        Assert.Contains("never the password", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(BowireNetworkSettingsStore.PathFor(null)));
    }

    [Fact]
    public void Credentials_In_The_Proxy_Url_Are_Refused()
    {
        Assert.Throws<ArgumentException>(() => BowireNetworkSettingsStore.Save(
            new BowireNetworkSettings { ProxyUrl = "http://alice:hunter2@proxy.corp:3128" }, null));
    }

    [Fact]
    public void The_Proxy_Password_Resolves_From_Its_Reference_And_Never_Lands_In_The_File()
    {
        var variable = "BOWIRE_TEST_PROXY_PW_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, "s3cret-value");
        try
        {
            BowireNetworkSettingsStore.Save(new BowireNetworkSettings
            {
                ProxyUrl = "http://proxy.corp:3128",
                ProxyUser = @"CORP\alice",
                ProxyPasswordRef = "env:" + variable,
            }, null);

            var file = File.ReadAllText(BowireNetworkSettingsStore.PathFor(null));
            Assert.DoesNotContain("s3cret-value", file, StringComparison.Ordinal);

            var credential = BowireNetworkPolicy.Proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:3128"), "Basic");
            Assert.NotNull(credential);
            Assert.Equal("alice", credential.UserName);
            Assert.Equal("CORP", credential.Domain);
            Assert.Equal("s3cret-value", credential.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void A_Keyring_Reference_Resolves_Through_The_Keyring_Module()
    {
        BowireNetworkPolicy.UseKeyring(reference => reference == "corp-proxy/alice" ? "from-keyring" : null);
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings
        {
            ProxyUrl = "http://proxy.corp:3128",
            ProxyUser = "alice",
            ProxyPasswordRef = "keyring:corp-proxy/alice",
        }, null);

        Assert.Equal("from-keyring",
            BowireNetworkPolicy.Proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:3128"), "Basic")!.Password);
    }

    [Fact]
    public void The_Ca_Bundle_Rescues_A_Chain_Error_And_Nothing_Else()
    {
        using var ca = CreateCa();
        using var leaf = CreateLeaf(ca, "api.example.test");

        // Without a bundle, a chain error stays an error.
        Assert.False(BowireNetworkPolicy.TrustsCustomCa(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));

        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { CaBundle = ca.ExportCertificatePem() }, null);
        Assert.True(BowireNetworkPolicy.TrustsCustomCa(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));

        // A wrong host name is not a trust question the bundle can answer.
        Assert.False(BowireNetworkPolicy.TrustsCustomCa(leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));

        // A certificate from some other CA is still rejected.
        using var other = CreateCa();
        using var stranger = CreateLeaf(other, "api.example.test");
        Assert.False(BowireNetworkPolicy.TrustsCustomCa(stranger, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_Ca_Bundle_Path_That_Does_Not_Exist_Is_Reported()
    {
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { CaBundle = Path.Combine(_root, "missing.pem") }, null);
        var effective = BowireNetworkPolicy.For(null);
        Assert.Null(effective.CaCertificates);
        Assert.Contains(effective.Problems, p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    internal static X509Certificate2 CreateCa()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Bowire Test Proxy CA " + Guid.NewGuid().ToString("N")[..6], key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    internal static X509Certificate2 CreateLeaf(X509Certificate2 ca, string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        if (host == "localhost") san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Round-trip through PFX: SslStream on Windows cannot use an ephemeral key.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
