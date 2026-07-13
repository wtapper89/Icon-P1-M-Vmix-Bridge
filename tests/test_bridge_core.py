import unittest
from copy import deepcopy
from unittest.mock import patch

import mido

from bridge_core import Bridge, ControllerRuntime, default_channel, default_controller


class FakePort:
    def __init__(self, name):
        self.name = name
        self.closed = False
        self.sent = []

    def close(self):
        self.closed = True

    def send(self, message):
        self.sent.append(message)


class FakeStore:
    def __init__(self, controllers=None):
        self.config = {
            "channel_count": 16,
            "controllers": controllers or [],
            "channels": [default_channel(index) for index in range(1, 17)],
            "master_fader": {"kind": "master"},
            "motor_feedback_hold_ms": 1800,
            "fader_write_interval_ms": 15,
            "send_scribble_text": True,
            "send_scribble_color": True,
            "send_motor_fader_feedback": True,
            "send_meters": True,
            "input_faders_touch_sensitive": True,
            "log_midi": False,
        }
        self.banks = []

    def get(self):
        return deepcopy(self.config)

    def set_bank(self, controller_id, bank_start):
        self.banks.append((controller_id, bank_start))
        for controller in self.config["controllers"]:
            if controller["id"] == controller_id:
                controller["bank_start"] = bank_start


