// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire;

/// <summary>
/// In-memory store for active <see cref="IBowireChannel"/> instances.
/// Channels auto-expire after 5 minutes of inactivity.
/// </summary>
internal static class ChannelStore
{
    private static readonly ConcurrentDictionary<string, IBowireChannel> Channels = new();

    public static void Add(IBowireChannel channel)
        => Channels[channel.Id] = channel;

    public static IBowireChannel? Get(string id)
        => Channels.TryGetValue(id, out var channel) ? channel : null;

    public static void Remove(string id)
        => Channels.TryRemove(id, out _);
}
