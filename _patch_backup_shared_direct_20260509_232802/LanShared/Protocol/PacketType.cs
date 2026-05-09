namespace LanShared.Protocol;

public static class PacketType
{
    public const byte TextMessage = 1;
    public const byte FileStart = 2;
    public const byte FileChunk = 3;
    public const byte FileEnd = 4;

    public const byte ScreenFrame = 5;
    public const byte ScreenVideoFrame = 5;

    public const byte OpenTarget = 6;
    public const byte TransferCancel = 7;
    public const byte BatchStart = 8;
    public const byte BatchEnd = 9;

    public const byte ScreenVideoStart = 10;
    public const byte ScreenVideoStop = 11;
    public const byte KeepAlive = 12;
    public const byte OpenTargetResult = 13;

    public static string ToName(byte type) => type switch
    {
        TextMessage => nameof(TextMessage),
        FileStart => nameof(FileStart),
        FileChunk => nameof(FileChunk),
        FileEnd => nameof(FileEnd),
        ScreenVideoFrame => nameof(ScreenVideoFrame),
        OpenTarget => nameof(OpenTarget),
        TransferCancel => nameof(TransferCancel),
        BatchStart => nameof(BatchStart),
        BatchEnd => nameof(BatchEnd),
        ScreenVideoStart => nameof(ScreenVideoStart),
        ScreenVideoStop => nameof(ScreenVideoStop),
        KeepAlive => nameof(KeepAlive),
        OpenTargetResult => nameof(OpenTargetResult),
        _ => $"Unknown({type})",
    };
}
