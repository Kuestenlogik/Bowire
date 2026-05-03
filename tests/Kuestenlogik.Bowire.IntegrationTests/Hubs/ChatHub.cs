// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.SignalR;

namespace Kuestenlogik.Bowire.IntegrationTests.Hubs;

public sealed class ChatHub : Hub
{
    public Task<string> Echo(string message)
    {
        return Task.FromResult($"Echo: {message}");
    }

    public async IAsyncEnumerable<int> Counter(int count, int delayMs)
    {
        for (int i = 1; i <= count; i++)
        {
            yield return i;
            await Task.Delay(delayMs);
        }
    }
}
