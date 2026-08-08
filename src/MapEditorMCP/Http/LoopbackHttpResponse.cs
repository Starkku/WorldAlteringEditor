using System.Net.Sockets;
using System.Text;

namespace MapEditorMCP.Http;

internal sealed class LoopbackHttpResponse
{
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(30.0);

    private readonly NetworkStream stream;
    private readonly TimeSpan writeTimeout;
    private readonly SemaphoreSlim startLock = new SemaphoreSlim(1, 1);
    private volatile bool headersSent;

    public LoopbackHttpResponse(NetworkStream stream, TimeSpan? writeTimeout = null)
    {
        this.stream = stream;
        this.writeTimeout = writeTimeout ?? DefaultWriteTimeout;
        if (this.writeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeTimeout), "The HTTP response write timeout must be positive.");

        stream.WriteTimeout = (int)Math.Min(int.MaxValue, Math.Ceiling(this.writeTimeout.TotalMilliseconds));
    }

    public int StatusCode { get; set; } = 200;
    public bool HasStarted { get; private set; }
    public bool HeadersSent => headersSent;
    public bool SuppressBody { get; set; }

    public Stream CreateSseBodyStream() => new DeferredSseResponseStream(this);

    public async Task EnsureSseStartedAsync(CancellationToken cancellationToken)
    {
        if (HasStarted)
            return;

        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasStarted)
                return;

            byte[] headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {StatusCode} {GetReasonPhrase(StatusCode)}\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache, no-store\r\n" +
                "Content-Encoding: identity\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n");

            HasStarted = true;
            await WriteToNetworkAsync(headers, cancellationToken).ConfigureAwait(false);
            headersSent = true;
        }
        finally
        {
            startLock.Release();
        }
    }

    public async Task WriteAsync(int statusCode, string contentType, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (HasStarted)
            throw new InvalidOperationException("The HTTP response has already started.");

        StatusCode = statusCode;
        string allowHeader = statusCode == 405 ? "Allow: POST\r\n" : string.Empty;
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            allowHeader +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n");

        HasStarted = true;
        await WriteToNetworkAsync(headers, cancellationToken).ConfigureAwait(false);
        headersSent = true;
        if (!SuppressBody && !body.IsEmpty)
            await WriteToNetworkAsync(body, cancellationToken).ConfigureAwait(false);

        await FlushNetworkAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WriteEmptyAsync(int statusCode, CancellationToken cancellationToken)
    {
        if (HasStarted)
            throw new InvalidOperationException("The HTTP response has already started.");

        StatusCode = statusCode;
        string allowHeader = statusCode == 405 ? "Allow: POST\r\n" : string.Empty;
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n" +
            "Content-Length: 0\r\n" +
            allowHeader +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        HasStarted = true;
        return WriteHeadersAndFlushAsync(headers, cancellationToken);
    }

    private async Task WriteHeadersAndFlushAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await WriteToNetworkAsync(bytes, cancellationToken).ConfigureAwait(false);
        headersSent = true;
        await FlushNetworkAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteToNetworkAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(writeTimeout);
        try
        {
            await stream.WriteAsync(bytes, timeoutCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Timed out while writing an HTTP response after {writeTimeout.TotalSeconds} seconds.");
        }
    }

    private async Task FlushNetworkAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(writeTimeout);
        try
        {
            await stream.FlushAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Timed out while flushing an HTTP response after {writeTimeout.TotalSeconds} seconds.");
        }
    }

    private static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        408 => "Request Timeout",
        411 => "Length Required",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        417 => "Expectation Failed",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        _ => "Unknown",
    };

    private sealed class DeferredSseResponseStream : Stream
    {
        private readonly LoopbackHttpResponse response;

        public DeferredSseResponseStream(LoopbackHttpResponse response)
        {
            this.response = response;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            response.EnsureSseStartedAsync(CancellationToken.None).GetAwaiter().GetResult();
            response.stream.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await response.EnsureSseStartedAsync(cancellationToken).ConfigureAwait(false);
            await response.FlushNetworkAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            response.EnsureSseStartedAsync(CancellationToken.None).GetAwaiter().GetResult();
            response.stream.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await response.EnsureSseStartedAsync(cancellationToken).ConfigureAwait(false);
            await response.WriteToNetworkAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
