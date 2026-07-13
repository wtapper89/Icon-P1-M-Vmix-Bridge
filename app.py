#!/usr/bin/env python3
import json
import logging
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from bridge_core import (
    ASSIGNMENT_KINDS,
    CONTROLLER_TYPES,
    STRIP_COLORS,
    Bridge,
    ConfigStore,
    default_channel,
    default_controller,
)


APP_NAME = "vMix MIDI Surface Bridge"
CONFIG_PATH = Path(os.environ.get("VMIX_MIDI_SURFACE_CONFIG", os.environ.get("VMIX_XTOUCH_CONFIG", os.environ.get("VMIX_CONTROL_SURFACE_CONFIG", "config.json"))))
LOCAL_APP_DATA = Path(os.environ.get("LOCALAPPDATA", Path.home()))
LEGACY_ICON_PROFILE = LOCAL_APP_DATA / "IconP1MVmixBridge" / "profile.json"
LOG_DIR = CONFIG_PATH.parent / "logs"


HTML = r"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>vMix MIDI Surface Bridge</title>
  <style>
    :root{color-scheme:dark;--bg:#0d0f12;--panel:#15191e;--line:#2c333c;--text:#eef2f6;--muted:#96a0ad;--blue:#53a6ff;--danger:#e16b6b}
    *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px Arial,sans-serif;letter-spacing:0}
    header{position:sticky;top:0;z-index:5;display:flex;align-items:center;justify-content:space-between;gap:16px;padding:16px 22px;background:#101318;border-bottom:1px solid var(--line)}
    h1{font-size:22px;margin:0}h2{font-size:16px;margin:0 0 14px}h3{font-size:15px;margin:0}.sub{color:var(--muted);margin-top:4px}
    main{max-width:1500px;margin:auto;padding:18px 22px 48px}section{padding:18px 0;border-bottom:1px solid var(--line)}
    .grid{display:grid;grid-template-columns:repeat(6,minmax(140px,1fr));gap:12px}.grid.compact{grid-template-columns:repeat(4,minmax(150px,1fr))}
    label{display:grid;gap:6px;color:var(--muted);font-size:12px}input,select{width:100%;min-height:38px;border:1px solid #343d48;border-radius:4px;background:#0f1318;color:var(--text);padding:8px;font:inherit}
    input[type=checkbox]{width:17px;min-height:17px;accent-color:var(--blue)}.check{display:flex;align-items:center;gap:8px;min-height:38px;color:var(--text);font-size:13px}
    button{border:1px solid #3b4653;border-radius:5px;background:#202731;color:var(--text);min-height:38px;padding:8px 13px;font-weight:600;cursor:pointer}button.primary{background:#1976d2;border-color:#3996ed}button.danger{color:#ffadad}.actions{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
    .status-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.status-item{padding:11px;border-left:3px solid #46515f;background:var(--panel)}.status-item strong{display:block;margin-bottom:5px}.status-item span{color:var(--muted)}
    .tabs{display:flex;gap:4px;overflow:auto;border-bottom:1px solid var(--line);margin-bottom:15px}.tab{border:0;border-radius:4px 4px 0 0;background:transparent;color:var(--muted);white-space:nowrap}.tab.active{background:#202731;color:var(--text);box-shadow:inset 0 -2px var(--blue)}
    .controller-head{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:12px}.controller-panel{background:var(--panel);padding:14px;border:1px solid var(--line);border-radius:6px}.controller-options{margin-top:12px;padding-top:12px;border-top:1px solid var(--line)}
    .table-wrap{overflow:auto;border:1px solid var(--line);border-radius:5px}table{width:100%;border-collapse:collapse;min-width:1050px}th,td{padding:8px;border-bottom:1px solid #282e36;text-align:left}th{position:sticky;top:71px;background:#171c22;color:var(--muted);font-size:12px;z-index:2}td input,td select{min-height:34px;padding:6px}.channel-number{font-weight:bold;width:55px}.in-bank{background:#172637}.swatch{width:17px;height:17px;border:1px solid #68727e;border-radius:2px;display:inline-block;vertical-align:middle;margin-right:6px}
    .hidden{display:none!important}.error{color:#ff9b9b}.live{font-variant-numeric:tabular-nums;color:#a9d3ff}.footer-status{position:fixed;bottom:0;left:0;right:0;background:#101318;border-top:1px solid var(--line);padding:8px 22px;color:var(--muted)}
    @media(max-width:1000px){.grid,.grid.compact{grid-template-columns:repeat(2,minmax(140px,1fr))}.status-grid{grid-template-columns:repeat(2,1fr)}}
    @media(max-width:600px){header{align-items:flex-start;padding:12px}main{padding:10px 12px 48px}.grid,.grid.compact,.status-grid{grid-template-columns:1fr}.actions{width:100%}button{flex:1}}
  </style>
</head>
<body>
<header><div><h1>vMix MIDI Surface Bridge</h1><div class="sub">iCON P1-M, Behringer X-Touch, and Mackie Control surfaces</div></div><div class="actions"><button id="refresh-vmix">Refresh vMix</button><button class="primary" id="save">Save settings</button></div></header>
<main>
  <section><h2>Status</h2><div class="status-grid" id="status"></div></section>
  <section><h2>vMix and Bridge</h2><div class="grid">
    <label>vMix Host<input id="vmix_host"></label><label>HTTP Port<input id="vmix_http_port" type="number"></label><label>TCP Port<input id="vmix_tcp_port" type="number"></label>
    <label>Browser Port<input id="web_port" type="number"></label><label>Assignment API Port<input id="api_port" type="number"></label><label>Logical Channels<input id="channel_count" type="number" min="8" max="64"></label>
    <label>Poll ms<input id="poll_interval_ms" type="number" min="50" max="5000"></label><label>Fader Write ms<input id="fader_write_interval_ms" type="number" min="10" max="500"></label><label>Motor Hold ms<input id="motor_feedback_hold_ms" type="number" min="250" max="5000"></label>
    <label class="check"><input id="send_motor_fader_feedback" type="checkbox">Motor fader feedback</label><label class="check"><input id="send_scribble_text" type="checkbox">Display text feedback</label><label class="check"><input id="send_scribble_color" type="checkbox">Display color feedback</label>
    <label class="check"><input id="send_meters" type="checkbox">Meter feedback</label><label class="check"><input id="input_faders_touch_sensitive" type="checkbox">P1-M touch sensitive</label><label class="check"><input id="start_with_windows" type="checkbox">Start with Windows</label>
    <label class="check"><input id="minimize_to_tray" type="checkbox">Run in background</label><label class="check"><input id="log_midi" type="checkbox">Log MIDI messages</label>
  </div></section>
  <section><div class="controller-head"><h2>Controllers</h2><button id="add-controller">Add controller</button></div><div class="tabs" id="controller-tabs"></div><div id="controller-editor"></div></section>
  <section><div class="controller-head"><div><h2>Channel Assignments</h2><div class="sub" id="input-count"></div></div><div class="actions"><button id="bank-left">Bank left</button><button id="bank-right">Bank right</button><button id="test-controller">Test controller</button><button id="refresh-surface">Refresh surface</button></div></div>
    <div class="table-wrap"><table><thead><tr><th>Channel</th><th>Assignment</th><th>vMix Input</th><th>Input #</th><th>Label Override</th><th>Follow Name</th><th>Strip Color</th><th>Volume</th><th>Meter</th></tr></thead><tbody id="channels"></tbody></table></div>
  </section>
  <section><h2>X-Touch Master Fader</h2><div class="grid compact"><label>Assignment<select id="master_kind"></select></label><label>vMix Input<select id="master_input_key"></select></label><label>Label<input id="master_label"></label><label>Color<select id="master_color"></select></label></div></section>
</main><div class="footer-status" id="footer">Loading...</div>
<script>
let config,devices={inputs:[],outputs:[]},vmix={inputs:[],outputs:{}},selectedController=0,statusData={controllers:[]};
const kinds=%KINDS%,colors=%COLORS%,types=%TYPES%; const $=id=>document.getElementById(id); const esc=v=>String(v??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const typeLabel={icon_p1m:"iCON P1-M",behringer_xtouch:"Behringer X-Touch",mackie_control:"Mackie Control"};
function options(values,current,label=x=>x){return values.map(v=>`<option value="${esc(v)}" ${v===current?"selected":""}>${esc(label(v))}</option>`).join("")}
function deviceOptions(values,current){let list=[`<option value="">Auto detect</option>`]; if(current&&!values.includes(current))list.push(`<option selected value="${esc(current)}">${esc(current)} (saved)</option>`); return list.concat(values.map(v=>`<option value="${esc(v)}" ${v===current?"selected":""}>${esc(v)}</option>`)).join("")}
function bind(id,key,type="text"){const el=$(id); el[type==="check"?"checked":"value"]=type==="check"?!!config[key]:config[key]; el.oninput=()=>config[key]=type==="check"?el.checked:type==="number"?Number(el.value):el.value}
function fillGlobal(){[["vmix_host","text"],["vmix_http_port","number"],["vmix_tcp_port","number"],["web_port","number"],["api_port","number"],["channel_count","number"],["poll_interval_ms","number"],["fader_write_interval_ms","number"],["motor_feedback_hold_ms","number"],["send_motor_fader_feedback","check"],["send_scribble_text","check"],["send_scribble_color","check"],["send_meters","check"],["input_faders_touch_sensitive","check"],["start_with_windows","check"],["minimize_to_tray","check"],["log_midi","check"]].forEach(x=>bind(x[0],x[0],x[1])); $("channel_count").onchange=()=>resizeChannels(Number($("channel_count").value))}
function resizeChannels(count){count=Math.max(8,Math.min(64,count||16)); while(config.channels.length<count)config.channels.push({channel:config.channels.length+1,kind:"none",input_key:"",input_number:0,label_override:"",follow_input_name:true,strip_color:"blue"}); config.channels=config.channels.slice(0,count); config.channel_count=count; drawChannels()}
function drawControllerTabs(){const root=$("controller-tabs");root.innerHTML="";config.controllers.forEach((c,i)=>{const b=document.createElement("button");b.className="tab "+(i===selectedController?"active":"");b.textContent=c.name||`Controller ${i+1}`;b.onclick=()=>{selectedController=i;drawControllerTabs();drawControllerEditor();drawChannels()};root.appendChild(b)});drawControllerEditor()}
function drawControllerEditor(){const root=$("controller-editor"),c=config.controllers[selectedController];if(!c){root.innerHTML="";return} const isX=c.type==="behringer_xtouch";root.innerHTML=`<div class="controller-panel"><div class="grid">
<label>Name<input data-c="name" value="${esc(c.name)}"></label><label>Controller Type<select data-c="type">${options(types,c.type,v=>typeLabel[v])}</select></label><label>MIDI Input<select data-c="midi_input">${deviceOptions(devices.inputs,c.midi_input)}</select></label><label>MIDI Output<select data-c="midi_output">${deviceOptions(devices.outputs,c.midi_output)}</select></label><label>Device Hint<input data-c="device_hint" value="${esc(c.device_hint||"")}"></label><label>Bank Start<input data-c="bank_start" type="number" min="0" max="${Math.max(0,config.channel_count-8)}" value="${c.bank_start||0}"></label><label class="check"><input data-c="enabled" type="checkbox" ${c.enabled!==false?"checked":""}>Enabled</label><label>Channel Buttons<select data-c="button_mode"><option value="volume_100_0" ${c.button_mode!=="toggle_mute"?"selected":""}>Record 100 / Mute 0</option><option value="toggle_mute" ${c.button_mode==="toggle_mute"?"selected":""}>Record 100 / Toggle Mute</option></select></label><button class="danger" id="remove-controller">Remove controller</button></div>
<div class="controller-options ${isX?"":"hidden"}"><div class="grid compact"><label>Surface Mode<select data-c="surface_mode"><option value="mcu" ${c.surface_mode!=="ctrl"?"selected":""}>MC USB / MCU</option><option value="ctrl" ${c.surface_mode==="ctrl"?"selected":""}>CTRL USB</option></select></label><label>Color Mode<select data-c="color_mode">${options(["mcu-72","behringer-72","behringer-4c","all","off"],c.color_mode)}</select></label><label>Meter Mode<select data-c="meter_mode">${options(["mcu-packed-aftertouch","per-channel-aftertouch","off"],c.meter_mode)}</select></label><label>Meter Gain dB<input data-c="meter_gain_db" type="number" value="${c.meter_gain_db??12}"></label><label class="check"><input data-c="master_fader_enabled" type="checkbox" ${c.master_fader_enabled?"checked":""}>Enable master fader</label></div></div></div>`;
root.querySelectorAll("[data-c]").forEach(el=>el.oninput=()=>{let key=el.dataset.c;c[key]=el.type==="checkbox"?el.checked:el.type==="number"?Number(el.value):el.value;if(key==="name"||key==="type"){drawControllerTabs()}});$("remove-controller").onclick=()=>{if(config.controllers.length>1){config.controllers.splice(selectedController,1);selectedController=Math.max(0,selectedController-1);drawControllerTabs();drawChannels()}}}
function inputOptions(current){let out=[`<option value="">None / input number</option>`];if(current&&!vmix.inputs.some(x=>x.key===current))out.push(`<option selected value="${esc(current)}">Missing input (${esc(current)})</option>`);return out.concat(vmix.inputs.map(x=>`<option value="${esc(x.key)}" ${x.key===current?"selected":""}>${x.number}: ${esc(x.title)}</option>`)).join("")}
function liveFor(ch){if(ch.kind==="input"){const x=vmix.inputs.find(i=>i.key===ch.input_key||(!ch.input_key&&i.number===ch.input_number));return x?{volume:x.volume,meter:x.meter}:{}}const x=vmix.outputs?.[ch.kind.toLowerCase()]||{};return x}
function drawChannels(){const root=$("channels"),bank=config.controllers[selectedController]?.bank_start||0;root.innerHTML="";config.channels.forEach((ch,i)=>{const live=liveFor(ch),tr=document.createElement("tr");if(i>=bank&&i<bank+8)tr.className="in-bank";tr.innerHTML=`<td class="channel-number">${i+1}</td><td><select data-f="kind">${options(kinds,ch.kind)}</select></td><td><select data-f="input_key">${inputOptions(ch.input_key)}</select></td><td><input data-f="input_number" type="number" min="0" value="${ch.input_number||0}"></td><td><input data-f="label_override" value="${esc(ch.label_override||"")}"></td><td><input data-f="follow_input_name" type="checkbox" ${ch.follow_input_name!==false?"checked":""}></td><td><span class="swatch" style="background:${ch.strip_color}"></span><select data-f="strip_color">${options(colors,ch.strip_color)}</select></td><td class="live">${live.volume==null?"-":Number(live.volume).toFixed(1)}</td><td class="live">${live.meter==null?"-":Number(live.meter).toFixed(2)}</td>`;tr.querySelectorAll("[data-f]").forEach(el=>el.oninput=()=>{const f=el.dataset.f;ch[f]=el.type==="checkbox"?el.checked:el.type==="number"?Number(el.value):el.value;if(f==="strip_color")drawChannels()});root.appendChild(tr)});$("input-count").textContent=`${config.channel_count} logical channels · ${vmix.inputs.length} vMix inputs · bank ${bank+1}-${Math.min(bank+8,config.channel_count)}`}
function fillMaster(){const m=config.master_fader;m.kind=m.kind||"master";$("master_kind").innerHTML=options(kinds,m.kind);$("master_input_key").innerHTML=inputOptions(m.input_key);$("master_label").value=m.label_override||"Master";$("master_color").innerHTML=options(colors,m.strip_color||"white");[["master_kind","kind"],["master_input_key","input_key"],["master_label","label_override"],["master_color","strip_color"]].forEach(([id,key])=>$(id).oninput=()=>m[key]=$(id).value)}
async function load(){[config,devices,vmix]=await Promise.all([fetch("/api/config").then(r=>r.json()),fetch("/api/midi/devices").then(r=>r.json()),fetch("/api/vmix/state?refresh=1").then(r=>r.json())]);fillGlobal();drawControllerTabs();drawChannels();fillMaster();refreshStatus()}
async function save(){const response=await fetch("/api/config",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(config)});const data=await response.json();if(!response.ok)throw Error(data.error||"Save failed");config=data;drawControllerTabs();drawChannels();fillMaster();$("footer").textContent="Settings saved"}
async function refreshStatus(){try{statusData=await fetch("/api/status").then(r=>r.json());const v=statusData.vmix||{};let items=[{name:"vMix",value:v.status||"Not connected"},{name:"Controllers",value:`${statusData.controllers.filter(x=>x.input_open&&x.output_open).length}/${statusData.controllers.length} open`}];statusData.controllers.forEach(c=>items.push({name:c.name,value:c.error||`${c.midi_input||"No input"} / ${c.midi_output||"No output"} · Bank ${c.bank_start+1}-${c.bank_start+8}`}));$("status").innerHTML=items.map(x=>`<div class="status-item"><strong>${esc(x.name)}</strong><span class="${x.value.includes("not found")?"error":""}">${esc(x.value)}</span></div>`).join("");$("footer").textContent=statusData.last_error||v.status||"Running"}catch(e){$("footer").textContent=e.message}}
$("add-controller").onclick=()=>{config.controllers.push({...%DEFAULT_CONTROLLER%,id:`controller-${Date.now()}`,name:`Controller ${config.controllers.length+1}`});selectedController=config.controllers.length-1;drawControllerTabs();drawChannels()};
$("save").onclick=async()=>{try{await save();await fetch("/api/surface/refresh",{method:"POST"})}catch(e){$("footer").textContent=e.message}};
$("refresh-vmix").onclick=async()=>{await save();vmix=await fetch("/api/vmix/state?refresh=1").then(r=>r.json());drawChannels();refreshStatus()};
$("refresh-surface").onclick=async()=>{await save();await fetch("/api/surface/refresh",{method:"POST"})};
$("test-controller").onclick=async()=>{await save();const id=config.controllers[selectedController]?.id;await fetch("/api/controllers/test",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({id})})};
async function bank(delta){await save();const id=config.controllers[selectedController]?.id;await fetch("/api/controllers/bank",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({id,delta})});config=await fetch("/api/config").then(r=>r.json());drawControllerTabs();drawChannels()}
$("bank-left").onclick=()=>bank(-8);$("bank-right").onclick=()=>bank(8);load();setInterval(refreshStatus,1500);setInterval(async()=>{vmix=await fetch("/api/vmix/state").then(r=>r.json());drawChannels()},3000);
</script></body></html>"""
HTML = HTML.replace("%KINDS%", json.dumps(ASSIGNMENT_KINDS)).replace("%COLORS%", json.dumps(STRIP_COLORS)).replace("%TYPES%", json.dumps(CONTROLLER_TYPES)).replace("%DEFAULT_CONTROLLER%", json.dumps(default_controller(1, "behringer_xtouch")))


def sync_windows_startup(enabled):
    if os.name != "nt":
        return
    command = ["schtasks", "/Change", "/TN", "vMix MIDI Surface Bridge", "/ENABLE" if enabled else "/DISABLE"]
    try:
        subprocess.run(command, check=False, capture_output=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except OSError:
        logging.exception("Could not update Windows startup task")


def make_handler(store, bridge, serve_ui=True):
    class Handler(BaseHTTPRequestHandler):
        def send_json(self, payload, status=200):
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(body)

        def read_json(self):
            length = int(self.headers.get("Content-Length", 0))
            return json.loads(self.rfile.read(length).decode("utf-8")) if length else {}

        def do_GET(self):
            parsed = urlparse(self.path)
            path = parsed.path.rstrip("/") or "/"
            if path == "/" and serve_ui:
                body = HTML.encode("utf-8")
                self.send_response(200); self.send_header("Content-Type", "text/html; charset=utf-8"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            elif path in ("/api/health", "/health"):
                self.send_json({"ok": True, "app": APP_NAME})
            elif path == "/api/config":
                self.send_json(store.get())
            elif path == "/api/status":
                self.send_json(bridge.get_status())
            elif path == "/api/midi/devices":
                self.send_json(bridge.midi_devices())
            elif path == "/api/vmix/state":
                self.send_json(bridge.vmix.poll(store.get()) if parse_qs(parsed.query).get("refresh") else bridge.vmix.get_state())
            elif path == "/api/assignments":
                self.send_json(bridge.assignments_snapshot())
            elif path == "/api/inputs":
                self.send_json(bridge.vmix.get_state().get("inputs", []))
            else:
                self.send_json({"error": "Not found"}, 404)

        def do_POST(self):
            path = urlparse(self.path).path.rstrip("/")
            try:
                if path == "/api/config":
                    saved = store.save(self.read_json()); sync_windows_startup(saved.get("start_with_windows", True)); bridge.reload(); self.send_json(saved)
                elif path == "/api/surface/refresh":
                    bridge.refresh_surface(force=True); self.send_json({"ok": True})
                elif path == "/api/controllers/test":
                    data = self.read_json(); self.send_json({"ok": bridge.test_controller(data.get("id", ""))})
                elif path == "/api/controllers/bank":
                    data = self.read_json(); bridge.change_bank(data.get("id", ""), data.get("delta", 0)); self.send_json({"ok": True})
                elif path.startswith("/api/channels/"):
                    number = int(path.rsplit("/", 1)[-1]); self.send_json({"success": True, "channel": bridge.update_channel(number, self.read_json())})
                else:
                    self.send_json({"error": "Not found"}, 404)
            except Exception as exc:
                logging.exception("Request failed")
                self.send_json({"error": str(exc)}, 400)

        do_PUT = do_POST

        def do_OPTIONS(self):
            self.send_response(204); self.send_header("Access-Control-Allow-Origin", "*"); self.send_header("Access-Control-Allow-Methods", "GET,POST,PUT,OPTIONS"); self.send_header("Access-Control-Allow-Headers", "Content-Type"); self.end_headers()

        def log_message(self, fmt, *args):
            logging.debug("HTTP %s", fmt % args)
    return Handler


def configure_logging():
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=[logging.FileHandler(LOG_DIR / "bridge.log", encoding="utf-8"), logging.StreamHandler()])


def main():
    configure_logging()
    store = ConfigStore(CONFIG_PATH, LEGACY_ICON_PROFILE)
    config = store.get()
    bridge = Bridge(store)
    bridge.start()
    web = ThreadingHTTPServer((config["web_host"], config["web_port"]), make_handler(store, bridge, True))
    api = None
    if config["api_port"] != config["web_port"]:
        try:
            api = ThreadingHTTPServer(("127.0.0.1", config["api_port"]), make_handler(store, bridge, False))
            threading.Thread(target=api.serve_forever, daemon=True).start()
        except OSError as exc:
            logging.warning("Assignment API port %s unavailable: %s", config["api_port"], exc)
    logging.info("%s listening on http://%s:%s/", APP_NAME, config["web_host"], config["web_port"])
    try:
        web.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        bridge.stop(); web.server_close()
        if api: api.shutdown(); api.server_close()


if __name__ == "__main__":
    main()
