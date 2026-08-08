namespace MapEditorMCP.Http;

/// <summary>
/// HTTP names and protocol-era checks used by the host-neutral transport adapter. These mirror
/// the internal constants in the official ModelContextProtocol 2.1.0 HTTP implementation.
/// </summary>
internal static class McpHttpProtocol
{
    public const string SessionIdHeader = "Mcp-Session-Id";
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";
    public const string MethodHeader = "Mcp-Method";
    public const string NameHeader = "Mcp-Name";
    public const string ParameterHeaderPrefix = "Mcp-Param-";

    public const string July2026ProtocolVersion = "2026-07-28";
    public const string November2025ProtocolVersion = "2025-11-25";
    public const string June2025ProtocolVersion = "2025-06-18";
    public const string March2025ProtocolVersion = "2025-03-26";
    public const string November2024ProtocolVersion = "2024-11-05";

    public static readonly string[] SupportedProtocolVersions =
    [
        November2024ProtocolVersion,
        March2025ProtocolVersion,
        June2025ProtocolVersion,
        November2025ProtocolVersion,
        July2026ProtocolVersion,
    ];

    public static bool IsSupportedProtocolVersion(string protocolVersion) =>
        protocolVersion != null && SupportedProtocolVersions.Contains(protocolVersion);

    public static bool RequiresPerRequestMetadata(string protocolVersion) =>
        !string.IsNullOrEmpty(protocolVersion) &&
        StringComparer.Ordinal.Compare(protocolVersion, July2026ProtocolVersion) >= 0;

    public static bool RequiresStandardHeaders(string protocolVersion) =>
        RequiresPerRequestMetadata(protocolVersion);
}
