namespace IconP1MVmixBridge;

public sealed record MidiDeviceInfo(int Id, string Name);

public sealed class MidiMessageEventArgs : EventArgs
{
    public MidiMessageEventArgs(int rawMessage, int timestamp)
    {
        RawMessage = rawMessage;
        Timestamp = timestamp;
        Status = rawMessage & 0xFF;
        Data1 = (rawMessage >> 8) & 0xFF;
        Data2 = (rawMessage >> 16) & 0xFF;
        Command = Status & 0xF0;
        Channel = Status & 0x0F;
    }

    public int RawMessage { get; }
    public int Timestamp { get; }
    public int Status { get; }
    public int Command { get; }
    public int Channel { get; }
    public int Data1 { get; }
    public int Data2 { get; }
}
