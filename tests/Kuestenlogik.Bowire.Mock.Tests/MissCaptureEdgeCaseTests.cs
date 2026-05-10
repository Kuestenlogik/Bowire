// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Capture;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Edge cases for <see cref="MissCaptureWriter"/> beyond the happy paths
/// covered by <c>MissCaptureTests</c>: the empty-existing-file branch,
/// the non-recording-document rejection, hop-by-hop header stripping,
/// truncation when the body exceeds the 1 MiB cap, and gRPC capture
/// against an empty zero-length frame.
/// </summary>
public sealed class MissCaptureEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public MissCaptureEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-miss-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions s_readOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static BowireRecording ReadCapture(string path) =>
        JsonSerializer.Deserialize<BowireRecording>(File.ReadAllText(path), s_readOptions)!;

    [Fact]
    public async Task ExistingEmptyFile_TreatedAsFreshRecording()
    {
        // Whitespace-only files happen when the user `touch`es the path
        // before pointing the mock at it. The writer should treat the
        // file as a blank canvas rather than failing to deserialise.
        var path = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, "   \n  ", TestContext.Current.CancellationToken);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/probe";

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var captured = ReadCapture(path);
        Assert.Single(captured.Steps);
        Assert.Equal("/probe", captured.Steps[0].HttpPath);
    }

    [Fact]
    public async Task ExistingFileWithJsonNullLiteral_LogsWarningAndDoesNotClobber()
    {
        // The writer rejects files that deserialise to a null recording so
        // it never overwrites unrelated user data. The exception is caught
        // by CaptureAsync and logged; the file is left untouched.
        var path = Path.Combine(_tempDir, "null-recording.json");
        var literal = "null";
        await File.WriteAllTextAsync(path, literal, TestContext.Current.CancellationToken);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/x";

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        // File still carries the original payload — capture failed cleanly.
        Assert.Equal(literal, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestRequest_StripsHopByHopHeaders_FromCapturedMetadata()
    {
        var path = Path.Combine(_tempDir, "hop.json");
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/x";
        ctx.Request.Headers["X-Keep"] = "yes";
        ctx.Request.Headers["Connection"] = "close";
        ctx.Request.Headers["Keep-Alive"] = "timeout=5";
        ctx.Request.Headers["Transfer-Encoding"] = "chunked";
        ctx.Request.Headers["Upgrade"] = "websocket";
        ctx.Request.Headers["TE"] = "trailers";
        ctx.Request.Headers["Trailer"] = "Expires";
        ctx.Request.Headers["Proxy-Authorization"] = "Basic abc";
        ctx.Request.Headers["Proxy-Authenticate"] = "Basic";
        ctx.Request.Body = new MemoryStream();

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var step = ReadCapture(path).Steps[0];
        Assert.NotNull(step.Metadata);
        Assert.True(step.Metadata!.ContainsKey("X-Keep"));
        Assert.False(step.Metadata.ContainsKey("Connection"));
        Assert.False(step.Metadata.ContainsKey("Keep-Alive"));
        Assert.False(step.Metadata.ContainsKey("Transfer-Encoding"));
        Assert.False(step.Metadata.ContainsKey("Upgrade"));
        Assert.False(step.Metadata.ContainsKey("TE"));
        Assert.False(step.Metadata.ContainsKey("Trailer"));
        Assert.False(step.Metadata.ContainsKey("Proxy-Authorization"));
        Assert.False(step.Metadata.ContainsKey("Proxy-Authenticate"));
    }

    [Fact]
    public async Task RestRequest_BodyExceeds1MiB_TruncatesWithMarker()
    {
        var path = Path.Combine(_tempDir, "big.json");
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/big";
        ctx.Request.ContentType = "text/plain";
        // 1 MiB + 100 bytes of 'a' so the truncation branch triggers.
        var bodyStr = new string('a', (1024 * 1024) + 100);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyStr));

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var step = ReadCapture(path).Steps[0];
        Assert.NotNull(step.Body);
        Assert.Contains("truncated", step.Body!, StringComparison.Ordinal);
        Assert.True(step.Body.Length >= 1024 * 1024, "body should retain its first 1 MiB before the truncation marker");
    }

    [Fact]
    public async Task GrpcRequest_EmptyZeroLengthFrame_CapturesEmptyPayload()
    {
        // 5-byte envelope: compression=0, length=0, no payload bytes.
        var path = Path.Combine(_tempDir, "grpc-empty.json");
        var emptyFrame = new byte[5];
        emptyFrame[0] = 0; // no compression
        // length bytes already zero

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/svc.S/Empty";
        ctx.Request.ContentType = "application/grpc+proto";
        ctx.Request.Body = new MemoryStream(emptyFrame);

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var step = ReadCapture(path).Steps[0];
        Assert.Equal("grpc", step.Protocol);
        Assert.Equal("svc.S", step.Service);
        Assert.Equal("Empty", step.Method);
        Assert.Equal("", step.RequestBinary);
    }

    [Fact]
    public async Task GrpcRequest_ShortBody_AppendsStepWithoutRequestBinary()
    {
        // Path has only the service segment — method falls back to "Unknown".
        var path = Path.Combine(_tempDir, "grpc-short-path.json");
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/onlyservice";
        ctx.Request.ContentType = "application/grpc";
        ctx.Request.Body = new MemoryStream(new byte[] { 0x00, 0x00 }); // less than 5-byte header

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var step = ReadCapture(path).Steps[0];
        Assert.Equal("onlyservice", step.Service);
        Assert.Equal("Unknown", step.Method);
        Assert.Null(step.RequestBinary);
    }

    [Fact]
    public async Task RestRequest_NonSeekableBody_LeavesBodyEmpty()
    {
        // The writer's REST path only reads a body that is both readable
        // and seekable — non-seekable streams (the default for ASP.NET
        // requests when the caller hasn't enabled buffering) skip the
        // body-read branch entirely.
        var path = Path.Combine(_tempDir, "nonseek.json");
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/no-buffering";
        ctx.Request.ContentType = "text/plain";
        ctx.Request.Body = new NonSeekableMemoryStream(Encoding.UTF8.GetBytes("payload"));

        await MissCaptureWriter.CaptureAsync(path, ctx, NullLogger.Instance, TestContext.Current.CancellationToken);

        var step = ReadCapture(path).Steps[0];
        Assert.Null(step.Body);
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
