namespace Zapqio.Runner.Protocol;

/// <summary>
/// Protocol version negotiated on the WebSocket handshake. The runner sends the <see cref="Header"/>
/// request header carrying its protocol <b>major</b> version; the server accepts the connection only
/// if it supports that version. A missing header is treated as version <c>1</c> and is refused by
/// the current server. Web and runner must use the same major version. The major version is
/// bumped only on a breaking change - see <c>protocol/PROTOCOL.md</c> section 9.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Handshake header carrying the runner's protocol major version.</summary>
    public const string Header = "X-Zapqio-Protocol-Version";

    /// <summary>
    /// The protocol major version implemented by this build.
    ///
    /// v2 added the required <c>attemptId</c> on Job/Log/JobReturn, the <c>JobAccepted</c> message
    /// with its acceptance deadline, and the one-outstanding-job-per-runner rule. A v1 runner cannot
    /// talk to a v2 server: the fields are required in both directions.
    ///
    /// v3 widened <c>level</c> on Log from Info/Error to Debug/Info/Warning/Error/Critical (§5.4).
    /// The break is one-way - a v3 server would understand everything a v2 runner sends - but the
    /// server speaks exactly one version (§9), so a v2 runner is refused with 426 by a v3 server all
    /// the same, and this build is refused by a v2 server. Upgrade the server and the runners together.
    ///
    /// v4 requires Heartbeat/HeartbeatAck, process identity and Info reconciliation on every session.
    /// Web and both runner implementations must be upgraded together.
    /// </summary>
    public const int Current = 4;
}
