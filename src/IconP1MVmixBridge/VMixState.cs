namespace IconP1MVmixBridge;

public sealed class VMixState
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public List<VMixInput> Inputs { get; init; } = [];
    public Dictionary<string, double> OutputVolumes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> OutputMeters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Connected { get; init; }
    public string Status { get; init; } = "Not connected";
}

public sealed class VMixInput
{
    public int Number { get; init; }
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public double Volume { get; init; }
    public double MeterF1 { get; init; }
    public double MeterF2 { get; init; }
    public bool Muted { get; init; }
}
