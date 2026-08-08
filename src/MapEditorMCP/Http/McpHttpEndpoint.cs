// Portions of the MCP HTTP validation and dispatch logic are adapted from the official
// Model Context Protocol C# SDK 2.1.0, licensed under Apache-2.0 and the MIT license
// (as of writing this, they are transitioning from MIT to Apache-2.0).
// https://github.com/modelcontextprotocol/csharp-sdk/blob/3be47ee31ede99ef9025af02d9eb37da938d7f05/LICENSE

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WaeLogger = Rampastring.Tools.Logger;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace MapEditorMCP.Http;

internal sealed class McpHttpEndpoint
{
    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo = GetRequiredJsonTypeInfo<JsonRpcMessage>();
    private static readonly JsonTypeInfo<JsonRpcError> ErrorTypeInfo = GetRequiredJsonTypeInfo<JsonRpcError>();
    private static readonly SearchValues<char> ValidHeaderValueCharacters =
        SearchValues.Create("\t !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~");

    private const long MaxSafeInteger = 9_007_199_254_740_991L;

    private readonly IServiceProvider services;
    private readonly IOptionsFactory<McpServerOptions> optionsFactory;
    private readonly ILoggerFactory loggerFactory;
    private readonly string expectedHost;
    private readonly string expectedLocalhost;
    private readonly bool allowOmittedHostPort;
    private readonly string path;

    public McpHttpEndpoint(IServiceProvider services, ILoggerFactory loggerFactory, int port, string path)
    {
        this.services = services;
        this.loggerFactory = loggerFactory;
        optionsFactory = services.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        expectedHost = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        expectedLocalhost = $"localhost:{port.ToString(CultureInfo.InvariantCulture)}";
        allowOmittedHostPort = port == 80;
        this.path = path;
    }

    public async Task HandleRequestAsync(
        LoopbackHttpRequest request,
        LoopbackHttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetHeader("Host", out string host) || !IsExpectedHost(host))
        {
            await WriteJsonRpcErrorAsync(response, "Forbidden: The Host header must identify the loopback MCP endpoint.",
                403, cancellationToken).ConfigureAwait(false);
            return;
        }

