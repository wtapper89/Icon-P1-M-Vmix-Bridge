import json
import logging
import math
import os
import shutil
import threading
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from copy import deepcopy
from pathlib import Path

import mido


ASSIGNMENT_KINDS = ["none", "input", "master", "busA", "busB", "busC", "busD", "busE", "busF", "busG"]
STRIP_COLORS = ["off", "white", "red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink"]
CONTROLLER_TYPES = ["icon_p1m", "behringer_xtouch", "mackie_control"]
ICON_RGB = {
    "off": (0, 0, 0), "white": (127, 127, 127), "red": (127, 0, 0),
    "orange": (127, 54, 0), "yellow": (127, 118, 0), "green": (0, 127, 0),
    "cyan": (0, 110, 127), "blue": (0, 36, 127), "purple": (74, 0, 127),
    "pink": (127, 0, 82),
}
XTOUCH_COLOR_INDEX = {
    "off": 0, "red": 1, "green": 2, "yellow": 3, "blue": 4,
    "purple": 5, "pink": 5, "orange": 3, "cyan": 6, "white": 7,
}
LEGACY_KIND = {0: "none", 1: "input", 2: "master", 3: "busA", 4: "busB", 5: "busC", 6: "busD", 7: "busE", 8: "busF", 9: "busG"}
LEGACY_COLOR = {index: color for index, color in enumerate(STRIP_COLORS)}


def default_channel(index):
    return {
        "channel": index, "kind": "none", "input_key": "", "input_number": 0,
        "label_override": "", "follow_input_name": True, "strip_color": "blue",
    }


def default_controller(index=1, controller_type="icon_p1m"):
    is_xtouch = controller_type == "behringer_xtouch"
    return {
        "id": f"controller-{index}", "name": f"Controller {index}", "type": controller_type,
        "midi_input": "", "midi_output": "", "device_hint": "X-TOUCH" if is_xtouch else "P1-M",
        "enabled": True, "bank_start": 0, "surface_mode": "ctrl" if is_xtouch else "mcu",
        "color_mode": "mcu-72", "meter_mode": "mcu-packed-aftertouch",
        "meter_gain_db": 12.0, "button_mode": "volume_100_0",
        "master_fader_enabled": is_xtouch,
    }


DEFAULT_CONFIG = {
    "config_version": 4,
    "web_host": "0.0.0.0", "web_port": 8091,
    "vmix_host": "127.0.0.1", "vmix_http_port": 8088, "vmix_tcp_port": 8099,
    "poll_interval_ms": 100, "fader_write_interval_ms": 15, "motor_feedback_hold_ms": 1800,
    "api_port": 8097, "channel_count": 16,
    "send_scribble_text": True, "send_scribble_color": True,
    "send_motor_fader_feedback": True, "send_meters": True,
    "input_faders_touch_sensitive": True, "minimize_to_tray": True,
    "start_with_windows": True, "log_midi": False,
    "controllers": [default_controller()],
    "channels": [default_channel(index) for index in range(1, 17)],
    "master_fader": {"kind": "master", "input_key": "", "input_number": 0, "label_override": "Master", "strip_color": "white"},
}


def deep_merge(base, override):
    result = deepcopy(base)
    for key, value in (override or {}).items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = deep_merge(result[key], value)
        else:
            result[key] = deepcopy(value)
    return result


def clamp(value, low, high, fallback):
    try:
        return max(low, min(high, int(value)))
    except (TypeError, ValueError):
        return fallback


def normalized_kind(value):
    text = str(value or "none")
    return next((kind for kind in ASSIGNMENT_KINDS if kind.lower() == text.lower()), "none")


def normalized_color(value):
    text = str(value or "blue").lower()
    if text == "magenta":
        text = "purple"
    return text if text in STRIP_COLORS else "blue"


def infer_controller_type(name, numeric_type=None):
    text = str(name or "").lower()
    if "x-touch" in text or "xtouch" in text or "behringer" in text:
        return "behringer_xtouch"
    if "p1" in text or "icon" in text:
        return "icon_p1m"
    return {1: "icon_p1m", 2: "behringer_xtouch"}.get(numeric_type, "mackie_control")


