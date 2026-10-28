"""
Moog Theremini → OSC bridge.

Reads CC2 (volume, left antenna) and CC20 (pitch, right antenna) from the
first USB MIDI input matching THEREMIN_HINT, smooths + dead-zones the values,
throttles to ~60 Hz, and sends them to Unity at OSC_HOST:OSC_PORT.

    /theremin/pitch   float 0..1   (CC20)
    /theremin/volume  float 0..1   (CC2)

Usage:
    pip install -r requirements.txt
    python theremini_bridge.py
    python theremini_bridge.py --list           # show all MIDI inputs
    python theremini_bridge.py --port "Theremini 0"
"""
from __future__ import annotations

import argparse
import sys
import time

import mido
from pythonosc.udp_client import SimpleUDPClient

# ── defaults ────────────────────────────────────────────────────────────────
THEREMIN_HINT  = "theremini"   # case-insensitive substring match
PITCH_CC       = 20            # CC20 = pitch / right antenna
VOLUME_CC      = 2             # CC2  = volume / left antenna
OSC_HOST       = "127.0.0.1"
OSC_PORT       = 9000
SEND_HZ        = 60            # throttle outgoing OSC rate
SMOOTH_ALPHA   = 0.30          # EMA weight for new samples (0..1, lower = smoother)
DEADZONE       = 0.015         # raw values below this snap to 0


def list_inputs() -> list[str]:
    return mido.get_input_names()


def find_port(hint: str | None) -> str | None:
    names = list_inputs()
    if hint:
        for n in names:
            if hint.lower() in n.lower():
                return n
        return None
    return names[0] if names else None


def main():
    ap = argparse.ArgumentParser(description="Moog Theremini → OSC bridge")
    ap.add_argument("--list", action="store_true", help="list MIDI inputs and exit")
    ap.add_argument("--port", help="exact MIDI port name (default: first match for 'theremini')")
    ap.add_argument("--osc-host", default=OSC_HOST)
    ap.add_argument("--osc-port", type=int, default=OSC_PORT)
    ap.add_argument("--smooth", type=float, default=SMOOTH_ALPHA, help="EMA alpha (lower = smoother)")
    ap.add_argument("--deadzone", type=float, default=DEADZONE)
    args = ap.parse_args()

    if args.list:
        names = list_inputs()
        if not names:
            print("(no MIDI inputs found — plug in the Theremini and try again)")
        else:
            print("Available MIDI inputs:")
            for n in names: print(f"  {n!r}")
        return

    port_name = args.port or find_port(THEREMIN_HINT)
    if not port_name:
        print(f"Could not find a MIDI port matching '{THEREMIN_HINT}'.")
        print("Available inputs:")
        for n in list_inputs(): print(f"  {n!r}")
        print("\nPass --port \"<exact name>\" or run with --list.")
        sys.exit(1)

    print(f"[Theremini] Opening MIDI: {port_name!r}")
    print(f"[Theremini] OSC → {args.osc_host}:{args.osc_port}")
    osc = SimpleUDPClient(args.osc_host, args.osc_port)

    pitch_smoothed  = 0.0
    volume_smoothed = 0.0
    raw_pitch       = 0.0
    raw_volume      = 0.0
    last_send       = 0.0
    msg_count       = 0
    next_log        = time.time() + 5.0

    try:
        with mido.open_input(port_name) as port:
            while True:
                # Drain available messages without blocking
                for msg in port.iter_pending():
                    if msg.type != "control_change":
                        continue
                    val = msg.value / 127.0
                    if val < args.deadzone:
                        val = 0.0
                    if msg.control == PITCH_CC:
                        raw_pitch = val
                        msg_count += 1
                    elif msg.control == VOLUME_CC:
                        raw_volume = val
                        msg_count += 1

                # EMA smoothing on a fixed clock (independent of MIDI rate)
                pitch_smoothed  += (raw_pitch  - pitch_smoothed)  * args.smooth
                volume_smoothed += (raw_volume - volume_smoothed) * args.smooth

                now = time.time()
                if now - last_send >= 1.0 / SEND_HZ:
                    osc.send_message("/theremin/pitch",  float(pitch_smoothed))
                    osc.send_message("/theremin/volume", float(volume_smoothed))
                    last_send = now

                if now >= next_log:
                    print(f"[Theremini] OSC→ {args.osc_host}:{args.osc_port}  "
                          f"midi_msgs={msg_count}  pitch={pitch_smoothed:.3f}  vol={volume_smoothed:.3f}")
                    next_log = now + 2.0   # log more often so you can see live values

                time.sleep(0.002)
    except KeyboardInterrupt:
        print("\n[Theremini] Stopped.")


if __name__ == "__main__":
    main()