class BridgeCoreTests(unittest.TestCase):
    def test_xtouch_hotplug_does_not_reopen_icon(self):
        icon_profile = default_controller(1, "icon_p1m")
        xtouch_profile = default_controller(2, "behringer_xtouch")
        store = FakeStore([icon_profile, xtouch_profile])
        bridge = Bridge(store)
        bridge.refresh_surface = lambda force=False: None

        icon = ControllerRuntime(icon_profile)
        icon.input = FakePort("iCON P1-M input")
        icon.output = FakePort("iCON P1-M output")
        xtouch = ControllerRuntime(xtouch_profile)
        xtouch.input = FakePort("X-Touch input")
        xtouch.output = FakePort("X-Touch output")
        bridge.runtimes = [icon, xtouch]

        devices = {"inputs": ["iCON P1-M input"], "outputs": ["iCON P1-M output"]}
        bridge.midi_devices = lambda: deepcopy(devices)
        bridge._reconnect_missing_controllers()
        self.assertFalse(icon.input.closed)
        self.assertIsNone(xtouch.input)
        self.assertIn("waiting to reconnect", xtouch.error)

        devices["inputs"].append("X-Touch input")
        devices["outputs"].append("X-Touch output")
        with patch("bridge_core.mido.open_input", side_effect=lambda name, callback: FakePort(name)), patch(
            "bridge_core.mido.open_output", side_effect=lambda name: FakePort(name)
        ):
            bridge._reconnect_missing_controllers()
        self.assertFalse(icon.input.closed)
        self.assertEqual(xtouch.input.name, "X-Touch input")
        self.assertEqual(xtouch.output.name, "X-Touch output")
        self.assertEqual(xtouch.error, "")

    def test_xtouch_does_not_claim_an_unrelated_port_while_powered_off(self):
        icon_profile = default_controller(1, "icon_p1m")
        xtouch_profile = default_controller(2, "behringer_xtouch")
        bridge = Bridge(FakeStore([icon_profile, xtouch_profile]))
        bridge.refresh_surface = lambda force=False: None

        icon = ControllerRuntime(icon_profile)
        icon.input = FakePort("iCON P1-M 0")
        icon.output = FakePort("iCON P1-M 1")
        xtouch = ControllerRuntime(xtouch_profile)
        xtouch.input = FakePort("MIDIIN2 (iCON P1-M) 1")
        xtouch.output = FakePort("MIDIOUT2 (iCON P1-M) 2")
        bridge.runtimes = [icon, xtouch]

        devices = {
            "inputs": ["iCON P1-M 0", "MIDIIN2 (iCON P1-M) 1"],
            "outputs": ["iCON P1-M 1", "MIDIOUT2 (iCON P1-M) 2"],
        }
        bridge.midi_devices = lambda: deepcopy(devices)
        bridge._reconnect_missing_controllers()

        self.assertIsNone(xtouch.input)
        self.assertIsNone(xtouch.output)
        self.assertIn("waiting to reconnect", xtouch.error)

    def test_master_fader_feedback_has_runtime_state_slot(self):
        runtime = ControllerRuntime(default_controller(1, "behringer_xtouch"))
        runtime.output = FakePort("X-Touch output")
        bridge = Bridge(FakeStore([runtime.profile]))

        bridge._send_fader(runtime, 8, 50, force=True)

        self.assertGreater(runtime.suppress_until[8], 0)

    def test_bank_buttons_move_both_directions_and_leds_follow(self):
        profile = default_controller(1, "behringer_xtouch")
        store = FakeStore([profile])
        bridge = Bridge(store)
        runtime = ControllerRuntime(profile)
        runtime.output = FakePort("X-Touch output")
        bridge.runtimes = [runtime]
        bridge.refresh_surface = lambda force=False: bridge._send_bank_leds(runtime, store.get(), force)

        bridge._change_bank(runtime, 8)
        self.assertEqual(runtime.bank_start, 8)
        self.assertEqual(store.banks[-1], (profile["id"], 8))
        self.assertEqual([(message.note, message.velocity) for message in runtime.output.sent[-2:]], [(92, 127), (93, 0)])

        bridge._change_bank(runtime, -8)
        self.assertEqual(runtime.bank_start, 0)
        self.assertEqual(store.banks[-1], (profile["id"], 0))
        self.assertEqual([(message.note, message.velocity) for message in runtime.output.sent[-2:]], [(92, 0), (93, 127)])

    def test_captured_xtouch_bank_notes_move_both_directions(self):
        profile = default_controller(1, "behringer_xtouch")
        store = FakeStore([profile])
        bridge = Bridge(store)
        runtime = ControllerRuntime(profile)
        runtime.output = FakePort("X-Touch output")
        bridge.runtimes = [runtime]
        bridge.refresh_surface = lambda force=False: bridge._send_bank_leds(runtime, store.get(), force)

        bridge._handle_midi(runtime, mido.Message("note_on", note=93, velocity=127))
        self.assertEqual(runtime.bank_start, 8)
        bridge._handle_midi(runtime, mido.Message("note_on", note=92, velocity=127))
        self.assertEqual(runtime.bank_start, 0)

    def test_captured_xtouch_jog_values_move_both_directions(self):
        profile = default_controller(1, "behringer_xtouch")
        store = FakeStore([profile])
        bridge = Bridge(store)
        runtime = ControllerRuntime(profile)
        runtime.output = FakePort("X-Touch output")
        bridge.runtimes = [runtime]
        bridge.refresh_surface = lambda force=False: bridge._send_bank_leds(runtime, store.get(), force)

        bridge._handle_midi(runtime, mido.Message("control_change", control=88, value=65))
        self.assertEqual(runtime.bank_start, 8)
        bridge._handle_midi(runtime, mido.Message("control_change", control=88, value=1))
        self.assertEqual(runtime.bank_start, 0)

    def test_xtouch_record_and_mute_set_volume_for_notes_and_cc(self):
        profile = default_controller(1, "behringer_xtouch")
        store = FakeStore([profile])
        store.config["channels"][0]["kind"] = "input"
        bridge = Bridge(store)
        runtime = ControllerRuntime(profile)
        values = []
        bridge.set_assignment_volume = lambda assignment, value: values.append(value)

        bridge._handle_midi(runtime, mido.Message("note_on", note=8, velocity=127))
        bridge._handle_midi(runtime, mido.Message("note_on", note=24, velocity=127))
        bridge._handle_midi(runtime, mido.Message("control_change", control=8, value=127))
        bridge._handle_midi(runtime, mido.Message("control_change", control=24, value=127))
        self.assertEqual(values, [100, 0, 100, 0])


if __name__ == "__main__":
    unittest.main()