def migrate_legacy_icon_profile(config, legacy):
    migrated = deepcopy(config)
    migrated.update({
        "vmix_host": legacy.get("VMixHost", migrated["vmix_host"]),
        "vmix_http_port": legacy.get("VMixHttpPort", migrated["vmix_http_port"]),
        "vmix_tcp_port": legacy.get("VMixTcpPort", migrated["vmix_tcp_port"]),
        "poll_interval_ms": legacy.get("PollIntervalMs", migrated["poll_interval_ms"]),
        "fader_write_interval_ms": legacy.get("FaderWriteIntervalMs", migrated["fader_write_interval_ms"]),
        "motor_feedback_hold_ms": legacy.get("MotorFeedbackHoldMs", migrated["motor_feedback_hold_ms"]),
        "api_port": legacy.get("ApiPort", migrated["api_port"]),
        "channel_count": legacy.get("ChannelCount", migrated["channel_count"]),
        "send_scribble_text": legacy.get("SendMackieScribbleStripText", True),
        "send_motor_fader_feedback": legacy.get("SendMotorFaderFeedback", True),
        "input_faders_touch_sensitive": legacy.get("InputFadersAreTouchSensitive", True),
        "minimize_to_tray": legacy.get("MinimizeToTray", True),
        "start_with_windows": legacy.get("StartWithWindows", True),
        "legacy_icon_profile_imported": True,
    })
    controllers = []
    legacy_controllers = legacy.get("Controllers") or [{
        "Name": "iCON P1-M", "MidiInputName": legacy.get("MidiInputName", ""),
        "MidiOutputName": legacy.get("MidiOutputName", ""), "Enabled": True,
    }]
    for index, old in enumerate(legacy_controllers, start=1):
        identity = f"{old.get('Name', '')} {old.get('MidiInputName', '')} {old.get('MidiOutputName', '')}"
        profile = default_controller(index, infer_controller_type(identity, old.get("Type")))
        profile.update({
            "id": f"legacy-icon-{index}",
            "name": old.get("Name") or f"Controller {index}",
            "midi_input": old.get("MidiInputName", ""), "midi_output": old.get("MidiOutputName", ""),
            "enabled": bool(old.get("Enabled", True)),
        })
        controllers.append(profile)

    current_controllers = config.get("controllers", [])
    current_xtouch = next((item for item in current_controllers if item.get("type") == "behringer_xtouch"), None)
    if current_xtouch:
        controllers.append(deepcopy(current_xtouch))
    elif config.get("xtouch") or str(config.get("midi_device_hint", "")).lower().find("touch") >= 0:
        xtouch = default_controller(len(controllers) + 1, "behringer_xtouch")
        old_xtouch = config.get("xtouch", {})
        xtouch.update({
            "name": "X-Touch", "midi_input": config.get("midi_input", ""),
            "midi_output": config.get("midi_output", ""), "device_hint": config.get("midi_device_hint", "X-TOUCH"),
            "surface_mode": old_xtouch.get("surface_mode", "ctrl"),
            "color_mode": old_xtouch.get("color_mode", "mcu-72"),
            "meter_mode": old_xtouch.get("meter_mode", "mcu-packed-aftertouch"),
            "meter_gain_db": old_xtouch.get("meter_gain_db", 12.0),
        })
        controllers.append(xtouch)
    migrated["controllers"] = controllers

    channels = []
    for old in legacy.get("Channels", []):
        channels.append({
            "channel": old.get("Channel", len(channels) + 1),
            "kind": LEGACY_KIND.get(old.get("Kind"), str(old.get("Kind", "none"))),
            "input_key": old.get("InputKey") or "", "input_number": 0,
            "label_override": old.get("LabelOverride") or "",
            "follow_input_name": bool(old.get("FollowInputName", not old.get("LabelOverride"))),
            "strip_color": LEGACY_COLOR.get(old.get("StripColor"), "blue"),
        })
    if channels:
        migrated["channels"] = channels
    return migrated


def migrate_xtouch_config(config):
    migrated = deep_merge(DEFAULT_CONFIG, {})
    for key in ("web_host", "web_port", "vmix_host", "vmix_http_port", "poll_interval_ms", "log_midi", "master_fader"):
        if key in config:
            migrated[key] = deepcopy(config[key])
    controller = default_controller(1, "behringer_xtouch")
    xtouch = config.get("xtouch", {})
    controller.update({
        "name": "X-Touch", "midi_input": config.get("midi_input", ""), "midi_output": config.get("midi_output", ""),
        "device_hint": config.get("midi_device_hint", "X-TOUCH"), "surface_mode": xtouch.get("surface_mode", "ctrl"),
        "color_mode": xtouch.get("color_mode", "mcu-72"), "meter_mode": xtouch.get("meter_mode", "mcu-packed-aftertouch"),
        "meter_gain_db": xtouch.get("meter_gain_db", 12.0),
    })
    migrated["controllers"] = [controller]
    strips = config.get("strips", [])
    migrated["channel_count"] = max(8, len(strips))
    migrated["channels"] = [{
        "channel": index, "kind": strip.get("kind", "none"), "input_key": strip.get("input_key", ""),
        "input_number": strip.get("input_number", 0), "label_override": strip.get("label", ""),
        "follow_input_name": not bool(strip.get("override_label", False)), "strip_color": strip.get("color", "blue"),
    } for index, strip in enumerate(strips, start=1)]
    return migrated


