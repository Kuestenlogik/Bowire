// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.SignalR;

namespace Kuestenlogik.Bowire.IntegrationTests.Hubs;

internal sealed class ChatHub : Hub
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "SignalR binds hub methods only among public instance methods, so this cannot be static.")]
    public Task<string> Echo(string message)
    {
        return Task.FromResult($"Echo: {message}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "SignalR binds hub methods only among public instance methods, so this cannot be static.")]
    public async IAsyncEnumerable<int> Counter(int count, int delayMs)
    {
        for (int i = 1; i <= count; i++)
        {
            yield return i;
            await Task.Delay(delayMs);
        }
    }
}
