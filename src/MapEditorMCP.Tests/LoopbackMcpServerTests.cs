using MapEditorMCP.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MapEditorMCP.Tests;

[TestClass]
public sealed class LoopbackMcpServerTests
{
    [TestMethod]
    public async Task OfficialClientCanListAndCallTools()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = testServer.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        });

        await using McpClient client = await McpClient.CreateAsync(transport);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.IsTrue(tools.Any(tool => tool.Name == "echo"));
        var result = await client.CallToolAsync("echo", new Dictionary<string, object>
        {
            ["text"] = "hello",
        });
        Assert.AreEqual("hello", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text);
    }

    [TestMethod]
    public async Task OfficialClientCanUseInitializeHandshakeProtocol()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = testServer.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        });
        var clientOptions = new McpClientOptions
        {
            ProtocolVersion = McpHttpProtocol.November2025ProtocolVersion,
        };

        await using McpClient client = await McpClient.CreateAsync(transport, clientOptions);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.IsTrue(tools.Any(tool => tool.Name == "echo"));
    }

    [TestMethod]
    public async Task OfficialClientCanUseEndpointWithQueryString()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        var endpointWithQuery = new Uri(testServer.Endpoint + "?client=test");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpointWithQuery,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        });

        await using McpClient client = await McpClient.CreateAsync(transport);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.IsTrue(tools.Any(tool => tool.Name == "echo"));
    }

    [TestMethod]
    public async Task BrowserOriginIsRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.TryAddWithoutValidation("Origin", "https://example.invalid");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task WrongHostIsRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Host = "example.invalid";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task InvalidHostIsRejectedBeforeContinueOrBodyRead()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        string response = await SendRawRequestAsync(testServer.Port,
            "POST /mcp HTTP/1.1\r\n" +
            "Host: example.invalid\r\n" +
            "Content-Type: application/json\r\n" +
            "Accept: application/json, text/event-stream\r\n" +
            "Content-Length: 8388608\r\n" +
            "Expect: 100-continue\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 403 Forbidden");
        Assert.IsFalse(response.Contains("100 Continue", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DefaultHttpPortAllowsHostWithoutPortSuffix()
    {
        await using TestServer testServer = await TestServer.StartAsync(expectedHostPort: 80);
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Host = "127.0.0.1";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task GetIsRejectedWithMethodNotAllowed()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.GetAsync(testServer.Endpoint);
        string body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode, body);
        Assert.AreEqual("POST", string.Join(",", response.Content.Headers.Allow));
    }

    [TestMethod]
    public async Task ZeroQualityAcceptValuesAreRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=0, text/event-stream;q=0");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [TestMethod]
    public async Task MalformedAcceptQualityIsRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=bogus, text/event-stream");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [TestMethod]
    public async Task NonmatchingAcceptParametersCannotOverrideZeroQuality()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation(
            "Accept", "application/json;q=0, application/json;profile=x;q=1, text/event-stream");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [TestMethod]
    public async Task MoreSpecificZeroQualityOverridesGenericPositiveQuality()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation(
            "Accept", "application/json;q=1, application/json;charset=utf-8;q=0, text/event-stream");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [TestMethod]
    public async Task MalformedContentTypeIsRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; broken=");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    public async Task QuotedCommaCannotBypassZeroAcceptQuality()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new HttpClient();
        using var request = CreateJsonRequest(testServer.Endpoint, "{}");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation(
            "Accept", "application/json;foo=\"x,application/json,y\";q=0, text/event-stream");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [TestMethod]
    public async Task HeadResponseDoesNotContainABody()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        string response = await SendRawRequestAsync(testServer.Port,
            $"HEAD /mcp HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 405 Method Not Allowed");
        int bodyStart = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.IsTrue(bodyStart >= 0);
        Assert.AreEqual(string.Empty, response[(bodyStart + 4)..]);
    }

    [TestMethod]
    public async Task NonStringRoutingFieldReturnsInvalidParamsWithRequestId()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        const string json =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{" +
            "\"name\":123,\"arguments\":{},\"_meta\":{" +
            "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"," +
            "\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
        string response = await SendRawRequestAsync(testServer.Port,
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\n" +
            "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
            "MCP-Protocol-Version: 2026-07-28\r\nMcp-Method: tools/call\r\nMcp-Name: 123\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");

        StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request");
        StringAssert.Contains(response, "\"id\":1");
        StringAssert.Contains(response, "\"code\":-32602");
    }

    [TestMethod]
    public async Task ConflictingBodyLengthHeadersAreRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, testServer.Port);
        using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\n" +
            "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
            "Content-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n{}");

        await stream.WriteAsync(request);
        string response = await ReadToEndAsync(stream);

        StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request");
    }

    [TestMethod]
    public async Task RawTabInRequestTargetIsRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        string response = await SendRawRequestAsync(testServer.Port,
            $"POST /mcp?\tbad HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\nContent-Length: 0\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request");
    }

    [TestMethod]
    public async Task MalformedOriginFormTargetsAreRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        string[] invalidTargets = ["/mcp?%ZZ", "/mcp?x|y", "/mcp?x\\y"];

        foreach (string invalidTarget in invalidTargets)
        {
            string response = await SendRawRequestAsync(testServer.Port,
                $"POST {invalidTarget} HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\nContent-Length: 0\r\n\r\n");
            StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request", invalidTarget);
        }
    }

    [TestMethod]
    public async Task DuplicateHeadersAreRejected()
    {
        await using TestServer testServer = await TestServer.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, testServer.Port);
        using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{testServer.Port}\r\nHost: localhost:{testServer.Port}\r\n" +
            "Content-Length: 2\r\n\r\n{}");

        await stream.WriteAsync(request);
        string response = await ReadToEndAsync(stream);

        StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request");
    }

    [TestMethod]
    public async Task ServerShutdownCancelsRunningRequest()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved.TrySetResult(cancellationToken.IsCancellationRequested);
            }
        });
        await server.StartAsync();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(request);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(2.0));
        Assert.IsTrue(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2.0)));
        client.Dispose();
    }

    [TestMethod]
    public async Task CancellingBeforeResponseHeadersCancelsRequest()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved.TrySetResult(cancellationToken.IsCancellationRequested);
            }
        });
        await server.StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        using var requestCancellationTokenSource = new CancellationTokenSource();
        Task<HttpResponseMessage> responseTask = client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestCancellationTokenSource.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        requestCancellationTokenSource.Cancel();
        try
        {
            using HttpResponseMessage unexpectedResponse = await responseTask;
            Assert.Fail("The HTTP request unexpectedly completed after cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsTrue(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2.0)));
    }

    [TestMethod]
    public async Task HalfClosedClientCanReceiveResponse()
    {
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            await Task.Delay(100, cancellationToken);
            await response.WriteEmptyAsync(202, cancellationToken);
        }, cancelRequestOnReadEof: false);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n"));
        client.Client.Shutdown(SocketShutdown.Send);

        string response = await ReadHeadersOnlyAsync(stream);
        StringAssert.StartsWith(response, "HTTP/1.1 202 Accepted");
    }

    [TestMethod]
    public async Task ClosingActiveSseResponseCancelsRequest()
    {
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            Stream responseBody = response.CreateSseBodyStream();
            await responseBody.WriteAsync("event: ready\r\ndata: ready\r\n\r\n"u8.ToArray(), cancellationToken);
            responseStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved.TrySetResult(cancellationToken.IsCancellationRequested);
            }
        });
        await server.StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        HttpResponseMessage httpResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, httpResponse.StatusCode);
        await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        httpResponse.Dispose();

        Assert.IsTrue(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2.0)));
    }

    [TestMethod]
    public async Task PipelinedDataDoesNotCancelFirstResponse()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            handlerStarted.TrySetResult();
            await releaseHandler.Task.WaitAsync(cancellationToken);
            await response.WriteEmptyAsync(202, cancellationToken);
        });
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using NetworkStream stream = client.GetStream();
        string firstRequest = $"POST /first HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(firstRequest));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        string secondRequest = $"GET /second HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(secondRequest));
        releaseHandler.TrySetResult();

        string response = await ReadHeadersOnlyAsync(stream);
        StringAssert.StartsWith(response, "HTTP/1.1 202 Accepted");
    }

    [TestMethod]
    public async Task TrailingDataThenDisconnectCancelsRequest()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved.TrySetResult(cancellationToken.IsCancellationRequested);
            }
        });
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /first HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n"));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /second HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n"));
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.IsTrue(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2.0)));
    }

    [TestMethod]
    public async Task StalledResponseWriterTimesOut()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeTimedOut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            handlerStarted.TrySetResult();
            try
            {
                Stream responseBody = response.CreateSseBodyStream();
                byte[] block = new byte[1024 * 1024];
                while (true)
                    await responseBody.WriteAsync(block, cancellationToken);
            }
            catch (IOException)
            {
                writeTimedOut.TrySetResult(true);
                throw;
            }
            finally
            {
                writeTimedOut.TrySetResult(false);
            }
        }, TimeSpan.FromMilliseconds(250));
        await server.StartAsync();

        using var client = new TcpClient { ReceiveBufferSize = 1024 };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n"));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0));

        Assert.IsTrue(await writeTimedOut.Task.WaitAsync(TimeSpan.FromSeconds(5.0)));
    }

    [TestMethod]
    public async Task OverLimitClientsReceiveServiceUnavailable()
    {
        const int connectionCount = 16;
        var allHandlersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedHandlerCount = 0;
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            if (Interlocked.Increment(ref startedHandlerCount) == connectionCount)
                allHandlersStarted.TrySetResult();

            await releaseHandlers.Task.WaitAsync(cancellationToken);
            await response.WriteEmptyAsync(202, cancellationToken);
        });
        await server.StartAsync();

        var activeClients = new List<TcpClient>();
        try
        {
            string requestText = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 0\r\n\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(requestText);
            for (int index = 0; index < connectionCount; index++)
            {
                var activeClient = new TcpClient();
                activeClients.Add(activeClient);
                await activeClient.ConnectAsync(IPAddress.Loopback, server.Port);
                await activeClient.GetStream().WriteAsync(requestBytes);
            }

            await allHandlersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5.0));

            Task<string>[] rejectionTasks = Enumerable.Range(0, 5)
                .Select(_ => SendRawRequestAsync(server.Port, requestText))
                .ToArray();
            string[] rejectionResponses = await Task.WhenAll(rejectionTasks).WaitAsync(TimeSpan.FromSeconds(5.0));
            foreach (string rejectionResponse in rejectionResponses)
                StringAssert.StartsWith(rejectionResponse, "HTTP/1.1 503 Service Unavailable");

            releaseHandlers.TrySetResult();
            Task<string>[] activeResponseTasks = activeClients
                .Select(activeClient => ReadHeadersOnlyAsync(activeClient.GetStream()))
                .ToArray();
            string[] activeResponses = await Task.WhenAll(activeResponseTasks).WaitAsync(TimeSpan.FromSeconds(5.0));
            foreach (string activeResponse in activeResponses)
                StringAssert.StartsWith(activeResponse, "HTTP/1.1 202 Accepted");
        }
        finally
        {
            releaseHandlers.TrySetResult();
            foreach (TcpClient activeClient in activeClients)
                activeClient.Dispose();
        }
    }

    [TestMethod]
    public async Task ChunkedRequestBodyIsDecoded()
    {
        string receivedBody = string.Empty;
        await using var server = new LoopbackHttpServer(0, async (request, response, cancellationToken) =>
        {
            await request.ReadBodyAsync(cancellationToken);
            receivedBody = Encoding.UTF8.GetString(request.Body);
            await response.WriteEmptyAsync(202, cancellationToken);
        });
        await server.StartAsync();
        int port = server.Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "1;part=first\r\n{\r\n1; part=\"second value\"\r\n}\r\n0;done\r\n\r\n");

        await stream.WriteAsync(request);
        string response = await ReadToEndAsync(stream);

        StringAssert.StartsWith(response, "HTTP/1.1 202 Accepted");
        Assert.AreEqual("{}", receivedBody);
    }

    private static async Task<string> SendRawRequestAsync(int port, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestText));
        return await ReadToEndAsync(stream);
    }

    private static HttpRequestMessage CreateJsonRequest(Uri endpoint, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var response = new MemoryStream();
        await stream.CopyToAsync(response);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static async Task<string> ReadHeadersOnlyAsync(NetworkStream stream)
    {
        using var response = new MemoryStream();
        int matchedTerminatorBytes = 0;
        byte[] terminator = "\r\n\r\n"u8.ToArray();
        var buffer = new byte[1];
        while (matchedTerminatorBytes < terminator.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0)
                throw new IOException("The HTTP response ended before its headers were complete.");

            byte value = buffer[0];
            response.WriteByte(value);
            matchedTerminatorBytes = value == terminator[matchedTerminatorBytes]
                ? matchedTerminatorBytes + 1
                : value == terminator[0] ? 1 : 0;
        }

        return Encoding.ASCII.GetString(response.ToArray());
    }

    [McpServerToolType]
    public sealed class TestTools
    {
        [McpServerTool(Name = "echo", ReadOnly = true, OpenWorld = false)]
        [System.ComponentModel.Description("Returns the supplied text.")]
        public static string Echo(string text) => text;
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly ServiceProvider serviceProvider;
        private readonly LoopbackHttpServer server;

        private TestServer(ServiceProvider serviceProvider, LoopbackHttpServer server, int port)
        {
            this.serviceProvider = serviceProvider;
            this.server = server;
            Port = port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        }

        public int Port { get; }
        public Uri Endpoint { get; }
        public static async Task<TestServer> StartAsync(int? expectedHostPort = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddMcpServer().WithTools<TestTools>();
            ServiceProvider serviceProvider = services.BuildServiceProvider();

            var server = new LoopbackHttpServer(0, boundPort =>
            {
                var endpoint = new McpHttpEndpoint(
                    serviceProvider,
                    NullLoggerFactory.Instance,
                    expectedHostPort ?? boundPort,
                    "/mcp");
                return endpoint.HandleRequestAsync;
            });
            await server.StartAsync();
            int port = server.Port;
            return new TestServer(serviceProvider, server, port);
        }

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }

    }
}