def normalize_config(raw, legacy_profile=None):
    raw = deepcopy(raw or {})
    version = clamp(raw.get("config_version", 0), 0, 999, 0)
    if version < 4 and "strips" in raw:
        raw = migrate_xtouch_config(raw)
    config = deep_merge(DEFAULT_CONFIG, raw)
    if legacy_profile and not config.get("legacy_icon_profile_imported"):
        config = migrate_legacy_icon_profile(config, legacy_profile)
    config["config_version"] = 4
    config["web_port"] = clamp(config.get("web_port"), 1, 65535, 8091)
    if config["web_port"] in (8088, 8090):
        config["web_port"] = 8091
    config["vmix_http_port"] = clamp(config.get("vmix_http_port"), 1, 65535, 8088)
    config["vmix_tcp_port"] = clamp(config.get("vmix_tcp_port"), 1, 65535, 8099)
    config["api_port"] = clamp(config.get("api_port"), 1, 65535, 8097)
    config["poll_interval_ms"] = clamp(config.get("poll_interval_ms"), 50, 5000, 100)
    config["fader_write_interval_ms"] = clamp(config.get("fader_write_interval_ms"), 10, 500, 15)
    config["motor_feedback_hold_ms"] = clamp(config.get("motor_feedback_hold_ms"), 250, 5000, 1800)
    config["channel_count"] = clamp(config.get("channel_count"), 8, 64, 16)

    controllers = []
    for index, item in enumerate(config.get("controllers") or [default_controller()], start=1):
        controller_type = str(item.get("type") or infer_controller_type(item.get("name"))).lower()
        if controller_type not in CONTROLLER_TYPES:
            controller_type = infer_controller_type(item.get("name"))
        controller = deep_merge(default_controller(index, controller_type), item)
        controller["id"] = str(controller.get("id") or f"controller-{index}")
        controller["name"] = str(controller.get("name") or f"Controller {index}").strip()
        controller["type"] = controller_type
        controller["midi_input"] = str(controller.get("midi_input") or "").strip()
        controller["midi_output"] = str(controller.get("midi_output") or "").strip()
        controller["bank_start"] = clamp(controller.get("bank_start"), 0, max(0, config["channel_count"] - 8), 0)
        controller["surface_mode"] = "ctrl" if str(controller.get("surface_mode")).lower() == "ctrl" else "mcu"
        controller["button_mode"] = "toggle_mute" if controller.get("button_mode") == "toggle_mute" else "volume_100_0"
        controllers.append(controller)
    config["controllers"] = controllers

    by_channel = {}
    for index, item in enumerate(config.get("channels", []), start=1):
        number = clamp(item.get("channel", index), 1, config["channel_count"], index)
        channel = deep_merge(default_channel(number), item)
        channel["channel"] = number
        channel["kind"] = normalized_kind(channel.get("kind"))
        channel["input_key"] = str(channel.get("input_key") or "")
        channel["input_number"] = clamp(channel.get("input_number"), 0, 9999, 0)
        channel["label_override"] = str(channel.get("label_override") or channel.get("label") or "")
        channel["follow_input_name"] = bool(channel.get("follow_input_name", not channel["label_override"]))
        channel["strip_color"] = normalized_color(channel.get("strip_color") or channel.get("color"))
        by_channel[number] = channel
    config["channels"] = [by_channel.get(index, default_channel(index)) for index in range(1, config["channel_count"] + 1)]
    config["master_fader"] = deep_merge(DEFAULT_CONFIG["master_fader"], config.get("master_fader", {}))
    return config


