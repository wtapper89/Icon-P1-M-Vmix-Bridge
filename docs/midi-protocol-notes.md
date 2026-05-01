# MIDI Protocol Notes

This first build assumes Mackie Control Universal style messages.

## Implemented

- Fader input: pitch bend on MIDI channels 1-8.
- Motor fader output: pitch bend on MIDI channels 1-8.
- Mute button input: Note On notes 16-23.
- Meter output: channel pressure message, channel encoded in high nibble, level 0-12.
- Display labels: standard MCU scribble-strip SysEx, 7 characters per strip.

## To Verify With Hardware

- Whether the P1-M sends mute as notes 16-23 in its selected mode.
- Whether the D4-T or P1-M strip labels accept standard MCU scribble-strip SysEx in the selected mode.
- Whether meter feedback expects channel pressure, SysEx, or a device-specific message.

The app logs every fader, mute, and display-label action. Send the log file after the first hardware test and the remaining protocol details can be tightened quickly.
