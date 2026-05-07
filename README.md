# iCON P1-M vMix Bridge

This Windows app lets an iCON P1-M control vMix audio like a small broadcast audio surface.

It is built for operators who need quick control during services, live streams, classes, meetings, or events:

- Move a P1-M fader to control an assigned vMix input.
- See source names on the P1-M display/scribble strip.
- See audio level activity on the P1-M meters.
- Let motorized faders follow vMix when someone changes a level in vMix.
- Quickly reassign each P1-M channel from inside the app.
- Optionally reassign channels from another program using a simple local API.

## Quick Start

Use this section if you just need to get the system running.

### 1. Download The App

Download the latest test build:

[Download IconP1MVmixBridge-win-x64.exe](https://github.com/wtapper89/IconP1MVmixBridge/raw/main/release/IconP1MVmixBridge-win-x64.exe)

Put the file somewhere easy to find, for example:

```text
C:\IconP1MVmixBridge\IconP1MVmixBridge-win-x64.exe
```

Windows may warn you because this is not yet a signed installer. Choose `More info`, then `Run anyway`.

### 2. Prepare vMix

1. Open vMix.
2. Go to `Settings`.
3. Open `Web Controller`.
4. Turn on the vMix Web Controller/API.
5. Leave the HTTP port set to `8088` unless you know it has been changed.
6. The bridge also uses the vMix TCP API on port `8099`.

The bridge app should show something like:

```text
Connected. 29 inputs.
```

If it says vMix is not connected, make sure vMix is open and the Web Controller/API is enabled.

### 3. Connect The P1-M

1. Connect the iCON P1-M to the Windows PC by USB.
2. Put the P1-M in Mackie Control / MCU mode.
3. Open the bridge app.
4. Click `Refresh MIDI`.
5. Select the P1-M MIDI input.
6. Select the P1-M MIDI output.

Important: do not use `Microsoft GS Wavetable Synth` as the MIDI output. The MIDI output must be the P1-M.

### 4. Start The Bridge

1. Click `Refresh vMix`.
2. Click `Open MIDI` if MIDI is not already open.
3. Click `Start Bridge`.

The status line should show:

```text
Bridge: running
MIDI In: open
MIDI Out: open
```

## Assigning Channels

Each row in the app is one P1-M channel strip.

Use the table in the app:

- `P1-M Channel`: the physical fader number.
- `Assignment`: choose what the fader controls.
- `vMix Input`: choose the vMix source when `Assignment` is `Input`.
- `Label Override`: optional short name to show on the P1-M display.
- `Strip Color`: color for the display strip.
- `Volume`: current volume from vMix.
- `Meter`: current audio level from vMix.

Common setup:

1. Set channel 1 to `Input`.
2. Choose your sermon mic, worship leader mic, or playback input.
3. Set channel 8 to `Master`.
4. Choose a strip color that makes sense to your team.
5. Click `Save Profile`.

The app saves your assignments here:

```text
%LOCALAPPDATA%\IconP1MVmixBridge\profile.json
```

The assignments should load again the next time you open the app.

## What The P1-M Controls Do

### Faders

Moving a P1-M fader changes the assigned vMix volume.

If someone changes the same volume inside vMix, the motorized P1-M fader should follow.

### Record Button

The record button beside each channel sets that assigned vMix volume to:

```text
100 / 0 dB
```

Use this as a quick "bring this channel to normal full level" button.

### Mute Button

The mute button beside each channel sets that assigned vMix volume to:

```text
0 / -inf
```

Use this as a quick "pull this channel all the way down" button.

This does not toggle the vMix mute state. It moves the volume to the bottom.

## Recommended Sunday Morning Test

Run this before a service or event.

1. Open vMix.
2. Open the bridge app.
3. Click `Refresh vMix`.
4. Click `Refresh MIDI`.
5. Confirm the P1-M input and output are selected.
6. Click `Start Bridge`.
7. Move channel 1 on the P1-M and confirm the assigned vMix volume moves.
8. Move the same fader in vMix and confirm the P1-M motor fader follows.
9. Press the channel record button and confirm the volume goes to `100`.
10. Press the channel mute button and confirm the volume goes to `0`.
11. Speak or play audio into a source and confirm the meter responds.

## Running In The Background

The app can run in the Windows notification area.

- Turn on `Minimize to tray` if you want the app to hide when minimized or closed.
- Turn on `Start with Windows` if you want the bridge to launch when the PC starts.
- Right-click the tray icon to show, start, stop, or exit the bridge.

For important events, open the app before the service and confirm the status line says the bridge is running.

## Troubleshooting

### vMix Inputs Do Not Load

Check these first:

1. vMix is open.
2. vMix Web Controller/API is enabled.
3. The bridge `vMix Host` is `127.0.0.1` if vMix is on the same computer.
4. The bridge HTTP port is `8088`.
5. Click `Refresh vMix`.

### MIDI Says Closed

Check these first:

1. The P1-M is connected by USB.
2. Click `Refresh MIDI`.
3. Select a P1-M MIDI input.
4. Select a P1-M MIDI output.
5. Make sure MIDI output is not `Microsoft GS Wavetable Synth`.
6. Click `Open MIDI`.

### Fader Moves In vMix But P1-M Does Not Follow

Check:

1. `Motor fader feedback` is checked.
2. MIDI output is the P1-M.
3. The bridge status says `MIDI Out: open`.
4. The P1-M is in Mackie Control / MCU mode.

### P1-M Moves But vMix Does Not Change

Check:

1. The bridge status says `Bridge: running`.
2. MIDI input is open.
3. The channel has an assignment.
4. If the assignment is `Input`, a vMix input is selected.

### The Wrong Source Is Controlled

Check the channel assignment table. If vMix inputs were renamed or moved, click `Refresh vMix` and choose the correct input again.

## Sending Logs For Help

The app writes logs automatically. Logs are the fastest way to diagnose a problem.

Logs are stored here:

```text
%LOCALAPPDATA%\IconP1MVmixBridge\logs
```

In the app, the log file path is shown at the bottom. Click it to open the folder or select the current log.

When reporting a problem, send:

1. The newest `bridge-*.log` file.
2. A short description of what you did.
3. Which P1-M channel was involved.
4. Whether the problem happened when moving the P1-M, moving vMix, or pressing a button.

Example:

```text
At 9:42 AM I moved P1-M channel 3 up. vMix did not move. Channel 3 was assigned to Pastor Mic.
```

## Assignment API

Most users do not need this section. It is for automation systems, companion apps, or technical volunteers who want to change assignments without clicking in the bridge app.

The API only listens on this computer:

```text
http://127.0.0.1:8097/api
```

The API port can be changed in the bridge app.

### List Current Assignments

```powershell
curl http://127.0.0.1:8097/api/assignments
```

### List vMix Inputs

```powershell
curl http://127.0.0.1:8097/api/inputs
```

### Assign A Channel To A vMix Input Number

This example assigns P1-M channel 1 to vMix input number 14:

```powershell
curl -X PUT http://127.0.0.1:8097/api/channels/1 `
  -H "Content-Type: application/json" `
  -d "{\"kind\":\"Input\",\"inputNumber\":14,\"stripColor\":\"Green\"}"
```

### Assign A Channel To Master

This example assigns P1-M channel 8 to Master:

```powershell
curl -X PUT http://127.0.0.1:8097/api/channels/8 `
  -H "Content-Type: application/json" `
  -d "{\"kind\":\"Master\",\"labelOverride\":\"MASTER\",\"stripColor\":\"White\"}"
```

Accepted `kind` values:

```text
None
Input
Master
BusA
BusB
BusC
BusD
BusE
BusF
BusG
```

Accepted `stripColor` values:

```text
Off
White
Red
Orange
Yellow
Green
Cyan
Blue
Purple
Pink
```

For input assignments, use one of these:

- `inputKey`
- `inputNumber`
- `inputTitle`

## Notes For Technical Users

The bridge uses:

- vMix HTTP API port `8088` for state, input names, meters, and fast volume commands.
- vMix TCP API port `8099` for supported TCP commands.
- Mackie Control pitch-bend messages for faders.
- Mackie Control scribble-strip SysEx for labels.
- Mackie Control meter messages for levels.

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

## GitHub Build Artifact

The included GitHub Actions workflow builds a self-contained Windows executable and uploads it as an artifact named:

```text
IconP1MVmixBridge-win-x64
```