class ConfigStore:
    def __init__(self, path, legacy_path=None):
        self.path = Path(path)
        self.lock = threading.RLock()
        self.legacy_path = Path(legacy_path) if legacy_path else None
        self.config = self._load()

    def _load(self):
        raw = {}
        if self.path.exists():
            with self.path.open("r", encoding="utf-8") as handle:
                raw = json.load(handle)
        legacy = None
        if self.legacy_path and self.legacy_path.exists():
            try:
                with self.legacy_path.open("r", encoding="utf-8-sig") as handle:
                    legacy = json.load(handle)
            except Exception:
                logging.exception("Could not read legacy Icon profile")
        migrated = normalize_config(raw, legacy)
        if raw != migrated:
            if self.path.exists() and not self.path.with_name("config.pre-v4.json").exists():
                shutil.copy2(self.path, self.path.with_name("config.pre-v4.json"))
            self._write(migrated)
        return migrated

    def _write(self, value):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(".tmp")
        with temp.open("w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2)
            handle.write("\n")
        temp.replace(self.path)

    def get(self):
        with self.lock:
            return deepcopy(self.config)

    def save(self, value):
        with self.lock:
            self.config = normalize_config(value)
            self._write(self.config)
            return deepcopy(self.config)

    def set_bank(self, controller_id, bank_start):
        with self.lock:
            for controller in self.config["controllers"]:
                if controller["id"] == controller_id:
                    controller["bank_start"] = clamp(bank_start, 0, max(0, self.config["channel_count"] - 8), 0)
                    break
            self._write(self.config)


class VMixClient:
    def __init__(self):
        self.lock = threading.Lock()
        self.state = {"connected": False, "status": "Not polled yet", "inputs": [], "outputs": {}}

    @staticmethod
    def base_url(config):
        return f"http://{config['vmix_host']}:{int(config['vmix_http_port'])}/api/"

    def poll(self, config, timeout=2.0):
        try:
            with urllib.request.urlopen(self.base_url(config), timeout=timeout) as response:
                root = ET.fromstring(response.read())
            inputs = []
            for item in root.findall("./inputs/input"):
                inputs.append({
                    "number": int(item.attrib.get("number", 0) or 0), "key": item.attrib.get("key", ""),
                    "title": item.attrib.get("title", "") or item.text or "",
                    "volume": float(item.attrib.get("volume", 0) or 0),
                    "muted": item.attrib.get("muted", "false").lower() == "true",
                    "meter": max(float(item.attrib.get("meterF1", 0) or 0), float(item.attrib.get("meterF2", 0) or 0)),
                })
            outputs = {}
            audio = root.find("./audio")
            if audio is not None:
                for child in list(audio):
                    outputs[child.tag.lower()] = {
                        "volume": float(child.attrib.get("volume", 0) or 0),
                        "meter": max(float(child.attrib.get("meterF1", 0) or 0), float(child.attrib.get("meterF2", 0) or 0)),
                    }
                for key, value in audio.attrib.items():
                    lower = key.lower()
                    if lower.endswith("volume"):
                        outputs.setdefault(lower[:-6], {})["volume"] = float(value or 0)
                    elif lower.endswith("meterf1") or lower.endswith("meterf2"):
                        suffix = "meterf1" if lower.endswith("meterf1") else "meterf2"
                        name = lower[:-len(suffix)]
                        outputs.setdefault(name, {})["meter"] = max(outputs.get(name, {}).get("meter", 0), float(value or 0))
            state = {"connected": True, "status": f"Connected. {len(inputs)} inputs.", "inputs": inputs, "outputs": outputs}
        except Exception as exc:
            state = {"connected": False, "status": str(exc), "inputs": [], "outputs": {}}
        with self.lock:
            self.state = state
        return deepcopy(state)

    def get_state(self):
        with self.lock:
            return deepcopy(self.state)

    def send_function(self, config, function, params=None):
        query = {"Function": function}
        query.update({key: value for key, value in (params or {}).items() if value not in (None, "")})
        with urllib.request.urlopen(self.base_url(config) + "?" + urllib.parse.urlencode(query), timeout=2.0) as response:
            response.read()


class ControllerRuntime:
    def __init__(self, profile):
        self.profile = deepcopy(profile)
        self.input = None
        self.output = None
        self.lock = threading.RLock()
        self.bank_start = int(profile.get("bank_start", 0))
        self.last_midi = ""
        self.error = ""
        self.messages = 0
        # The ninth slot is the dedicated master fader.
        self.touched = [False] * 9
        self.last_touch = [0.0] * 9
        self.suppress_until = [0.0] * 9
        self.ignore_until = [0.0] * 9
        self.last_motor_raw = [-1] * 9
        self.last_sent_percent = [None] * 9
        self.pending = {}
        self.last_labels = [None] * 8
        self.last_colors = None
        self.last_xtouch_packets = [None] * 8
        self.last_bank_leds = None

    def close(self):
        for port in (self.input, self.output):
            try:
                if port:
                    port.close()
            except Exception:
                pass
        self.input = self.output = None

    def status(self):
        return {
            "id": self.profile["id"], "name": self.profile["name"], "type": self.profile["type"],
            "input_open": bool(self.input), "output_open": bool(self.output),
            "midi_input": getattr(self.input, "name", "") if self.input else "",
            "midi_output": getattr(self.output, "name", "") if self.output else "",
            "bank_start": self.bank_start, "last_midi": self.last_midi, "messages": self.messages, "error": self.error,
        }


class Bridge:
    def __init__(self, store):
        self.store = store
        self.vmix = VMixClient()
        self.running = threading.Event()
        self.running.set()
        self.reload_event = threading.Event()
        self.runtime_lock = threading.RLock()
        self.runtimes = []
        self.last_error = ""

    def start(self):
        threading.Thread(target=self._controller_loop, daemon=True).start()
        threading.Thread(target=self._poll_loop, daemon=True).start()
        threading.Thread(target=self._fader_loop, daemon=True).start()

    def stop(self):
        self.running.clear()
        self.reload_event.set()
        with self.runtime_lock:
            for runtime in self.runtimes:
                runtime.close()

    def reload(self):
        self.reload_event.set()

    @staticmethod
    def midi_devices():
        return {"inputs": list(mido.get_input_names()), "outputs": list(mido.get_output_names())}

    def get_status(self):
        with self.runtime_lock:
            controllers = [runtime.status() for runtime in self.runtimes]
        return {"started_at": getattr(self, "started_at", time.time()), "last_error": self.last_error, "controllers": controllers, "vmix": self.vmix.get_state()}

    @staticmethod
    def _pick_device(devices, requested, hint, used):
        choices = []
        if requested:
            choices.extend([item for item in devices if item.lower() == requested.lower()])
            choices.extend([item for item in devices if requested.lower() in item.lower()])
        if hint:
            choices.extend([item for item in devices if hint.lower() in item.lower()])
        # A configured identity must never fall through to an unrelated MIDI
        # device when the intended controller is temporarily powered off.
        if not requested and not hint:
            choices.extend(devices)
        return next((item for item in choices if item not in used and "Microsoft GS Wavetable" not in item), "")

    @staticmethod
    def _device_matches_profile(name, requested, hint):
        lowered = name.lower()
        selectors = [str(value).lower() for value in (requested, hint) if value]
        return not selectors or any(selector in lowered for selector in selectors)

    def _controller_loop(self):
        self.started_at = time.time()
        self._open_controllers()
        while self.running.is_set():
            if not self.reload_event.wait(2.0):
                self._reconnect_missing_controllers()
                continue
            self.reload_event.clear()
            with self.runtime_lock:
                old = self.runtimes
                self.runtimes = []
            for runtime in old:
                runtime.close()
            if self.running.is_set():
                self._open_controllers()

    def _open_controllers(self):
        config = self.store.get()
        devices = self.midi_devices()
        used_inputs, used_outputs = set(), set()
        runtimes = []
        for profile in config["controllers"]:
            runtime = ControllerRuntime(profile)
            runtimes.append(runtime)
            self._open_runtime(runtime, devices, used_inputs, used_outputs)
        with self.runtime_lock:
            old = self.runtimes
            self.runtimes = runtimes
        for runtime in old:
            runtime.close()
        self.refresh_surface(force=True)

    @staticmethod
    def _port_name(port):
        return str(getattr(port, "name", "") or "")

    @staticmethod
    def _port_is_open(port):
        return bool(port) and not bool(getattr(port, "closed", False))

    def _open_runtime(self, runtime, devices, used_inputs, used_outputs):
        profile = runtime.profile
        if not profile.get("enabled", True):
            runtime.close()
            runtime.error = ""
            return False
        try:
            input_name = self._pick_device(
                devices["inputs"],
                profile.get("midi_input", ""),
                profile.get("device_hint", ""),
                used_inputs,
            )
            output_name = self._pick_device(
                devices["outputs"],
                profile.get("midi_output", ""),
                profile.get("device_hint", ""),
                used_outputs,
            )
            if not input_name or not output_name:
                raise RuntimeError("Matching MIDI input and output were not found; waiting to reconnect")
            runtime.output = mido.open_output(output_name)
            runtime.input = mido.open_input(
                input_name,
                callback=lambda message, current=runtime: self._handle_midi(current, message),
            )
            runtime.error = ""
            runtime.last_labels = [None] * 8
            runtime.last_xtouch_packets = [None] * 8
            runtime.last_bank_leds = None
            used_inputs.add(input_name)
            used_outputs.add(output_name)
            logging.info(
                "%s connected: input '%s', output '%s'",
                profile["name"],
                input_name,
                output_name,
            )
            return True
        except Exception as exc:
            runtime.close()
            runtime.error = str(exc)
            return False

    def _runtime_is_connected(self, runtime, devices):
        profile = runtime.profile
        input_name = self._port_name(runtime.input)
        output_name = self._port_name(runtime.output)
        return (
            self._port_is_open(runtime.input)
            and self._port_is_open(runtime.output)
            and input_name in devices["inputs"]
            and output_name in devices["outputs"]
            and self._device_matches_profile(
                input_name,
                profile.get("midi_input", ""),
                profile.get("device_hint", ""),
            )
            and self._device_matches_profile(
                output_name,
                profile.get("midi_output", ""),
                profile.get("device_hint", ""),
            )
        )

    def _reconnect_missing_controllers(self):
        try:
            devices = self.midi_devices()
        except Exception as exc:
            self.last_error = f"MIDI device scan failed: {exc}"
            return
        with self.runtime_lock:
            runtimes = list(self.runtimes)
        used_inputs = {
            self._port_name(runtime.input)
            for runtime in runtimes
            if self._runtime_is_connected(runtime, devices)
        }
        used_outputs = {
            self._port_name(runtime.output)
            for runtime in runtimes
            if self._runtime_is_connected(runtime, devices)
        }
        reconnected = False
        for runtime in runtimes:
            if not runtime.profile.get("enabled", True):
                continue
            if self._runtime_is_connected(runtime, devices):
                continue
            if runtime.input or runtime.output:
                logging.warning("%s disconnected; waiting for MIDI device", runtime.profile["name"])
            runtime.close()
            if self._open_runtime(runtime, devices, used_inputs, used_outputs):
                reconnected = True
        if reconnected:
            self.refresh_surface(force=True)

    def _poll_loop(self):
        while self.running.is_set():
            config = self.store.get()
            try:
                self.vmix.poll(config)
                self.refresh_surface()
                self.last_error = ""
            except Exception as exc:
                self.last_error = str(exc)
            time.sleep(max(0.05, config["poll_interval_ms"] / 1000.0))

    def _fader_loop(self):
        while self.running.is_set():
            config = self.store.get()
            with self.runtime_lock:
                runtimes = list(self.runtimes)
            for runtime in runtimes:
                with runtime.lock:
                    pending = list(runtime.pending.items())
                    runtime.pending.clear()
                for physical, percent in pending:
                    assignment = config["master_fader"] if physical == 8 else self._assignment_for(runtime, physical, config)
                    if assignment:
                        try:
                            self.set_assignment_volume(assignment, percent)
                            runtime.last_sent_percent[physical] = percent
                        except Exception as exc:
                            runtime.error = str(exc)
            time.sleep(max(0.01, config["fader_write_interval_ms"] / 1000.0))

    @staticmethod
    def _input_for(assignment, state):
        for item in state.get("inputs", []):
            if assignment.get("input_key") and item["key"] == assignment["input_key"]:
                return item
            if assignment.get("input_number") and item["number"] == assignment["input_number"]:
                return item
        return None

    def live_for(self, assignment, state=None):
        state = state or self.vmix.get_state()
        kind = assignment.get("kind", "none")
        if kind == "input":
            item = self._input_for(assignment, state)
            return {"volume": item.get("volume"), "meter": item.get("meter", 0), "muted": item.get("muted", False)} if item else {"volume": None, "meter": 0, "muted": False}
        name = kind.lower()
        output = state.get("outputs", {}).get(name, {})
        return {"volume": output.get("volume"), "meter": output.get("meter", 0), "muted": False}

    def label_for(self, assignment, state=None):
        if assignment.get("label_override") and not assignment.get("follow_input_name", False):
            return assignment["label_override"]
        if assignment.get("kind") == "input":
            item = self._input_for(assignment, state or self.vmix.get_state())
            return item.get("title", "") if item else assignment.get("label_override") or "Input"
        return assignment.get("label_override") or assignment.get("kind", "none")

    @staticmethod
    def vmix_to_fader(volume):
        return math.pow(max(0.0, min(100.0, float(volume))) / 100.0, 0.25) * 100.0

    @staticmethod
    def fader_raw(percent):
        return int(round(max(0.0, min(100.0, percent)) / 100.0 * 16383))

    @staticmethod
    def meter_level(value, gain_db=0.0):
        value = max(0.0, float(value or 0))
        normalized = value / 100.0 if value > 1 else value
        if normalized <= 0.00001:
            return 0
        db = 20 * math.log10(normalized) + float(gain_db)
        return max(1, min(12, int(round(max(0, min(100, (db + 60) / 54 * 100)) / 100 * 12))))

    @staticmethod
    def _fit(text):
        return "".join(char if 32 <= ord(char) <= 126 else " " for char in str(text or ""))[:7].ljust(7)

    def _send_text(self, runtime, physical, label, bottom=""):
        if not runtime.output:
            return
        for offset, text in ((physical * 7, label), (56 + physical * 7, bottom)):
            runtime.output.send(mido.Message("sysex", data=[0, 0, 0x66, 0x14, 0x12, offset] + [ord(char) for char in self._fit(text)]))

    def _send_icon_colors(self, runtime, colors):
        values = []
        for color in colors[:8]:
            values.extend(ICON_RGB[normalized_color(color)])
        packet = tuple(values)
        if packet != runtime.last_colors and runtime.output:
            runtime.output.send(mido.Message("sysex", data=[0, 0x02, 0x4E, 0x16, 0x14] + values))
            runtime.last_colors = packet

    def _send_xtouch_color(self, runtime, physical, color, label, bottom, force=False):
        if not runtime.output:
            return
        profile = runtime.profile
        mode = str(profile.get("color_mode", "mcu-72")).lower()
        if mode == "off":
            return
        index = XTOUCH_COLOR_INDEX[normalized_color(color)]
        packet_key = (mode, index, self._fit(label), self._fit(bottom))
        if not force and runtime.last_xtouch_packets[physical] == packet_key:
            return
        candidates = []
        if mode in ("mcu-72", "all"):
            candidates.append([0, 0, 0x66, 0x14, 0x72, physical, index])
        if mode in ("behringer-72", "all"):
            candidates.append([0, 0x20, 0x32, 0x14, 0x72, physical, index])
        if mode in ("behringer-4c", "all"):
            candidates.append([0, 0x20, 0x32, 0x15, 0, 0x4C, physical, index])
        combined_text = self._fit(label) + self._fit(bottom)
        candidates.append(
            [0, 0x20, 0x32, 0x14, 0x4C, physical, index]
            + [ord(char) for char in combined_text]
        )
        for data in candidates:
            try:
                runtime.output.send(mido.Message("sysex", data=data))
            except Exception:
                pass
        runtime.last_xtouch_packets[physical] = packet_key

    def _send_fader(self, runtime, physical, percent, force=False):
        if not runtime.output:
            return
        raw = self.fader_raw(percent)
        if not force and runtime.last_motor_raw[physical] == raw:
            return
        if runtime.profile["type"] == "behringer_xtouch" and runtime.profile.get("surface_mode") == "ctrl":
            runtime.output.send(mido.Message("control_change", channel=0, control=70 + physical, value=int(round(percent * 127 / 100))))
        else:
            runtime.output.send(mido.Message("pitchwheel", channel=physical, pitch=raw - 8192))
        runtime.last_motor_raw[physical] = raw
        runtime.suppress_until[physical] = time.time() + 0.25

    def _send_meter(self, runtime, physical, value):
        if not runtime.output:
            return
        level = self.meter_level(value, runtime.profile.get("meter_gain_db", 0))
        if runtime.profile["type"] == "behringer_xtouch" and runtime.profile.get("surface_mode") == "ctrl":
            runtime.output.send(mido.Message("control_change", channel=0, control=90 + physical, value=int(round(level * 127 / 12))))
        else:
            runtime.output.send(mido.Message("aftertouch", channel=0, value=(physical << 4) | level))

    def _send_bank_leds(self, runtime, config, force=False):
        if not runtime.output or runtime.profile["type"] != "behringer_xtouch":
            return
        state = (
            runtime.bank_start > 0,
            runtime.bank_start + 8 < config["channel_count"],
        )
        if not force and runtime.last_bank_leds == state:
            return
        ctrl_mode = runtime.profile.get("surface_mode") == "ctrl"
        left_note, right_note = (92, 93) if ctrl_mode else (0x2E, 0x2F)
        runtime.output.send(
            mido.Message("note_on", note=left_note, velocity=127 if state[0] else 0)
        )
        runtime.output.send(
            mido.Message("note_on", note=right_note, velocity=127 if state[1] else 0)
        )
        runtime.last_bank_leds = state

    def refresh_surface(self, force=False):
        config = self.store.get()
        state = self.vmix.get_state()
        with self.runtime_lock:
            runtimes = list(self.runtimes)
        for runtime in runtimes:
            if not runtime.output:
                continue
            colors = []
            for physical in range(8):
                assignment = self._assignment_for(runtime, physical, config)
                if not assignment:
                    label, color, live = "", "off", {"volume": None, "meter": 0, "muted": False}
                else:
                    label, color, live = self.label_for(assignment, state), assignment["strip_color"], self.live_for(assignment, state)
                colors.append(color)
                if config.get("send_scribble_text", True) and (force or runtime.last_labels[physical] != label):
                    self._send_text(runtime, physical, label, assignment.get("kind", "") if assignment else "")
                    runtime.last_labels[physical] = label
                if runtime.profile["type"] == "behringer_xtouch" and config.get("send_scribble_color", True):
                    self._send_xtouch_color(
                        runtime,
                        physical,
                        color,
                        label,
                        assignment.get("kind", "") if assignment else "",
                        force=force,
                    )
                if config.get("send_motor_fader_feedback", True) and live["volume"] is not None:
                    now = time.time()
                    hold = config["motor_feedback_hold_ms"] / 1000.0
                    if not runtime.touched[physical] and now - runtime.last_touch[physical] > hold:
                        self._send_fader(runtime, physical, self.vmix_to_fader(live["volume"]), force=force)
                if config.get("send_meters", True):
                    self._send_meter(runtime, physical, live["meter"])
                if runtime.profile["type"] == "behringer_xtouch":
                    ctrl_mode = runtime.profile.get("surface_mode") == "ctrl"
                    rec_note = (8 if ctrl_mode else 0) + physical
                    mute_note = (24 if ctrl_mode else 16) + physical
                    volume = live["volume"]
                    runtime.output.send(
                        mido.Message("note_on", note=rec_note, velocity=127 if volume is not None and volume >= 99.5 else 0)
                    )
                    runtime.output.send(
                        mido.Message("note_on", note=mute_note, velocity=127 if volume is not None and volume <= 0.5 else 0)
                    )
            if runtime.profile["type"] == "icon_p1m" and config.get("send_scribble_color", True):
                self._send_icon_colors(runtime, colors)
            if runtime.profile.get("master_fader_enabled"):
                live = self.live_for(config["master_fader"], state)
                if live["volume"] is not None:
                    self._send_fader(runtime, 8, self.vmix_to_fader(live["volume"]), force=force)
            self._send_bank_leds(runtime, config, force=force)

    @staticmethod
    def _assignment_for(runtime, physical, config):
        logical = runtime.bank_start + physical
        return config["channels"][logical] if 0 <= logical < len(config["channels"]) else None

    def _change_bank(self, runtime, delta):
        config = self.store.get()
        old_start = runtime.bank_start
        runtime.bank_start = max(
            0,
            min(max(0, config["channel_count"] - 8), runtime.bank_start + delta),
        )
        if runtime.bank_start == old_start:
            self._send_bank_leds(runtime, config, force=True)
            return
        self.store.set_bank(runtime.profile["id"], runtime.bank_start)
        runtime.last_labels = [None] * 8
        self.refresh_surface(force=True)

    def change_bank(self, controller_id, delta):
        with self.runtime_lock:
            runtime = next((item for item in self.runtimes if item.profile["id"] == controller_id), None)
        if runtime:
            self._change_bank(runtime, int(delta))

    def _handle_midi(self, runtime, message):
        runtime.messages += 1
        runtime.last_midi = str(message)
        config = self.store.get()
        if config.get("log_midi"):
            logging.info("%s MIDI: %s", runtime.profile["name"], message)
        try:
            if message.type == "note_on" and 104 <= message.note <= 111:
                physical = message.note - 104
                touched = message.velocity > 0
                now = time.time()
                if now < runtime.suppress_until[physical] or now < runtime.ignore_until[physical]:
                    runtime.touched[physical] = False
                    return
                runtime.touched[physical] = touched
                runtime.last_touch[physical] = now if touched else 0
                if not touched:
                    runtime.ignore_until[physical] = now + 0.1
                    if runtime.last_motor_raw[physical] >= 0:
                        self._send_fader(runtime, physical, runtime.last_motor_raw[physical] / 16383 * 100, force=True)
                return
            if message.type == "note_on" and message.velocity > 0:
                nav = {0x2E: -8, 0x2F: 8, 0x30: -1, 0x31: 1}
                if runtime.profile["type"] == "behringer_xtouch" and runtime.profile.get("surface_mode") == "ctrl":
                    nav.update({92: -8, 93: 8})
                if message.note in nav:
                    self._change_bank(runtime, nav[message.note])
                    return
                if self._handle_channel_button(runtime, message.note, config):
                    return
            if (
                runtime.profile["type"] == "behringer_xtouch"
                and runtime.profile.get("surface_mode") == "ctrl"
                and message.type == "control_change"
                and message.value > 0
            ):
                navigation = {0x2E: -8, 0x2F: 8, 0x30: -1, 0x31: 1}
                if message.control in navigation:
                    self._change_bank(runtime, navigation[message.control])
                    return
                if self._handle_channel_button(runtime, message.control, config):
                    return
            if message.type == "control_change" and message.control == 60:
                self._change_bank(runtime, 8 if message.value <= 63 else -8)
                return
            if (
                runtime.profile["type"] == "behringer_xtouch"
                and runtime.profile.get("surface_mode") == "ctrl"
                and message.type == "control_change"
                and message.control == 88
            ):
                self._change_bank(runtime, -8 if message.value <= 63 else 8)
                return
            if runtime.profile["type"] == "behringer_xtouch" and runtime.profile.get("surface_mode") == "ctrl" and message.type == "control_change" and 70 <= message.control <= 78:
                physical = message.control - 70
                percent = message.value / 127 * 100
            elif message.type == "pitchwheel" and 0 <= message.channel <= 8:
                physical = message.channel
                percent = (message.pitch + 8192) / 16383 * 100
            else:
                return
            if physical == 8:
                if runtime.profile.get("master_fader_enabled"):
                    with runtime.lock:
                        runtime.pending[8] = percent
                return
            now = time.time()
            if now < runtime.suppress_until[physical] or now < runtime.ignore_until[physical]:
                return
            raw = self.fader_raw(percent)
            if config.get("input_faders_touch_sensitive", True) and runtime.profile["type"] != "behringer_xtouch" and not runtime.touched[physical]:
                if runtime.last_motor_raw[physical] >= 0 and abs(raw - runtime.last_motor_raw[physical]) < 96:
                    return
                runtime.touched[physical] = True
            runtime.last_touch[physical] = now
            runtime.last_motor_raw[physical] = raw
            with runtime.lock:
                runtime.pending[physical] = percent
        except Exception as exc:
            runtime.error = str(exc)

    def _handle_channel_button(self, runtime, control, config):
        ctrl_mode = runtime.profile["type"] == "behringer_xtouch" and runtime.profile.get("surface_mode") == "ctrl"
        rec_base = 8 if ctrl_mode else 0
        mute_base = 24 if ctrl_mode else 16
        if rec_base <= control <= rec_base + 7:
            assignment = self._assignment_for(runtime, control - rec_base, config)
            if assignment:
                self.set_assignment_volume(assignment, 100)
            return True
        if mute_base <= control <= mute_base + 7:
            assignment = self._assignment_for(runtime, control - mute_base, config)
            if assignment:
                if runtime.profile.get("button_mode") == "toggle_mute":
                    self.toggle_assignment_mute(assignment)
                else:
                    self.set_assignment_volume(assignment, 0)
            return True
        return False

    def _function_for(self, assignment, action):
        kind = assignment.get("kind", "none")
        suffix = kind[3:].upper() if kind.lower().startswith("bus") else ""
        if action == "volume":
            if kind == "input":
                return "SetVolume", {"Input": assignment.get("input_key") or assignment.get("input_number")}
            if kind == "master":
                return "SetMasterVolume", {}
            if suffix in set("ABCDEFG"):
                return f"SetBus{suffix}Volume", {}
        if action == "mute":
            if kind == "input":
                return "Audio", {"Input": assignment.get("input_key") or assignment.get("input_number")}
            if kind == "master":
                return "MasterAudio", {}
            if suffix in set("ABCDEFG"):
                return f"Bus{suffix}Audio", {}
        return "", {}

    def set_assignment_volume(self, assignment, percent):
        function, params = self._function_for(assignment, "volume")
        if function:
            params["Value"] = max(0, min(100, int(round(percent))))
            self.vmix.send_function(self.store.get(), function, params)

    def toggle_assignment_mute(self, assignment):
        function, params = self._function_for(assignment, "mute")
        if function:
            self.vmix.send_function(self.store.get(), function, params)

    def test_controller(self, controller_id):
        with self.runtime_lock:
            runtime = next((item for item in self.runtimes if item.profile["id"] == controller_id), None)
        if not runtime or not runtime.output:
            return False
        for physical in range(8):
            self._send_text(runtime, physical, f"VMIX {physical + 1}", "TEST")
            self._send_fader(runtime, physical, 75 if physical % 2 == 0 else 25, force=True)
            self._send_meter(runtime, physical, (physical + 1) / 8)
        if runtime.profile["type"] == "icon_p1m":
            self._send_icon_colors(runtime, STRIP_COLORS[1:9])
        return True

    def assignments_snapshot(self):
        config = self.store.get()
        state = self.vmix.get_state()
        channels = []
        for assignment in config["channels"]:
            item = deepcopy(assignment)
            item["label"] = self.label_for(assignment, state)
            item["live"] = self.live_for(assignment, state)
            channels.append(item)
        return {"channels": channels, "inputs": state["inputs"], "assignmentKinds": ASSIGNMENT_KINDS, "stripColors": STRIP_COLORS}

    def update_channel(self, number, request):
        config = self.store.get()
        if number < 1 or number > len(config["channels"]):
            raise ValueError(f"Channel must be 1-{len(config['channels'])}")
        channel = config["channels"][number - 1]
        for source, target in (("kind", "kind"), ("inputKey", "input_key"), ("input_key", "input_key"), ("inputNumber", "input_number"), ("labelOverride", "label_override"), ("stripColor", "strip_color")):
            if source in request and request[source] is not None:
                channel[target] = request[source]
        if "inputTitle" in request and request["inputTitle"]:
            match = next((item for item in self.vmix.get_state()["inputs"] if item["title"].lower() == str(request["inputTitle"]).lower()), None)
            if match:
                channel["input_key"] = match["key"]
        channel["follow_input_name"] = not bool(channel.get("label_override"))
        saved = self.store.save(config)
        self.refresh_surface(force=True)
        return saved["channels"][number - 1]