        // WAE does not expose a browser application, so no browser Origin is trusted. Native MCP
        // clients do not send this header. Rejecting it prevents DNS-rebinding and cross-origin use.
        if (request.Headers.ContainsKey("Origin"))
        {
            await WriteJsonRpcErrorAsync(response, "Forbidden: Browser-originated requests are not accepted.",
                403, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Path, path, StringComparison.Ordinal))
        {
            await WriteJsonRpcErrorAsync(response, "Not Found: The requested MCP endpoint does not exist.",
                404, cancellationToken, (int)McpErrorCode.MethodNotFound).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
        {
            await WriteJsonRpcErrorAsync(response, "Method Not Allowed: The MCP endpoint only accepts POST requests.",
                405, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!request.TryGetHeader("Content-Type", out string contentType) ||
            !MatchesMediaType(contentType, "application/json"))
        {
            await WriteJsonRpcErrorAsync(response, "Unsupported Media Type: The request body must be application/json.",
                415, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!request.TryGetHeader("Accept", out string accept) ||
            !ContainsAcceptableMediaType(accept, "application/json; charset=utf-8") ||
            !ContainsAcceptableMediaType(accept, "text/event-stream"))
        {
            await WriteJsonRpcErrorAsync(response,
                "Not Acceptable: Client must accept both application/json and text/event-stream.",
                406, cancellationToken).ConfigureAwait(false);
            return;
        }

        await request.ReadBodyAsync(cancellationToken).ConfigureAwait(false);

        JsonRpcMessage message;
        try
        {
            message = JsonSerializer.Deserialize(request.Body, MessageTypeInfo);
        }
        catch (JsonException)
        {
            await WriteJsonRpcErrorAsync(response,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                400, cancellationToken, (int)McpErrorCode.InvalidRequest).ConfigureAwait(false);
            return;
        }

        if (message == null)
        {
            await WriteJsonRpcErrorAsync(response,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                400, cancellationToken, (int)McpErrorCode.InvalidRequest).ConfigureAwait(false);
            return;
        }

        RequestId requestId = message is JsonRpcRequest jsonRpcRequest ? jsonRpcRequest.Id : default;
        string protocolVersion = GetHeader(request, McpHttpProtocol.ProtocolVersionHeader);
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            message.Context ??= new JsonRpcMessageContext();
            message.Context.ProtocolVersion = protocolVersion;
        }

        using IServiceScope requestScope = services.CreateScope();
        McpServerOptions serverOptions = optionsFactory.Create(Options.DefaultName);
        serverOptions.ScopeRequests = false;

        IReadOnlyList<string> supportedProtocolVersions = GetConfiguredSupportedProtocolVersions(serverOptions.ProtocolVersion);
        if (!ValidateProtocolVersionHeader(protocolVersion, supportedProtocolVersions, out JsonRpcErrorDetail protocolVersionError))
        {
            await WriteJsonRpcErrorDetailAsync(response, protocolVersionError, 400, cancellationToken, requestId).ConfigureAwait(false);
            return;
        }

        if (!ValidateProtocolVersionEnvelope(protocolVersion, message, out JsonRpcErrorDetail protocolEnvelopeError))
        {
            await WriteJsonRpcErrorDetailAsync(response, protocolEnvelopeError, 400, cancellationToken, requestId).ConfigureAwait(false);
            return;
        }

        if (!ValidateMcpHeaders(request, message, serverOptions, protocolVersion,
            out string headerError, out int headerErrorCode))
        {
            await WriteJsonRpcErrorAsync(response, headerError, 400, cancellationToken,
                headerErrorCode, requestId).ConfigureAwait(false);
            return;
        }

        if (!ValidateRequiredPerRequestMeta(protocolVersion, message, out JsonRpcErrorDetail requiredMetaError))
        {
            await WriteJsonRpcErrorDetailAsync(response, requiredMetaError, 400, cancellationToken, requestId).ConfigureAwait(false);
            return;
        }

#pragma warning disable MCP9005
        if (McpHttpProtocol.RequiresPerRequestMetadata(protocolVersion) &&
            message is JsonRpcRequest
            {
                Method: RequestMethods.Initialize or RequestMethods.Ping or RequestMethods.LoggingSetLevel
                    or RequestMethods.ResourcesSubscribe or RequestMethods.ResourcesUnsubscribe,
            } removedMethodRequest)
#pragma warning restore MCP9005
        {
            await WriteJsonRpcErrorAsync(response,
                $"Method '{removedMethodRequest.Method}' is not available on protocol version '{protocolVersion}'.",
                404, cancellationToken, (int)McpErrorCode.MethodNotFound, requestId).ConfigureAwait(false);
            return;
        }

        if (!McpHttpProtocol.RequiresPerRequestMetadata(protocolVersion) &&
            request.Headers.ContainsKey(McpHttpProtocol.SessionIdHeader))
        {
            await WriteJsonRpcErrorAsync(response,
                "Bad Request: The Mcp-Session-Id header is not supported by this stateless server.",
                400, cancellationToken, requestId: requestId).ConfigureAwait(false);
            return;
        }

        await RunMcpRequestAsync(message, serverOptions, requestScope.ServiceProvider, response,
            protocolVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunMcpRequestAsync(
        JsonRpcMessage message,
        McpServerOptions serverOptions,
        IServiceProvider requestServices,
        LoopbackHttpResponse response,
        string protocolVersion,
        CancellationToken cancellationToken)
    {
        var transport = new StreamableHttpServerTransport(loggerFactory)
        {
            Stateless = true,
        };
        McpServer server = McpServer.Create(transport, serverOptions, loggerFactory, requestServices);
        Task serverRunTask = server.RunAsync(cancellationToken);

        try
        {
            Func<JsonRpcMessage, ValueTask> onResponseStarting = null;
            if (McpHttpProtocol.RequiresPerRequestMetadata(protocolVersion))
            {
                onResponseStarting = firstMessage =>
                {
                    if (firstMessage is JsonRpcError { Error: { } errorDetail } && !response.HasStarted)
                    {
                        response.StatusCode = (McpErrorCode)errorDetail.Code switch
                        {
                            McpErrorCode.MethodNotFound => 404,
                            McpErrorCode.MissingRequiredClientCapability => 400,
                            McpErrorCode.UnsupportedProtocolVersion => 400,
                            McpErrorCode.HeaderMismatch => 400,
                            _ => response.StatusCode,
                        };
                    }

                    return default;
                };
            }

            Stream responseBody = response.CreateSseBodyStream();
            bool wroteResponse = await transport.HandlePostRequestAsync(
                message, responseBody, onResponseStarting, cancellationToken).ConfigureAwait(false);

            if (!wroteResponse)
            {
                if (!response.HasStarted)
                    await response.WriteEmptyAsync(202, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await response.EnsureSseStartedAsync(cancellationToken).ConfigureAwait(false);
                await responseBody.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                try
                {
                    await serverRunTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool ValidateRequiredPerRequestMeta(
        string protocolVersion,
        JsonRpcMessage message,
        [NotNullWhen(false)] out JsonRpcErrorDetail errorDetail)
    {
        if (message is JsonRpcRequest { Params: var requestParams } &&
            McpHttpProtocol.RequiresPerRequestMetadata(protocolVersion) &&
            (requestParams is not JsonObject paramsObject ||
             paramsObject["_meta"] is not JsonObject metaObject ||
             metaObject[MetaKeys.ClientCapabilities] is not JsonObject))
        {
            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.InvalidParams,
                Message = $"Requests using protocol version '{protocolVersion}' must include '_meta/{MetaKeys.ClientCapabilities}' as a JSON object.",
            };
            return false;
        }

        errorDetail = null;
        return true;
    }

    private static bool ValidateProtocolVersionHeader(
        string protocolVersion,
        IReadOnlyList<string> supportedProtocolVersions,
        [NotNullWhen(false)] out JsonRpcErrorDetail errorDetail)
    {
        if (!string.IsNullOrEmpty(protocolVersion) && !supportedProtocolVersions.Contains(protocolVersion))
        {
            string[] metadataVersions = supportedProtocolVersions
                .Where(McpHttpProtocol.RequiresPerRequestMetadata)
                .ToArray();
            IReadOnlyList<string> advertisedVersions = metadataVersions.Length > 0
                ? metadataVersions
                : supportedProtocolVersions;

            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.UnsupportedProtocolVersion,
                Message = $"Bad Request: The MCP-Protocol-Version header value '{protocolVersion}' is not supported.",
                Data = JsonSerializer.SerializeToNode(
                    new UnsupportedProtocolVersionErrorData
                    {
                        Supported = [.. advertisedVersions],
                        Requested = protocolVersion,
                    },
                    GetRequiredJsonTypeInfo<UnsupportedProtocolVersionErrorData>()),
            };
            return false;
        }

        errorDetail = null;
        return true;
    }

    private static string[] GetConfiguredSupportedProtocolVersions(string protocolVersion)
    {
        if (protocolVersion == null)
            return McpHttpProtocol.SupportedProtocolVersions;

        if (!McpHttpProtocol.IsSupportedProtocolVersion(protocolVersion))
        {
            throw new McpException(
                $"Unsupported server protocol version '{protocolVersion}'. Supported protocol versions: " +
                string.Join(", ", McpHttpProtocol.SupportedProtocolVersions) + ".");
        }

        return [protocolVersion];
    }

    private static bool ValidateProtocolVersionEnvelope(
        string protocolVersionHeader,
        JsonRpcMessage message,
        [NotNullWhen(false)] out JsonRpcErrorDetail errorDetail)
    {
        if (message is not (JsonRpcRequest or JsonRpcNotification))
        {
            errorDetail = null;
            return true;
        }

        if (message is JsonRpcRequest { Method: RequestMethods.Initialize, Params: JsonObject initializeParams } &&
            initializeParams["protocolVersion"] is JsonValue initializeProtocolVersionValue &&
            initializeProtocolVersionValue.TryGetValue(out string initializeProtocolVersion) &&
            !string.IsNullOrEmpty(protocolVersionHeader) &&
            !string.Equals(protocolVersionHeader, initializeProtocolVersion, StringComparison.Ordinal))
        {
            errorDetail = CreateHeaderMismatchError(
                $"Bad Request: The {McpHttpProtocol.ProtocolVersionHeader} header value '{protocolVersionHeader}' does not match body params.protocolVersion value '{initializeProtocolVersion}'.");
            return false;
        }

        bool hasProtocolVersionMeta = TryGetProtocolVersionMeta(message, out string protocolVersionMeta);
        if (!McpHttpProtocol.RequiresPerRequestMetadata(protocolVersionHeader) &&
            !McpHttpProtocol.RequiresPerRequestMetadata(protocolVersionMeta))
        {
            errorDetail = null;
            return true;
        }

        if (string.IsNullOrEmpty(protocolVersionHeader))
        {
            errorDetail = CreateHeaderMismatchError(
                $"Bad Request: The {McpHttpProtocol.ProtocolVersionHeader} header is required when the request body declares a per-request metadata protocol version.");
            return false;
        }

        if (!hasProtocolVersionMeta)
        {
            if (message is not JsonRpcRequest)
            {
                errorDetail = null;
                return true;
            }

            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.InvalidParams,
                Message = $"Requests using protocol version '{protocolVersionHeader}' must include '_meta/{MetaKeys.ProtocolVersion}'.",
            };
            return false;
        }

        if (!string.Equals(protocolVersionHeader, protocolVersionMeta, StringComparison.Ordinal))
        {
            errorDetail = CreateHeaderMismatchError(
                $"Bad Request: The {McpHttpProtocol.ProtocolVersionHeader} header value '{protocolVersionHeader}' does not match body _meta/{MetaKeys.ProtocolVersion} value '{protocolVersionMeta}'.");
            return false;
        }

        errorDetail = null;
        return true;
    }

    private static bool TryGetProtocolVersionMeta(JsonRpcMessage message, [NotNullWhen(true)] out string protocolVersion)
    {
        JsonNode parameters = message switch
        {
            JsonRpcRequest request => request.Params,
            JsonRpcNotification notification => notification.Params,
            _ => null,
        };

        if (parameters is JsonObject paramsObject &&
            paramsObject["_meta"] is JsonObject metaObject &&
            metaObject[MetaKeys.ProtocolVersion] is JsonValue protocolVersionValue &&
            protocolVersionValue.TryGetValue(out string value) &&
            !string.IsNullOrEmpty(value))
        {
            protocolVersion = value;
            return true;
        }

        protocolVersion = null;
        return false;
    }

    private static JsonRpcErrorDetail CreateHeaderMismatchError(string message) => new JsonRpcErrorDetail
    {
        Code = (int)McpErrorCode.HeaderMismatch,
        Message = message,
    };

    private static bool ValidateMcpHeaders(
        LoopbackHttpRequest request,
        JsonRpcMessage message,
        McpServerOptions serverOptions,
        string protocolVersion,
        [NotNullWhen(false)] out string errorMessage,
        out int errorCode)
    {
        errorCode = (int)McpErrorCode.HeaderMismatch;
        if (!McpHttpProtocol.RequiresStandardHeaders(protocolVersion) ||
            message is not (JsonRpcRequest or JsonRpcNotification))
        {
            errorMessage = null;
            return true;
        }

        if (!request.TryGetHeader(McpHttpProtocol.MethodHeader, out string methodHeader))
        {
            errorMessage = $"Missing required {McpHttpProtocol.MethodHeader} header.";
            return false;
        }

        string method = message switch
        {
            JsonRpcRequest jsonRpcRequest => jsonRpcRequest.Method,
            JsonRpcNotification notification => notification.Method,
            _ => null,
        };

        methodHeader = methodHeader.Trim();
        if (!string.Equals(methodHeader, method, StringComparison.Ordinal))
        {
            errorMessage = $"Header mismatch: {McpHttpProtocol.MethodHeader} header value '{methodHeader}' does not match body value '{method}'.";
            return false;
        }

#pragma warning disable MCPEXP002
        string routingNameParameter = GetRoutingNameParameter(method, serverOptions.RequestHandlers);
#pragma warning restore MCPEXP002
        if (routingNameParameter == null)
        {
            errorMessage = null;
            return true;
        }

        if (!request.TryGetHeader(McpHttpProtocol.NameHeader, out string nameHeader))
        {
            errorMessage = $"Missing required {McpHttpProtocol.NameHeader} header.";
            return false;
        }

        nameHeader = nameHeader.Trim();
        if (!IsValidHeaderValue(nameHeader))
        {
            errorMessage = $"Header mismatch: {McpHttpProtocol.NameHeader} header contains invalid characters.";
            return false;
        }

        string decodedName = McpHeaderEncoder.DecodeValue(nameHeader);
        if (decodedName == null)
        {
            errorMessage = $"Header mismatch: {McpHttpProtocol.NameHeader} header contains invalid Base64 encoding.";
            return false;
        }

        JsonNode bodyParams = message switch
        {
            JsonRpcRequest jsonRpcRequest => jsonRpcRequest.Params,
            JsonRpcNotification notification => notification.Params,
            _ => null,
        };
        if (!TryGetJsonNodeStringProperty(bodyParams, routingNameParameter, out string bodyName))
        {
            errorMessage = $"Invalid params: body parameter '{routingNameParameter}' must be a string.";
            errorCode = (int)McpErrorCode.InvalidParams;
            return false;
        }

        if (!string.Equals(decodedName, bodyName, StringComparison.Ordinal))
        {
            errorMessage = $"Header mismatch: {McpHttpProtocol.NameHeader} header value '{nameHeader}' does not match body value '{bodyName}'.";
            return false;
        }

        return ValidateCustomParameterHeaders(request, message, serverOptions.ToolCollection, out errorMessage);
    }

#pragma warning disable MCPEXP002
    private static string GetRoutingNameParameter(string method, IList<McpServerRequestHandler> requestHandlers)
    {
        string builtInParameter = method switch
        {
            RequestMethods.ToolsCall or RequestMethods.PromptsGet => "name",
            RequestMethods.ResourcesRead => "uri",
            _ => null,
        };

        if (builtInParameter != null)
            return builtInParameter;

        if (requestHandlers != null)
        {
            foreach (McpServerRequestHandler requestHandler in requestHandlers)
            {
                if (string.Equals(requestHandler.Method, method, StringComparison.Ordinal))
                    return requestHandler.RoutingNameParameter;
            }
        }

        return null;
    }
#pragma warning restore MCPEXP002

    private static bool ValidateCustomParameterHeaders(
        LoopbackHttpRequest request,
        JsonRpcMessage message,
        McpServerPrimitiveCollection<McpServerTool> toolCollection,
        [NotNullWhen(false)] out string errorMessage)
    {
        if (message is not JsonRpcRequest { Method: RequestMethods.ToolsCall, Params: { } bodyParams })
        {
            errorMessage = null;
            return true;
        }

        string toolName = TryGetJsonNodeStringProperty(bodyParams, "name", out string bodyToolName)
            ? bodyToolName
            : null;
        if (toolName == null || toolCollection == null || !toolCollection.TryGetPrimitive(toolName, out McpServerTool tool))
        {
            errorMessage = null;
            return true;
        }

        JsonElement inputSchema = tool.ProtocolTool.InputSchema;
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            errorMessage = null;
            return true;
        }

        JsonNode arguments = null;
        if (bodyParams is JsonObject paramsObject)
            paramsObject.TryGetPropertyValue("arguments", out arguments);

        return ValidateCustomParameterHeadersFromProperties(request, properties, arguments, out errorMessage);
    }

    private static bool ValidateCustomParameterHeadersFromProperties(
        LoopbackHttpRequest request,
        JsonElement properties,
        JsonNode arguments,
        [NotNullWhen(false)] out string errorMessage)
    {
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (property.Value.TryGetProperty("properties", out JsonElement nestedProperties) &&
                nestedProperties.ValueKind == JsonValueKind.Object)
            {
                JsonNode nestedArguments = null;
                if (arguments is JsonObject parentObject)
                    parentObject.TryGetPropertyValue(property.Name, out nestedArguments);

                if (!ValidateCustomParameterHeadersFromProperties(request, nestedProperties, nestedArguments, out errorMessage))
                    return false;
            }

            if (!property.Value.TryGetProperty("x-mcp-header", out JsonElement headerNameElement))
                continue;

            string headerName = headerNameElement.GetString();
            if (string.IsNullOrEmpty(headerName))
                continue;

            string fullHeaderName = McpHttpProtocol.ParameterHeaderPrefix + headerName;
            if (!request.TryGetHeader(fullHeaderName, out string actualHeaderValue))
            {
                bool hasNonNullBodyValue = arguments is JsonObject argumentsForMissing &&
                    argumentsForMissing.TryGetPropertyValue(property.Name, out JsonNode missingArgument) &&
                    missingArgument != null && missingArgument.GetValueKind() != JsonValueKind.Null;

                if (hasNonNullBodyValue)
                {
                    errorMessage = $"Missing required {fullHeaderName} header for parameter '{property.Name}' annotated with x-mcp-header.";
                    return false;
                }

                continue;
            }

            actualHeaderValue = actualHeaderValue.Trim();
            if (!IsValidHeaderValue(actualHeaderValue))
            {
                errorMessage = $"Header mismatch: {fullHeaderName} header contains invalid characters.";
                return false;
            }

            string decodedActual = McpHeaderEncoder.DecodeValue(actualHeaderValue);
            if (decodedActual == null)
            {
                errorMessage = $"Header mismatch: {fullHeaderName} header contains invalid Base64 encoding.";
                return false;
            }

            if (arguments is JsonObject argumentsObject &&
                argumentsObject.TryGetPropertyValue(property.Name, out JsonNode argument) && argument != null)
            {
                string expectedHeaderValue = McpHeaderEncoder.ConvertToHeaderValue(argument);
                if (expectedHeaderValue != null)
                {
                    string decodedExpected = McpHeaderEncoder.DecodeValue(expectedHeaderValue);
                    switch (CompareHeaderValues(decodedActual, decodedExpected, property.Value))
                    {
                        case HeaderValueComparison.IntegerOutOfRange:
                            errorMessage = $"Header mismatch: {fullHeaderName} integer value for parameter '{property.Name}' is outside the JavaScript safe integer range.";
                            return false;
                        case HeaderValueComparison.Mismatch:
                            errorMessage = $"Header mismatch: {fullHeaderName} header value does not match body argument '{property.Name}'.";
                            return false;
                    }
                }
            }
        }

        errorMessage = null;
        return true;
    }

    private static HeaderValueComparison CompareHeaderValues(string actual, string expected, JsonElement propertySchema)
    {
        if (actual != null && expected != null && SchemaTypeIsInteger(propertySchema))
        {
            SafeIntegerParse actualResult = ParseSafeInteger(actual, out long actualValue);
            SafeIntegerParse expectedResult = ParseSafeInteger(expected, out long expectedValue);

            if (actualResult == SafeIntegerParse.OutOfRange || expectedResult == SafeIntegerParse.OutOfRange)
                return HeaderValueComparison.IntegerOutOfRange;

            if (actualResult == SafeIntegerParse.SafeInteger && expectedResult == SafeIntegerParse.SafeInteger)
                return actualValue == expectedValue ? HeaderValueComparison.Match : HeaderValueComparison.Mismatch;

            if (actualResult == SafeIntegerParse.NonInteger || expectedResult == SafeIntegerParse.NonInteger)
                return HeaderValueComparison.Mismatch;
        }

        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? HeaderValueComparison.Match
            : HeaderValueComparison.Mismatch;
    }

    private static bool SchemaTypeIsInteger(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out JsonElement typeElement))
            return false;

        if (typeElement.ValueKind == JsonValueKind.String)
            return typeElement.ValueEquals("integer");

        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in typeElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.ValueEquals("integer"))
                    return true;
            }
        }

        return false;
    }

    private static SafeIntegerParse ParseSafeInteger(string text, out long value)
    {
        value = 0;
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

        if (long.TryParse(text, styles, CultureInfo.InvariantCulture, out long parsed))
        {
            if (parsed < -MaxSafeInteger || parsed > MaxSafeInteger)
                return SafeIntegerParse.OutOfRange;

            value = parsed;
            return SafeIntegerParse.SafeInteger;
        }

        if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out double doubleValue))
            return Math.Abs(doubleValue) > MaxSafeInteger ? SafeIntegerParse.OutOfRange : SafeIntegerParse.NonInteger;

        return SafeIntegerParse.NotNumeric;
    }

    private static bool TryGetJsonNodeStringProperty(JsonNode node, string propertyName, out string value)
    {
        if (node is JsonObject jsonObject &&
            jsonObject.TryGetPropertyValue(propertyName, out JsonNode propertyValue) &&
            propertyValue is JsonValue jsonValue &&
            jsonValue.TryGetValue(out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsValidHeaderValue(string value) =>
        value.AsSpan().IndexOfAnyExcept(ValidHeaderValueCharacters) < 0;

    private static bool ContainsAcceptableMediaType(string values, string offeredContentType)
    {
        if (!MediaTypeHeaderValue.TryParse(offeredContentType, out MediaTypeHeaderValue offeredValue))
            throw new ArgumentException("The offered content type is invalid.", nameof(offeredContentType));

        int bestSpecificity = -1;
        double bestQuality = 0.0;
        foreach (string value in SplitHeaderListValues(values))
        {
            if (!MediaTypeWithQualityHeaderValue.TryParse(value.Trim(), out MediaTypeWithQualityHeaderValue parsedValue) ||
                !TryMatchAcceptRange(parsedValue, offeredValue, out int specificity, out double quality))
            {
                continue;
            }

            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                bestQuality = quality;
            }
            else if (specificity == bestSpecificity)
            {
                bestQuality = Math.Max(bestQuality, quality);
            }
        }

        return bestSpecificity >= 0 && bestQuality > 0.0;
    }

    private static bool TryMatchAcceptRange(
        MediaTypeWithQualityHeaderValue range,
        MediaTypeHeaderValue offeredValue,
        out int specificity,
        out double quality)
    {
        specificity = 0;
        quality = 1.0;
        if (!string.Equals(range.MediaType, offeredValue.MediaType, StringComparison.OrdinalIgnoreCase))
            return false;

        bool foundQuality = false;
        bool parsingExtensions = false;
        foreach (NameValueHeaderValue parameter in range.Parameters)
        {
            if (string.Equals(parameter.Name, "q", StringComparison.OrdinalIgnoreCase))
            {
                if (foundQuality || !TryParseQuality(parameter.Value, out quality))
                    return false;

                foundQuality = true;
                parsingExtensions = true;
                continue;
            }

            if (parsingExtensions)
                continue;

            NameValueHeaderValue offeredParameter = offeredValue.Parameters.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (offeredParameter == null || !HeaderParameterValuesEqual(parameter.Value, offeredParameter.Value))
                return false;

            specificity++;
        }

        return true;
    }

    private static bool TryParseQuality(string value, out double quality)
    {
        quality = 0.0;
        if (value == null)
            return false;

        ReadOnlySpan<char> text = value.AsSpan().Trim();
        if (text.Length == 0 || text[0] is not ('0' or '1'))
            return false;

        if (text.Length == 1)
        {
            quality = text[0] == '1' ? 1.0 : 0.0;
            return true;
        }

        if (text[1] != '.' || text.Length > 5)
            return false;

        int fractionalValue = 0;
        int divisor = 1;
        for (int index = 2; index < text.Length; index++)
        {
            char digit = text[index];
            if (digit is < '0' or > '9' || text[0] == '1' && digit != '0')
                return false;

            fractionalValue = fractionalValue * 10 + digit - '0';
            divisor *= 10;
        }

        quality = text[0] == '1' ? 1.0 : (double)fractionalValue / divisor;
        return true;
    }

    private static bool HeaderParameterValuesEqual(string left, string right) =>
        string.Equals(UnquoteHeaderParameter(left), UnquoteHeaderParameter(right), StringComparison.OrdinalIgnoreCase);

    private static string UnquoteHeaderParameter(string value) =>
        value != null && value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static IEnumerable<string> SplitHeaderListValues(string values)
    {
        int valueStart = 0;
        bool inQuotedString = false;
        bool escaped = false;

        for (int index = 0; index < values.Length; index++)
        {
            char value = values[index];
            if (inQuotedString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == '\\')
                {
                    escaped = true;
                }
                else if (value == '"')
                {
                    inQuotedString = false;
                }

                continue;
            }

            if (value == '"')
            {
                inQuotedString = true;
            }
            else if (value == ',')
            {
                yield return values[valueStart..index];
                valueStart = index + 1;
            }
        }

        yield return values[valueStart..];
    }

    private static bool MatchesMediaType(string value, string expectedMediaType) =>
        MediaTypeHeaderValue.TryParse(value.Trim(), out MediaTypeHeaderValue parsedValue) &&
        string.Equals(parsedValue.MediaType, expectedMediaType, StringComparison.OrdinalIgnoreCase);

    private static string GetHeader(LoopbackHttpRequest request, string name) =>
        request.TryGetHeader(name, out string value) ? value.Trim() : string.Empty;

    private bool IsExpectedHost(string host) =>
        string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, expectedLocalhost, StringComparison.OrdinalIgnoreCase) ||
        allowOmittedHostPort &&
        (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static Task WriteJsonRpcErrorAsync(
        LoopbackHttpResponse response,
        string message,
        int statusCode,
        CancellationToken cancellationToken,
        int errorCode = -32000,
        RequestId requestId = default)
    {
        return WriteJsonRpcErrorDetailAsync(response, new JsonRpcErrorDetail
        {
            Code = errorCode,
            Message = message,
        }, statusCode, cancellationToken, requestId);
    }

    private static Task WriteJsonRpcErrorDetailAsync(
        LoopbackHttpResponse response,
        JsonRpcErrorDetail errorDetail,
        int statusCode,
        CancellationToken cancellationToken,
        RequestId requestId = default)
    {
        string safeMessage = new string((errorDetail.Message ?? "Unknown validation failure")
            .Take(512)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        WaeLogger.Log($"MCP HTTP request rejected with status {statusCode}: {safeMessage}");

        var error = new JsonRpcError
        {
            Id = requestId,
            Error = errorDetail,
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(error, ErrorTypeInfo);
        return response.WriteAsync(statusCode, "application/json; charset=utf-8", body, cancellationToken);
    }

    private static JsonTypeInfo<T> GetRequiredJsonTypeInfo<T>() =>
        (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));

    private enum HeaderValueComparison
    {
        Match,
        Mismatch,
        IntegerOutOfRange,
    }

    private enum SafeIntegerParse
    {
        SafeInteger,
        OutOfRange,
        NonInteger,
        NotNumeric,
    }
}
