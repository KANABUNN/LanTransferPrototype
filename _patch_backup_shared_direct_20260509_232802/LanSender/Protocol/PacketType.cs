namespace LanSender.Protocol;

public static class PacketType
{
    public const byte TextMessage = LanShared.Protocol.PacketType.TextMessage;
    public const byte FileStart = LanShared.Protocol.PacketType.FileStart;
    public const byte FileChunk = LanShared.Protocol.PacketType.FileChunk;
    public const byte FileEnd = LanShared.Protocol.PacketType.FileEnd;

    public const byte ScreenFrame = LanShared.Protocol.PacketType.ScreenFrame;
    public const byte ScreenVideoFrame = LanShared.Protocol.PacketType.ScreenVideoFrame;

    public const byte OpenTarget = LanShared.Protocol.PacketType.OpenTarget;
    public const byte TransferCancel = LanShared.Protocol.PacketType.TransferCancel;
    public const byte BatchStart = LanShared.Protocol.PacketType.BatchStart;
    public const byte BatchEnd = LanShared.Protocol.PacketType.BatchEnd;

    public const byte ScreenVideoStart = LanShared.Protocol.PacketType.ScreenVideoStart;
    public const byte ScreenVideoStop = LanShared.Protocol.PacketType.ScreenVideoStop;
    public const byte KeepAlive = LanShared.Protocol.PacketType.KeepAlive;
    public const byte OpenTargetResult = LanShared.Protocol.PacketType.OpenTargetResult;

    public static string ToName(byte type) => LanShared.Protocol.PacketType.ToName(type);
}
