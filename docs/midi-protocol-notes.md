# MIDI Protocol Notes

The bridge supports standard Mackie Control messages and the measured CTRL USB messages from the Behringer X-Touch used during hardware validation.

## Mackie Control / iCON P1-M

- Faders: pitch bend on MIDI channels 1-8.
- Motor feedback: pitch bend on MIDI channels 1-8.
- Record: Note On 0-7.
- Mute: Note On 16-23.
- Bank left/right: Note On 46/47.
- Channel left/right: Note On 48/49.
- Meter feedback: packed channel pressure values.
- Labels: MCU scribble-strip SysEx, seven characters per line.

## Behringer X-Touch CTRL USB

- Channel faders: Control Change 70-77; master fader uses 78.
- Motor feedback: Control Change 70-78.
- Record: Note On 8-15.
- Mute: Note On 24-31.
- Fader Bank left/right: Note On 92/93.
- Jog wheel: Control Change 88, value 1 left and value 65 right.
- Meter feedback: Control Change 90-97.
- Labels and colors: Behringer display SysEx using device ID `0x14` and command `0x4C`.

Button LED feedback uses the same note numbers as the corresponding button input. X-Touch CTRL USB mappings were captured from physical hardware and are covered by the regression tests in `tests/test_bridge_core.py`.
