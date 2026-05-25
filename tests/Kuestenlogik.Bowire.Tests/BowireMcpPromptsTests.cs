// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for <see cref="BowireMcpPrompts"/>. Each prompt renders
/// a single user-role <see cref="ChatMessage"/>; tests check the role
/// + that the supplied arguments land inside the rendered text so
/// the agent prompt is actionable.
/// </summary>
public sealed class BowireMcpPromptsTests
{
    [Fact]
    public void ReplayRecording_RendersUserRole_WithRecordingIdInBody()
    {
        var msg = BowireMcpPrompts.ReplayRecording(recordingId: "rec-42");

        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Contains("rec-42", msg.Text, StringComparison.Ordinal);
        Assert.Contains("bowire.record.list", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayRecording_TargetUrlOverride_LandsInPrompt()
    {
        var msg = BowireMcpPrompts.ReplayRecording(
            recordingId: "rec-7", targetUrl: "https://staging.example.com");

        Assert.Contains("https://staging.example.com", msg.Text, StringComparison.Ordinal);
        Assert.Contains("--url", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareResponses_PutsBothIdsInPrompt()
    {
        var msg = BowireMcpPrompts.CompareResponses(
            baselineId: "base-1", candidateId: "cand-2");

        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Contains("base-1", msg.Text, StringComparison.Ordinal);
        Assert.Contains("cand-2", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FuzzMethod_NamesServiceAndMethodInPrompt()
    {
        var msg = BowireMcpPrompts.FuzzMethod(
            url: "https://api.example.com",
            service: "weather.WeatherService",
            method: "GetCurrentWeather");

        Assert.Contains("weather.WeatherService/GetCurrentWeather", msg.Text, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", msg.Text, StringComparison.Ordinal);
        Assert.Contains("bowire.discover", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FuzzMethod_PayloadClass_LandsInPrompt()
    {
        var msg = BowireMcpPrompts.FuzzMethod(
            url: "https://api.example.com",
            service: "weather.WeatherService",
            method: "GetCurrentWeather",
            payloadClass: "string");

        Assert.Contains("`string` class", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanService_DefaultProfile_RendersFastInPrompt()
    {
        var msg = BowireMcpPrompts.ScanService(url: "https://api.example.com");

        Assert.Contains("--profile fast", msg.Text, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanService_FullProfile_RendersInPrompt()
    {
        var msg = BowireMcpPrompts.ScanService(
            url: "https://api.example.com", profile: "full");

        Assert.Contains("--profile full", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MockFromRecording_DefaultPort_StillProducesPrompt()
    {
        var msg = BowireMcpPrompts.MockFromRecording(recordingId: "rec-9");

        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Contains("rec-9", msg.Text, StringComparison.Ordinal);
        Assert.Contains("bowire.mock.start", msg.Text, StringComparison.Ordinal);
        Assert.Contains("bowire.mock.stop", msg.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MockFromRecording_CustomPort_LandsInPrompt()
    {
        var msg = BowireMcpPrompts.MockFromRecording(recordingId: "rec-9", port: 5555);

        Assert.Contains("port `5555`", msg.Text, StringComparison.Ordinal);
    }
}
