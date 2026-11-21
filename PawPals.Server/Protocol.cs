using System;

namespace PawPals.Protocol;

public sealed record PlayerView(Guid Id, string Name, int CatId, bool Connected,
    DateTimeOffset? ReconnectUntil);
public sealed record RoomView(string Code, int Capacity, long Version, PlayerView[] Players, Guid Epoch = default);
public sealed record RoomSession(Guid PlayerId, string ResumeToken, RoomView Room, WorldView World, int ProtocolVersion = 9);
public sealed record RoomChat(Guid Id, Guid PlayerId, string Name, string Text, DateTimeOffset SentAt, string Code = "");
public sealed record BubbleView(Guid Id, string Text, double SecondsRemaining);
public sealed record PetFrame(Guid PlayerId, double X, int Direction, string Action,
    int FrameIndex, double BreathingScale, string? Message, double BubbleSecondsRemaining, bool Connected,
    BubbleView[]? Bubbles = null);
public sealed record WorldView(string Code, long Sequence, PetFrame[] Pets, Guid Epoch = default);

public sealed record PersistentJoin(Guid PlayerId, string ResumeToken, string Mode, string? Code, string Name, int CatId);
