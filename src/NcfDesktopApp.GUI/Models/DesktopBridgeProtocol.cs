/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopBridgeProtocol.cs
    文件功能描述：DesktopBridge 客户端协议模型

    创建标识：Senparc - 20260725
----------------------------------------------------------------*/

using System;

namespace NcfDesktopApp.GUI.Models;

public enum DesktopBridgeAvailability
{
    Available,
    NotInstalled,
    Unauthorized,
    Inactive,
    Incompatible,
    Unavailable
}

public sealed record DesktopBridgeCapabilities(
    int ProtocolVersion,
    string BridgeVersion,
    bool SupportsSse,
    bool SupportsSnapshot,
    string EventEndpoint,
    string SnapshotEndpoint,
    bool SupportsAuthorizedSync = false,
    string? AuthorizedSyncEndpoint = null,
    bool SupportsAdminAuthHandoff = false,
    string? AdminAuthHandoffRequestEndpoint = null,
    string? AdminAuthHandoffRedeemEndpoint = null);

public sealed record DesktopBridgeProbeResult(
    DesktopBridgeAvailability Availability,
    string Message,
    DesktopBridgeCapabilities? Capabilities = null)
{
    public bool IsAvailable => Availability == DesktopBridgeAvailability.Available;
}

public sealed record DesktopBridgePairingCreateResponse(
    Guid RequestId,
    string DeviceCode,
    string PollSecret,
    DateTimeOffset ExpiresAt,
    string VerificationPath,
    int PollIntervalSeconds);

public sealed record DesktopBridgePairingPollResponse(
    string Status,
    string? SessionToken,
    DateTimeOffset? SessionExpiresAt,
    string? Message);

public sealed record DesktopAdminAuthHandoff(
    Guid RequestId,
    DateTimeOffset ExpiresAt,
    string ApprovalPath,
    int PollIntervalMilliseconds,
    string CodeVerifier);

public sealed record DesktopAdminAuthHandoffCreateResponse(
    Guid RequestId,
    DateTimeOffset ExpiresAt,
    string ApprovalPath,
    int PollIntervalMilliseconds);

public sealed record DesktopAdminAuthHandoffRedeemResponse(
    string Status,
    string? UserName,
    string? AccessToken,
    DateTimeOffset? ExpiresUtc,
    string? Message);

public sealed record DesktopActivityMessage(
    long Sequence,
    string ActivityId,
    string Source,
    string State,
    string Title,
    string? Detail,
    double? Progress,
    DateTimeOffset Time,
    bool IsTerminal,
    string? ActionUrl);

public sealed record DesktopAuthorizedSyncMessage(
    long Sequence,
    string Channel,
    string ResourceId,
    string Action,
    DateTimeOffset Time);
