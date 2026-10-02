#!/usr/bin/env python3
"""A synthetic dictation the fake provider can transcribe: one tone burst per word, each word its own pitch.

Words come in phrases with short gaps inside and longer pauses between them, like speech, so the apps' chunker has
real pauses to cut at. Nothing recorded, nothing private, the same bytes on every run.
"""
import math
import struct
import wave

SAMPLE_RATE = 16_000
# English and Chinese, so the joined transcript exercises both the spaced and the unspaced joins.
WORDS = ("open", "typeless", "keeps", "every", "word", "语音", "输入", "很", "稳定", "long", "dictation", "works",
         "测试", "通过", "today", "again")
FREQUENCIES = tuple(420 + 55 * i for i in range(len(WORDS)))  # 420 … 1245 Hz, far apart enough to tell by ear
WORD_SECONDS = 0.45
GAP_SECONDS = 0.18
PAUSE_SECONDS = 0.9
PHRASE = 5


def script(count):
    """`count` words, cycling through the vocabulary with a stride so neighbours differ."""
    return [WORDS[(i * 7) % len(WORDS)] for i in range(count)]


def samples(words):
    out = [0] * int(0.6 * SAMPLE_RATE)
    fade = int(0.01 * SAMPLE_RATE)
    length = int(WORD_SECONDS * SAMPLE_RATE)
    for index, word in enumerate(words):
        frequency = FREQUENCIES[WORDS.index(word)]
        for n in range(length):
            envelope = min(1.0, n / fade, (length - n) / fade)
            out.append(int(0.35 * 32767 * envelope * math.sin(2 * math.pi * frequency * n / SAMPLE_RATE)))
        last = index == len(words) - 1
        gap = 1.2 if last else PAUSE_SECONDS if (index + 1) % PHRASE == 0 else GAP_SECONDS
        out.extend([0] * int(gap * SAMPLE_RATE))
    return out


def write(path, words):
    data = samples(words)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(struct.pack(f"<{len(data)}h", *data))
    return len(data) / SAMPLE_RATE
