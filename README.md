# iCON P1-M vMix Bridge

Windows bridge app for controlling vMix audio from an iCON P1-M or compatible Mackie Control surface.

## Current Test Build Scope

- Connects to vMix HTTP API on port `8088` for source names, source volume, and meters.
- Sends vMix control commands through the vMix TCP API on port `8099`.
- Reads Mackie Control fader moves from MIDI pitch-bend messages on channels 1-8.
- Sends motor fader feedback as Mackie Control pitch-bend messages on channels 1-8.
- Sends basic Mackie meter feedback through channel pressure meter messages.
- Sends source labels using standard Mackie Control scribble-strip SysEx.
- Allows each of the 8 strips to be assigned to a vMix input, Master, or buses A-G.
- Saves assignments automatically to:

```text
%LOCALAPPDATA%\IconP1MVmixBridge\profile.json
```

- Writes rolling logs to:

```text
%LOCALAPPDATA%\IconP1MVmixBridge\logs
```

## Important Hardware Note

Motor faders and strip labels use standard Mackie Control messages and should be testable immediately.
If the P1-M/D4-T uses a device-specific display variation in your selected mode, the log will show the label data the app attempted to send so we can adjust the SysEx packet.

## vMix Setup

1. Open vMix on the Windows PC.
2. Go to `Settings > Web Controller`.
3. Enable the Web Controller/API.
4. Leave HTTP on `8088` unless you change the bridge setting.
5. Confirm TCP API port `8099` is reachable. This is the default vMix TCP API port.

## P1-M Setup

1. Connect the P1-M by USB.
2. Put the P1-M in Mackie Control Universal mode.
3. Start the bridge.
4. Select the P1-M MIDI input and MIDI output.
5. Assign strips in the grid.
6. Click `Connect`.

## Testing Checklist

1. Move fader 1 on the P1-M.
2. Confirm the assigned vMix input fader moves.
3. Move the assigned vMix input fader with the mouse.
4. Confirm the P1-M motor fader follows.
5. Press the P1-M mute button for strip 1.
6. Confirm the assigned vMix input audio toggles.
7. Send the latest log file if any step fails.

## Build Locally On Windows

Install the .NET 8 SDK, then run:

```powershell
cd src\IconP1MVmixBridge
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true
```

The executable will be in:

```text
src\IconP1MVmixBridge\bin\Release\net8.0-windows\win-x64\publish\IconP1MVmixBridge.exe
```

## Build Artifact From GitHub

The included GitHub Actions workflow builds a self-contained Windows executable and uploads it as an artifact named `IconP1MVmixBridge-win-x64`.
