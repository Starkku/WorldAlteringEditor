using Rampastring.Tools;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MapEditorMCP.Http;

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const int ListenBacklog = 16;
    private const int MaxConcurrentConnections = 16;
    private const int MaxConcurrentRejections = 16;
    private const int MaxTrailingRequestBytes = 32 * 1024;
    private static readonly TimeSpan ConnectionDrainTimeout = TimeSpan.FromSeconds(1.0);

    private readonly int requestedPort;
    private Func<LoopbackHttpRequest, LoopbackHttpResponse, CancellationToken, Task> requestHandler;
    private readonly Func<int, Func<LoopbackHttpRequest, LoopbackHttpResponse, CancellationToken, Task>> requestHandlerFactory;
    private readonly TimeSpan? responseWriteTimeout;
    private readonly bool cancelRequestOnReadEof;
    private readonly CancellationTokenSource shutdownCancellationTokenSource = new CancellationTokenSource();
    private readonly SemaphoreSlim connectionLimit = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly SemaphoreSlim rejectionLimit = new SemaphoreSlim(MaxConcurrentRejections, MaxConcurrentRejections);
    private readonly ConcurrentDictionary<int, TcpClient> activeClients = new ConcurrentDictionary<int, TcpClient>();
    private readonly ConcurrentDictionary<int, Task> activeConnectionTasks = new ConcurrentDictionary<int, Task>();
    private readonly object lifecycleLock = new object();

    private TcpListener listener;
    private Task acceptLoopTask;
    private Task shutdownTask;
    private Task disposeTask;
    private int nextConnectionId;
    private bool disposed;

    public LoopbackHttpServer(
        int port,
        Func<LoopbackHttpRequest, LoopbackHttpResponse, CancellationToken, Task> requestHandler,
        TimeSpan? responseWriteTimeout = null,
        bool cancelRequestOnReadEof = true)
    {
        ArgumentNullException.ThrowIfNull(requestHandler);
        requestedPort = port;
        this.requestHandler = requestHandler;
        this.responseWriteTimeout = responseWriteTimeout;
        this.cancelRequestOnReadEof = cancelRequestOnReadEof;
    }

    public LoopbackHttpServer(
        int port,
        Func<int, Func<LoopbackHttpRequest, LoopbackHttpResponse, CancellationToken, Task>> requestHandlerFactory,
        TimeSpan? responseWriteTimeout = null,
        bool cancelRequestOnReadEof = true)
    {
        ArgumentNullException.ThrowIfNull(requestHandlerFactory);
        requestedPort = port;
        this.requestHandlerFactory = requestHandlerFactory;
        this.responseWriteTimeout = responseWriteTimeout;
        this.cancelRequestOnReadEof = cancelRequestOnReadEof;
    }

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (listener != null)
            return Task.CompletedTask;

        var newListener = new TcpListener(IPAddress.Loopback, requestedPort);
        newListener.Server.ExclusiveAddressUse = true;
        try
        {
            newListener.Start(ListenBacklog);
            int boundPort = ((IPEndPoint)newListener.LocalEndpoint).Port;
            Func<LoopbackHttpRequest, LoopbackHttpResponse, CancellationToken, Task> boundRequestHandler =
                requestHandler ?? requestHandlerFactory(boundPort);
            if (boundRequestHandler == null)
                throw new InvalidOperationException("The HTTP request handler factory returned null.");

            requestHandler = boundRequestHandler;
            listener = newListener;
            Port = boundPort;
            acceptLoopTask = AcceptConnectionsAsync(shutdownCancellationTokenSource.Token);
        }
        catch
        {
            newListener.Stop();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task task = EnsureShutdownStarted();
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleLock)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private Task EnsureShutdownStarted()
    {
        lock (lifecycleLock)
        {
            shutdownTask ??= ShutdownCoreAsync();
            return shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        if (!shutdownCancellationTokenSource.IsCancellationRequested)
            await shutdownCancellationTokenSource.CancelAsync().ConfigureAwait(false);

        listener?.Stop();

        if (acceptLoopTask != null)
        {
            try
            {
                await acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownCancellationTokenSource.IsCancellationRequested)
            {
            }
        }

        foreach (TcpClient client in activeClients.Values)
            client.Dispose();

        Task[] connectionTasks = activeConnectionTasks.Values.ToArray();
        if (connectionTasks.Length > 0)
            await Task.WhenAll(connectionTasks).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;

        try
        {
            await EnsureShutdownStarted().ConfigureAwait(false);
        }
        finally
        {
            listener = null;
            shutdownCancellationTokenSource.Dispose();
            connectionLimit.Dispose();
            rejectionLimit.Dispose();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!connectionLimit.Wait(0))
            {
                StartBusyClientRejection(client, cancellationToken);
                continue;
            }

            int connectionId = Interlocked.Increment(ref nextConnectionId);
            activeClients.TryAdd(connectionId, client);

            Task connectionTask = HandleClientAsync(connectionId, client, cancellationToken);
            TrackConnectionTask(connectionId, connectionTask);
        }
    }

    private void StartBusyClientRejection(TcpClient client, CancellationToken cancellationToken)
    {
        if (!rejectionLimit.Wait(0))
        {
            client.Dispose();
            return;
        }

        int connectionId = Interlocked.Increment(ref nextConnectionId);
        activeClients.TryAdd(connectionId, client);
        Task rejectionTask = RejectBusyClientAsync(connectionId, client, cancellationToken);
        TrackConnectionTask(connectionId, rejectionTask);
    }

    private void TrackConnectionTask(int connectionId, Task connectionTask)
    {
        activeConnectionTasks.TryAdd(connectionId, connectionTask);
        _ = connectionTask.ContinueWith(
            completedTask => activeConnectionTasks.TryRemove(connectionId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleClientAsync(int connectionId, TcpClient client, CancellationToken shutdownCancellationToken)
    {
        using (client)
        using (var requestCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(shutdownCancellationToken))
        using (var monitorCancellationTokenSource = new CancellationTokenSource())
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var response = new LoopbackHttpResponse(stream, responseWriteTimeout);
            CancellationToken requestCancellationToken = requestCancellationTokenSource.Token;
            Task disconnectMonitorTask = Task.CompletedTask;

            try
            {
                LoopbackHttpRequest request = await LoopbackHttpRequest.ReadHeadersAsync(stream, requestCancellationToken).ConfigureAwait(false);
                response.SuppressBody = string.Equals(request.Method, "HEAD", StringComparison.Ordinal);
                disconnectMonitorTask = MonitorClientDisconnectAsync(
                    client,
                    request.BodyReadCompletion,
                    response,
                    cancelRequestOnReadEof,
                    requestCancellationTokenSource,
                    monitorCancellationTokenSource.Token);

                await requestHandler(request, response, requestCancellationToken).ConfigureAwait(false);

                if (!response.HasStarted)
                    await WriteErrorAsync(response, 500, -32603, "The request completed without a response.", requestCancellationToken).ConfigureAwait(false);
            }
            catch (HttpProtocolException ex)
            {
                LogExternalRequestFailure(ex.StatusCode, ex.Message);
                if (!response.HasStarted)
                    await TryWriteErrorAsync(response, ex.StatusCode, -32600, ex.Message, requestCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException ex)
            {
                if (!requestCancellationToken.IsCancellationRequested)
                    Logger.Log("MCP HTTP connection ended unexpectedly. Returned error: " + ex.Message);
            }
            catch (SocketException ex)
            {
                if (!requestCancellationToken.IsCancellationRequested)
                    Logger.Log("MCP HTTP connection ended unexpectedly. Returned error: " + ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Log("MCP HTTP request failed. Returned error: " + ex);
                if (!response.HasStarted)
                    await TryWriteErrorAsync(response, 500, -32603, "The MCP server encountered an internal error.", requestCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await monitorCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                try
                {
                    await disconnectMonitorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (monitorCancellationTokenSource.IsCancellationRequested)
                {
                }

                if (response.HasStarted && !shutdownCancellationToken.IsCancellationRequested)
                    await ShutdownSendAndDrainAsync(client).ConfigureAwait(false);

                activeClients.TryRemove(connectionId, out _);
                connectionLimit.Release();
            }
        }
    }

    private static async Task ShutdownSendAndDrainAsync(TcpClient client)
    {
        try
        {
            Socket socket = client.Client;
            if (socket == null)
                return;

            socket.Shutdown(SocketShutdown.Send);

            using var drainCancellationTokenSource = new CancellationTokenSource(ConnectionDrainTimeout);
            byte[] buffer = new byte[4096];
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, drainCancellationTokenSource.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static async Task MonitorClientDisconnectAsync(
        TcpClient client,
        Task bodyReadCompletion,
        LoopbackHttpResponse response,
        bool cancelRequestOnReadEof,
        CancellationTokenSource requestCancellationTokenSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await bodyReadCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[4096];
            Socket socket = client.Client;
            if (socket == null)
            {
                await requestCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                return;
            }

            int trailingBytesRead = 0;
            while (true)
            {
                int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (cancelRequestOnReadEof || response.HeadersSent)
                        await requestCancellationTokenSource.CancelAsync().ConfigureAwait(false);

                    return;
                }

                trailingBytesRead = checked(trailingBytesRead + bytesRead);
                if (trailingBytesRead > MaxTrailingRequestBytes)
                {
                    await requestCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            await requestCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (SocketException)
        {
            await requestCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task RejectBusyClientAsync(int connectionId, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                try
                {
                    LogExternalRequestFailure(503, "The MCP server has too many open connections.");
                    var response = new LoopbackHttpResponse(client.GetStream(), responseWriteTimeout);
                    await WriteErrorAsync(response, 503, -32000, "The MCP server has too many open connections.", cancellationToken).ConfigureAwait(false);
                    await ShutdownSendAndDrainAsync(client).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
        }
        finally
        {
            activeClients.TryRemove(connectionId, out _);
            rejectionLimit.Release();
        }
    }

    private static void LogExternalRequestFailure(int statusCode, string message)
    {
        string safeMessage = new string(message
            .Take(512)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        Logger.Log($"MCP HTTP request rejected with status {statusCode}: {safeMessage}");
    }

    private static async Task TryWriteErrorAsync(
        LoopbackHttpResponse response,
        int statusCode,
        int errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteErrorAsync(response, statusCode, errorCode, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static Task WriteErrorAsync(
        LoopbackHttpResponse response,
        int statusCode,
        int errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = (object)null,
            error = new
            {
                code = errorCode,
                message,
            },
        });

        return response.WriteAsync(statusCode, "application/json; charset=utf-8", body, cancellationToken);
    }
}
