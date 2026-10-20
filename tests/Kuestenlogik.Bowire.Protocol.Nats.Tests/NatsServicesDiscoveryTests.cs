// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Models;
using NATS.Client.Core;

namespace Kuestenlogik.Bowire.Protocol.Nats.Tests;

/// <summary>
/// <c>NatsServicesDiscovery.ListAsync</c> against an in-memory broker that
/// keeps NATS's delivery rule: a message reaches only the subscriptions
/// the server knows about when it is published. A reply that arrives
/// before the reply-inbox subscription is registered is gone.
/// </summary>
public sealed class NatsServicesDiscoveryTests
{
    [Fact]
    public async Task ListAsync_Registers_The_Reply_Subscription_Before_Publishing_The_Ping()
    {
        // The responder answers $SRV.PING immediately, as a service on the
        // same server does. A subscription that is still being set up when
        // the PING goes out misses that answer; an enumerable subscription
        // gives the caller no signal for when it is registered, so the fake
        // registers it at the latest point the client allows — after the
        // publish. Discovery has to wait for its subscription first.
        var broker = new InMemoryBroker(pingReply: """{"name":"echo","id":"1","version":"1.0.0"}""");
        var conn = broker.CreateConnection();

        var services = await InvokeListAsync(conn, "nats://fake:4222", TimeSpan.FromMilliseconds(200));

        var svc = Assert.Single(services);
        Assert.Equal("Service:echo", svc.Name);
    }

    private static async Task<List<BowireServiceInfo>> InvokeListAsync(
        INatsConnection conn, string originUrl, TimeSpan scanDuration)
    {
        var type = typeof(BowireNatsProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Nats.NatsServicesDiscovery", throwOnError: true)!;
        var method = type.GetMethod("ListAsync", BindingFlags.Public | BindingFlags.Static)!;
        var task = (Task<List<BowireServiceInfo>>)method.Invoke(
            null, [conn, originUrl, TestContext.Current.CancellationToken, scanDuration])!;
        return await task;
    }

    /// <summary>
    /// Just enough of a NATS server for service discovery: subscriptions,
    /// publish-with-reply on <c>$SRV.PING</c>, and no responders for
    /// anything requested.
    /// </summary>
    private sealed class InMemoryBroker(string pingReply)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Channel<NatsMsg<byte[]>>> _subs = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public INatsConnection CreateConnection() =>
            CreateProxy<INatsConnection>((method, args) =>
            {
                switch (method.Name)
                {
                    case "PublishAsync":
                        Publish((string)args[0]!, args[3] as string);
                        return ValueTask.CompletedTask;
                    case "SubscribeAsync":
                        return SubscribeLazily((string)args[0]!, (CancellationToken)args[^1]!);
                    case "SubscribeCoreAsync":
                        return new ValueTask<INatsSub<byte[]>>(CreateSub((string)args[0]!));
                    case "RequestAsync":
                        throw new NatsNoRespondersException();
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });

        private INatsSub<byte[]> CreateSub(string subject)
        {
            var channel = Register(subject);
            return CreateProxy<INatsSub<byte[]>>((method, _) =>
            {
                switch (method.Name)
                {
                    case "get_Msgs":
                        return channel.Reader;
                    case "get_Subject":
                        return subject;
                    case "UnsubscribeAsync":
                    case "DisposeAsync":
                        Unregister(subject);
                        channel.Writer.TryComplete();
                        return ValueTask.CompletedTask;
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
        }

        private Channel<NatsMsg<byte[]>> Register(string subject)
        {
            var channel = Channel.CreateUnbounded<NatsMsg<byte[]>>();
            lock (_lock) _subs[subject] = channel;
            return channel;
        }

        private void Unregister(string subject)
        {
            lock (_lock) _subs.Remove(subject);
        }

        private void Publish(string subject, string? replyTo)
        {
            if (subject == "$SRV.PING" && replyTo is not null)
            {
                Channel<NatsMsg<byte[]>>? target;
                lock (_lock) _subs.TryGetValue(replyTo, out target);
                var data = Encoding.UTF8.GetBytes(pingReply);
                target?.Writer.TryWrite(new NatsMsg<byte[]>(replyTo, null, data.Length, null, data, null));
            }
            _published.TrySetResult();
        }

        private async IAsyncEnumerable<NatsMsg<byte[]>> SubscribeLazily(
            string subject, [EnumeratorCancellation] CancellationToken ct)
        {
            await _published.Task.WaitAsync(ct);
            var channel = Register(subject);
            try
            {
                await foreach (var msg in channel.Reader.ReadAllAsync(ct))
                    yield return msg;
            }
            finally
            {
                Unregister(subject);
            }
        }
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, HandlerProxy>();
        ((HandlerProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>Routes every interface call to a handler delegate.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types",
        Justification = "DispatchProxy.Create derives from the proxy type at run time and rejects a sealed one: "
            + "https://learn.microsoft.com/dotnet/api/system.reflection.dispatchproxy.create")]
    private class HandlerProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Handler(targetMethod, args ?? []);
        }
    }
}
