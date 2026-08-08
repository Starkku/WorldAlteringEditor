using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace MapEditorMCP.Http;

internal sealed class LoopbackHttpRequest
{
    private const int MaxRequestLineBytes = 4 * 1024;
    private const int MaxHeaderLineBytes = 8 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxHeaderCount = 64;
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int MaxChunkLineBytes = 128;

    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(30.0);

    private readonly BufferedNetworkReader reader;
    private readonly NetworkStream stream;
    private readonly int contentLength;
    private readonly bool isChunked;
    private readonly bool expectsContinue;
    private readonly TaskCompletionSource bodyReadCompletionSource =
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] body = Array.Empty<byte>();
    private bool bodyRead;

    private LoopbackHttpRequest(
        string method,
        string target,
        Dictionary<string, string> headers,
        NetworkStream stream,
        BufferedNetworkReader reader,
        int contentLength,
        bool isChunked,
        bool expectsContinue)
    {
        Method = method;
        Target = target;
        int queryIndex = target.IndexOf('?');
        Path = queryIndex < 0 ? target : target[..queryIndex];
        Headers = headers;
        this.stream = stream;
        this.reader = reader;
        this.contentLength = contentLength;
        this.isChunked = isChunked;
        this.expectsContinue = expectsContinue;
    }

    public string Method { get; }
    public string Target { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public byte[] Body => bodyRead
        ? body
        : throw new InvalidOperationException("The HTTP request body has not been read yet.");

    internal Task BodyReadCompletion => bodyReadCompletionSource.Task;

    public bool TryGetHeader(string name, out string value) => Headers.TryGetValue(name, out value);

    public static async Task<LoopbackHttpRequest> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var reader = new BufferedNetworkReader(stream);

        string requestLine;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int headerBytes = 0;

        using (var headerCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headerCancellationTokenSource.CancelAfter(HeaderReadTimeout);

            try
            {
                requestLine = await reader.ReadAsciiLineAsync(MaxRequestLineBytes, headerCancellationTokenSource.Token).ConfigureAwait(false);
                headerBytes = checked(requestLine.Length + 2);

                while (true)
                {
                    string line = await reader.ReadAsciiLineAsync(MaxHeaderLineBytes, headerCancellationTokenSource.Token).ConfigureAwait(false);
                    headerBytes = checked(headerBytes + line.Length + 2);
                    if (headerBytes > MaxHeaderBytes)
                        throw new HttpProtocolException(431, "Request headers are too large.");

                    if (line.Length == 0)
                        break;

                    if (headers.Count >= MaxHeaderCount)
                        throw new HttpProtocolException(431, "The request contains too many headers.");

                    ParseHeader(line, headers);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpProtocolException(408, "Timed out while reading request headers.");
            }
        }

        (string method, string target) = ParseRequestLine(requestLine);

        if (!headers.ContainsKey("Host"))
            throw new HttpProtocolException(400, "The Host header is required.");

        bool hasContentLength = headers.TryGetValue("Content-Length", out string contentLengthValue);
        bool hasTransferEncoding = headers.TryGetValue("Transfer-Encoding", out string transferEncodingValue);

        if (hasContentLength && hasTransferEncoding)
            throw new HttpProtocolException(400, "Content-Length and Transfer-Encoding cannot be combined.");

        int contentLength = 0;
        if (hasContentLength)
        {
            if (contentLengthValue.Length == 0 || contentLengthValue.Any(c => c is < '0' or > '9') ||
                !int.TryParse(contentLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
            {
                throw new HttpProtocolException(400, "Content-Length is invalid.");
            }

            if (contentLength > MaxBodyBytes)
                throw new HttpProtocolException(413, "The request body is too large.");
        }
        else if (hasTransferEncoding &&
                 !string.Equals(transferEncodingValue.Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpProtocolException(501, "Only chunked transfer encoding is supported.");
        }

        if (headers.ContainsKey("Trailer"))
            throw new HttpProtocolException(400, "HTTP trailers are not supported.");

        bool expectsContinue = false;
        if (headers.TryGetValue("Expect", out string expectValue))
        {
            if (!string.Equals(expectValue.Trim(), "100-continue", StringComparison.OrdinalIgnoreCase))
                throw new HttpProtocolException(417, "The requested expectation is not supported.");

            expectsContinue = true;
        }

        return new LoopbackHttpRequest(method, target, headers, stream, reader, contentLength, hasTransferEncoding, expectsContinue);
    }

    public async Task ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (bodyRead)
            return;

        if (expectsContinue)
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var bodyCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bodyCancellationTokenSource.CancelAfter(BodyReadTimeout);

            try
            {
                body = contentLength > 0
                    ? await reader.ReadExactlyAsync(contentLength, bodyCancellationTokenSource.Token).ConfigureAwait(false)
                    : isChunked
                        ? await ReadChunkedBodyAsync(reader, bodyCancellationTokenSource.Token).ConfigureAwait(false)
                        : Array.Empty<byte>();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpProtocolException(408, "Timed out while reading the request body.");
            }
        }

        bodyRead = true;
        bodyReadCompletionSource.TrySetResult();
    }

    private static (string Method, string Target) ParseRequestLine(string requestLine)
    {
        int firstSpace = requestLine.IndexOf(' ');
        int secondSpace = firstSpace < 0 ? -1 : requestLine.IndexOf(' ', firstSpace + 1);
        if (firstSpace <= 0 || secondSpace <= firstSpace + 1 || requestLine.IndexOf(' ', secondSpace + 1) >= 0)
            throw new HttpProtocolException(400, "The HTTP request line is invalid.");

        string method = requestLine[..firstSpace];
        string target = requestLine[(firstSpace + 1)..secondSpace];
        string version = requestLine[(secondSpace + 1)..];

        if (!method.All(IsTokenCharacter))
            throw new HttpProtocolException(400, "The HTTP method is invalid.");

        if (!IsValidOriginFormTarget(target))
            throw new HttpProtocolException(400, "Only origin-form HTTP request targets are supported.");

        if (!string.Equals(version, "HTTP/1.1", StringComparison.Ordinal))
            throw new HttpProtocolException(505, "Only HTTP/1.1 is supported.");

        return (method, target);
    }

    private static bool IsValidOriginFormTarget(string target)
    {
        if (target.Length == 0 || target[0] != '/')
            return false;

        bool parsingQuery = false;
        for (int index = 0; index < target.Length; index++)
        {
            char value = target[index];
            if (value <= 0x20 || value == 0x7F || value == '#')
                return false;

            if (value == '?')
            {
                parsingQuery = true;
                continue;
            }

            if (value == '%')
            {
                if (index + 2 >= target.Length || !IsHexDigit(target[index + 1]) || !IsHexDigit(target[index + 2]))
                    return false;

                index += 2;
                continue;
            }

            if (value == '/' || parsingQuery && value == '?' || IsPathCharacter(value))
                continue;

            return false;
        }

        return true;
    }

    private static bool IsPathCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            '-' or '.' or '_' or '~' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or ':' or '@';

    private static void ParseHeader(string line, Dictionary<string, string> headers)
    {
        if (line[0] is ' ' or '\t')
            throw new HttpProtocolException(400, "Folded HTTP headers are not supported.");

        int colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
            throw new HttpProtocolException(400, "An HTTP header is malformed.");

        string name = line[..colonIndex];
        if (!name.All(IsTokenCharacter))
            throw new HttpProtocolException(400, "An HTTP header name is invalid.");

        string value = line[(colonIndex + 1)..].Trim(' ', '\t');
        if (value.Any(c => c != '\t' && (c < 0x20 || c == 0x7F)))
            throw new HttpProtocolException(400, "An HTTP header value contains invalid control characters.");

        if (!headers.TryAdd(name, value))
        {
            if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                headers[name] = headers[name] + "," + value;
                return;
            }

            throw new HttpProtocolException(400, $"Duplicate {name} headers are not supported.");
        }
    }

    private static bool IsTokenCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static async Task<byte[]> ReadChunkedBodyAsync(BufferedNetworkReader reader, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            while (true)
            {
                string chunkSizeLine = await reader.ReadAsciiLineAsync(MaxChunkLineBytes, cancellationToken).ConfigureAwait(false);
                if (!TryParseChunkSizeLine(chunkSizeLine, out long chunkSize))
                    throw new HttpProtocolException(400, "A chunk size is invalid.");

                if (chunkSize == 0)
                {
                    string trailerTerminator = await reader.ReadAsciiLineAsync(MaxHeaderLineBytes, cancellationToken).ConfigureAwait(false);
                    if (trailerTerminator.Length != 0)
                        throw new HttpProtocolException(400, "HTTP trailers are not supported.");

                    return body.ToArray();
                }

                if (chunkSize > MaxBodyBytes - body.Length)
                    throw new HttpProtocolException(413, "The request body is too large.");

                long remaining = chunkSize;
                while (remaining > 0)
                {
                    int bytesToRead = (int)Math.Min(copyBuffer.Length, remaining);
                    await reader.ReadExactlyAsync(copyBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                    await body.WriteAsync(copyBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                    remaining -= bytesToRead;
                }

                await reader.ReadRequiredCrlfAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }
    }

    private static bool TryParseChunkSizeLine(string line, out long chunkSize)
    {
        chunkSize = 0;
        if (line.Length == 0)
            return false;

        int index = 0;
        int sizeStart = index;
        while (index < line.Length && IsHexDigit(line[index]))
            index++;

        if (index == sizeStart ||
            !long.TryParse(line.AsSpan(sizeStart, index - sizeStart), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out chunkSize) ||
            chunkSize < 0)
        {
            return false;
        }

        SkipOptionalWhitespace(line, ref index);
        while (index < line.Length)
        {
            if (line[index++] != ';')
                return false;

            SkipOptionalWhitespace(line, ref index);
            int nameStart = index;
            while (index < line.Length && IsTokenCharacter(line[index]))
                index++;

            if (index == nameStart)
                return false;

            SkipOptionalWhitespace(line, ref index);
            if (index >= line.Length || line[index] == ';')
                continue;

            if (line[index++] != '=')
                return false;

            SkipOptionalWhitespace(line, ref index);
            if (index >= line.Length)
                return false;

            if (line[index] == '"')
            {
                if (!TrySkipQuotedString(line, ref index))
                    return false;
            }
            else
            {
                int valueStart = index;
                while (index < line.Length && IsTokenCharacter(line[index]))
                    index++;

                if (index == valueStart)
                    return false;
            }

            SkipOptionalWhitespace(line, ref index);
        }

        return true;
    }

    private static bool TrySkipQuotedString(string text, ref int index)
    {
        index++;
        while (index < text.Length)
        {
            char value = text[index++];
            if (value == '"')
                return true;

            if (value == '\\')
            {
                if (index >= text.Length || !IsQuotedPairCharacter(text[index++]))
                    return false;
            }
            else if (!IsQuotedTextCharacter(value))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsQuotedTextCharacter(char value) =>
        value == '\t' || value == ' ' || value == '!' || value is >= '#' and <= '[' or >= ']' and <= '~';

    private static bool IsQuotedPairCharacter(char value) =>
        value == '\t' || value == ' ' || value is >= '!' and <= '~';

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static void SkipOptionalWhitespace(string text, ref int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t')
            index++;
    }

    private sealed class BufferedNetworkReader
    {
        private readonly NetworkStream stream;
        private readonly byte[] buffer = new byte[8192];
        private int bufferOffset;
        private int bufferLength;

        public BufferedNetworkReader(NetworkStream stream)
        {
            this.stream = stream;
        }

        public async Task<string> ReadAsciiLineAsync(int maximumLength, CancellationToken cancellationToken)
        {
            byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumLength));
            int lineLength = 0;

            try
            {
                while (true)
                {
                    int value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    if (value < 0)
                        throw new HttpProtocolException(400, "The HTTP request ended unexpectedly.");

                    if (value == '\r')
                    {
                        int next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                        if (next != '\n')
                            throw new HttpProtocolException(400, "HTTP lines must end with CRLF.");

                        return Encoding.ASCII.GetString(lineBuffer, 0, lineLength);
                    }

                    if (value == '\n')
                        throw new HttpProtocolException(400, "HTTP lines must end with CRLF.");

                    if (value > 0x7E || value < 0x20 && value != '\t')
                        throw new HttpProtocolException(400, "HTTP headers must contain ASCII characters only.");

                    if (lineLength >= maximumLength)
                        throw new HttpProtocolException(431, "An HTTP header line is too long.");

                    lineBuffer[lineLength++] = (byte)value;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lineBuffer);
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            var result = new byte[length];
            await ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            int written = 0;
            while (written < destination.Length)
            {
                if (bufferOffset < bufferLength)
                {
                    int bytesToCopy = Math.Min(bufferLength - bufferOffset, destination.Length - written);
                    buffer.AsMemory(bufferOffset, bytesToCopy).CopyTo(destination[written..]);
                    bufferOffset += bytesToCopy;
                    written += bytesToCopy;
                    continue;
                }

                int bytesRead = await stream.ReadAsync(destination[written..], cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    throw new HttpProtocolException(400, "The HTTP request body ended unexpectedly.");

                written += bytesRead;
            }
        }

        public async Task ReadRequiredCrlfAsync(CancellationToken cancellationToken)
        {
            int first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            int second = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (first != '\r' || second != '\n')
                throw new HttpProtocolException(400, "A chunk was not followed by CRLF.");
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (bufferOffset >= bufferLength)
            {
                bufferLength = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                bufferOffset = 0;
                if (bufferLength == 0)
                    return -1;
            }

            return buffer[bufferOffset++];
        }
    }
}

internal sealed class HttpProtocolException : Exception
{
    public HttpProtocolException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
