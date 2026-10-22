"""Telephone-style alternatives: repeated ringing cadences rather than melodies."""

import json
import math

import numpy as np

import generate as design

RATE = design.RATE
design.OUT = design.OUT / "telephone-v2"


def edges(t, length, attack=0.014, release=0.040):
    return np.sin(np.minimum(t / attack, 1) * math.pi / 2) ** 2 * np.sin(
        np.clip((length - t) / release, 0, 1) * math.pi / 2
    ) ** 2


def bell_burst():
    track = np.zeros(round(0.72 * RATE))
    for index, start in enumerate(np.arange(0, 0.49, 0.049)):
        frequency = 910 if index % 2 == 0 else 1190
        t = np.arange(round(0.22 * RATE)) / RATE
        phase = 2 * math.pi * frequency * t
        tone = np.sin(phase) + 0.18 * np.sin(1.505 * phase) + 0.035 * np.sin(2.76 * phase)
        tone *= (1 - np.exp(-t / 0.0035)) * np.exp(-t / 0.047)
        tone *= edges(t, 0.22, attack=0.004, release=0.05)
        design.place(track, start, tone)
    return track


def office_burst(length=0.46):
    t = np.arange(round(length * RATE)) / RATE
    blend = 0.5 + 0.5 * np.sin(2 * math.pi * 16 * t)
    tone = blend * np.sin(2 * math.pi * 740 * t)
    tone += (1 - blend) * np.sin(2 * math.pi * 990 * t)
    tone += 0.055 * np.sin(2 * math.pi * 1480 * t)
    return tone * edges(t, length)


def mobile_burst(length=0.66):
    t = np.arange(round(length * RATE)) / RATE
    # A continuous, rounded brr-ring with shallow pitch modulation and no tune.
    phase = 2 * math.pi * 830 * t + 0.18 * np.sin(2 * math.pi * 23 * t)
    tone = np.sin(phase) + 0.14 * np.sin(2 * math.pi * 1106.67 * t)
    tone += 0.04 * np.sin(2 * phase)
    flutter = 0.68 + 0.32 * np.cos(2 * math.pi * 25 * t)
    return tone * flutter * edges(t, length, attack=0.026, release=0.065)


def loop(burst, second, level=-28.5):
    cycle = np.zeros(4 * RATE)
    design.place(cycle, 0.08, burst)
    design.place(cycle, second + 0.08, burst, gain=0.94)
    # Small room, kept below the main ringing so it does not turn into a wash.
    cycle = design.room(cycle)
    gain = min(0.21 / np.max(np.abs(cycle)), 10 ** (level / 20) / np.sqrt(np.mean(cycle**2)))
    cycle *= gain
    assert np.max(np.abs(cycle[:200])) < 1e-10
    assert np.max(np.abs(cycle[-200:])) < 1e-10
    return np.tile(cycle, 3)


def main():
    design.OUT.mkdir(parents=True, exist_ok=True)
    tracks = [
        ("01-soft-double-bell.wav", loop(bell_burst(), 0.92)),
        ("02-office-phone.wav", loop(office_burst(), 0.69)),
        ("03-rounded-mobile.wav", loop(mobile_burst(), 0.91)),
    ]
    report = [design.write(name, track) for name, track in tracks]
    (design.OUT / "levels.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
