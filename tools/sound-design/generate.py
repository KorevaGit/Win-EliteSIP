"""Original, sample-free ringtone proposals. Requires numpy; writes PCM16 mono WAVs."""

import json
import math
from pathlib import Path
import wave

import numpy as np

RATE = 48000
OUT = Path(__file__).resolve().parents[2] / "artifacts" / "sound-proposals"


def envelope(t, length, decay, attack=0.018, release=0.12):
    rise = np.sin(np.minimum(t / attack, 1) * math.pi / 2) ** 2
    fall = np.sin(np.clip((length - t) / release, 0, 1) * math.pi / 2) ** 2
    return rise * np.exp(-t / decay) * fall


def voice(freq, length, kind):
    t = np.arange(round(length * RATE)) / RATE
    phase = 2 * math.pi * freq * t
    if kind == "glass":
        tone = np.sin(phase + 0.24 * np.exp(-t / 0.18) * np.sin(2.003 * phase))
        tone += 0.09 * np.sin(2.002 * phase) * np.exp(-t / 0.23)
        tone += 0.10 * np.sin(phase / 2)
        return tone * envelope(t, length, 0.43, attack=0.016)
    if kind == "drops":
        tone = np.sin(phase) + 0.13 * np.sin(2.004 * phase) * np.exp(-t / 0.13)
        tone += 0.05 * np.sin(3 * phase) * np.exp(-t / 0.08)
        return tone * envelope(t, length, 0.29, attack=0.012)
    if kind == "keys":
        tone = np.sin(phase + 0.22 * np.sin(phase) * np.exp(-t / 0.22))
        tone += 0.14 * np.sin(phase / 2) + 0.055 * np.sin(3 * phase) * np.exp(-t / 0.15)
        return tone * envelope(t, length, 0.54, attack=0.028)
    tone = np.sin(phase) + 0.17 * np.sin(phase / 2)
    tone += 0.045 * np.sin(2.001 * phase) * np.exp(-t / 0.40)
    return tone * envelope(t, length, 0.66, attack=0.045, release=0.20)


def place(track, onset, note, gain=1):
    start = round(onset * RATE)
    end = min(start + len(note), len(track))
    track[start:end] += note[: end - start] * gain


def room(track):
    result = track.copy()
    for delay, gain in ((0.071, 0.09), (0.137, 0.055), (0.229, 0.025)):
        n = round(delay * RATE)
        result[n:] += track[:-n] * gain
    return result


def write(name, track):
    assert np.isfinite(track).all()
    assert np.max(np.abs(track)) < 0.99
    path = OUT / name
    pcm = np.rint(track * 32767).astype("<i2")
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(pcm.tobytes())
    return {
        "file": name,
        "seconds": round(len(track) / RATE, 3),
        "peak_dbfs": round(20 * np.log10(np.max(np.abs(track))), 2),
        "rms_dbfs": round(20 * np.log10(np.sqrt(np.mean(track**2))), 2),
    }


def ringtone(kind, motif, peak=0.18):
    cycle = np.zeros(4 * RATE)
    for start, freq, length, gain in motif:
        place(cycle, start + 0.08, voice(freq, length, kind), gain)
    cycle = room(cycle)
    fade = np.sin(np.linspace(0, math.pi / 2, 960)) ** 2
    cycle[:960] *= fade
    cycle[-960:] *= fade[::-1]
    gain = min(peak / np.max(np.abs(cycle)), 10 ** (-31 / 20) / np.sqrt(np.mean(cycle**2)))
    cycle *= gain
    return np.tile(cycle, 3)


def connection():
    # Same formula and level as SignalSoundPlayer.ConnectedToneProvider.
    t = np.arange(round(0.34 * RATE)) / RATE
    result = np.zeros_like(t)
    for onset, freq, length, gain in ((0, 523.25, 0.19, 0.90), (0.11, 659.25, 0.23, 0.75)):
        mask = (t >= onset) & (t < onset + length)
        local = t[mask] - onset
        attack = np.sin(np.minimum(local / 0.012, 1) * math.pi / 2) ** 2
        release = np.sin(np.minimum((length - local) / 0.060, 1) * math.pi / 2) ** 2
        phase = 2 * math.pi * freq * local
        result[mask] += gain * attack * release * np.exp(-7 * local) * (
            np.sin(phase) + 0.08 * np.sin(2 * phase)
        ) / 1.08
    return result * 0.032


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    proposals = [
        ("01-glass.wav", "glass", [(0, 659.25, 1.12, 1), (0.24, 783.99, 1.08, 0.80), (0.58, 1174.66, 1.30, 0.60)]),
        ("02-drops.wav", "drops", [(0, 587.33, 0.85, 1), (0.19, 739.99, 0.90, 0.88), (0.73, 659.25, 0.85, 0.86), (0.96, 880, 1.05, 0.68)]),
        ("03-warm-keys.wav", "keys", [(0, 440, 1.45, 1), (0.28, 659.25, 1.25, 0.70), (0.62, 493.88, 1.20, 0.83), (0.94, 739.99, 1.30, 0.54)]),
        ("04-air.wav", "air", [(0, 622.25, 1.65, 1), (0.42, 932.33, 1.55, 0.65), (0.87, 830.61, 1.70, 0.64)]),
    ]
    report = [write(name, ringtone(kind, motif)) for name, kind, motif in proposals]
    cue = connection()
    report.append(write("connected.wav", cue))
    preview = np.zeros(round(4.2 * RATE))
    for start in (0.15, 1.55, 2.95):
        place(preview, start, cue)
    report.append(write("connected-preview.wav", preview))
    (OUT / "levels.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
