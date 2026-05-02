using System.Text.Json;
using System.Text.Json.Serialization;

namespace IconP1MVmixBridge;

public sealed class BridgeProfile
{
    public string VMixHost { get; set; } = "127.0.0.1";
    public int VMixHttpPort { get; set; } = 8088;
    public int VMixTcpPort { get; set; } = 8099;
    public int PollIntervalMs { get; set; } = 250;
    public int FaderWriteIntervalMs { get; set; } = 75;
    public int MotorFeedbackHoldMs { get; set; } = 1800;
    public string MidiInputName { get; set; } = "";
    public string MidiOutputName { get; set; } = "";
    public bool SendMackieScribbleStripText { get; set; } = true;
    public bool SendMotorFaderFeedback { get; set; } = true;
    public bool InputFadersAreTouchSensitive { get; set; } = true;
    public List<ChannelAssignment> Channels { get; set; } = Enumerable.Range(1, 8)
        .Select(index => new ChannelAssignment { Channel = index })
        .ToList();

    public static BridgeProfile LoadOrCreate(string path, FileLogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                var newProfile = new BridgeProfile();
                newProfile.Save(path);
                return newProfile;
            }

            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<BridgeProfile>(json, JsonOptions()) ?? new BridgeProfile();
            profile.Normalize();
            logger.Info("Loaded profile from {0}", path);
            return profile;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not load profile. A default profile will be used.");
            return new BridgeProfile();
        }
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions()));
    }

    private void Normalize()
    {
        VMixHttpPort = Math.Clamp(VMixHttpPort, 1, 65535);
        VMixTcpPort = Math.Clamp(VMixTcpPort, 1, 65535);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 100, 5000);
        FaderWriteIntervalMs = Math.Clamp(FaderWriteIntervalMs, 25, 500);
        MotorFeedbackHoldMs = Math.Clamp(MotorFeedbackHoldMs, 250, 5000);

        var byChannel = Channels
            .Where(c => c.Channel is >= 1 and <= 8)
            .GroupBy(c => c.Channel)
            .ToDictionary(g => g.Key, g => g.First());

        Channels = Enumerable.Range(1, 8)
            .Select(index => byChannel.TryGetValue(index, out var existing)
                ? existing with { Channel = index }
                : new ChannelAssignment { Channel = index })
            .ToList();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record ChannelAssignment
{
    public int Channel { get; init; }
    public AssignmentKind Kind { get; init; } = AssignmentKind.None;
    public string? InputKey { get; init; }
    public string LabelOverride { get; init; } = "";
    public bool FollowInputName { get; init; } = true;
    public StripColor StripColor { get; init; } = StripColor.Blue;
}

public enum AssignmentKind
{
    None,
    Input,
    Master,
    BusA,
    BusB,
    BusC,
    BusD,
    BusE,
    BusF,
    BusG
}

public enum StripColor
{
    Off,
    White,
    Red,
    Orange,
    Yellow,
    Green,
    Cyan,
    Blue,
    Purple,
    Pink
}
