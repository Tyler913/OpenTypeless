#!/usr/bin/env python3
"""A local, OpenRouter-shaped speech-to-text and clean-up server for end-to-end runs of the packaged apps.

The synthetic recordings (see dictation_audio.py) "say" one word per tone burst, each word its own pitch, so the
server can transcribe any chunk the app sends by measuring its bursts: what comes back depends only on the audio that
arrived, like a real model. A chunk cut through a word, a chunk sent twice or a chunk lost shows up in the transcript.

It also behaves like a real provider in the ways that have broken the apps before: the first transcription request
fails with 503 (the apps must retry), and the clean-up streams as server-sent events over chunked transfer encoding in
a few bytes at a time, so multi-byte characters arrive split across reads.
"""
import base64
import io
import json
import math
import struct
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from dictation_audio import FREQUENCIES, SAMPLE_RATE, WORDS

API_KEY = "ci-test-key"
POLISH_PREFIX = "【整理】"
POLISH_SUFFIX = "。"
# Bytes per write of the streamed clean-up: small and odd, so UTF-8 characters (3 bytes for CJK) straddle writes.
STREAM_PIECE = 5


def polished(transcript):
    return POLISH_PREFIX + transcript + POLISH_SUFFIX


def decode_wav(data):
    with wave.open(io.BytesIO(data)) as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, SAMPLE_RATE):
            raise ValueError(f"expected 16 kHz mono 16-bit, got {wav.getnchannels()} ch, "
                             f"{8 * wav.getsampwidth()} bit, {wav.getframerate()} Hz")
        frames = wav.readframes(wav.getnframes())
    return struct.unpack(f"<{len(frames) // 2}h", frames)


def transcribe(samples):
    """The words in `samples`: one per tone burst, by pitch. Unknown pitches come back as "?"."""
    frame = SAMPLE_RATE // 100  # 10 ms
    loud = []
    for start in range(0, len(samples) - frame + 1, frame):
        window = samples[start:start + frame]
        loud.append(math.sqrt(sum(s * s for s in window) / frame) > 0.05 * 32768)
    bursts, start = [], None
    for index, is_loud in enumerate(loud + [False]):
        if is_loud and start is None:
            start = index
        elif not is_loud and start is not None:
            bursts.append((start * frame, index * frame))
            start = None
    words = []
    for begin, end in bursts:
        if end - begin < 0.15 * SAMPLE_RATE:
            words.append("?")  # a sliver of a word: the chunk was cut through it
            continue
        # Count zero crossings away from the fades at either end.
        middle = samples[begin + frame * 3:end - frame * 3]
        crossings = sum(1 for a, b in zip(middle, middle[1:]) if (a < 0) != (b < 0))
        pitch = crossings * SAMPLE_RATE / (2 * len(middle))
        nearest = min(range(len(FREQUENCIES)), key=lambda i: abs(FREQUENCIES[i] - pitch))
        words.append(WORDS[nearest] if abs(FREQUENCIES[nearest] - pitch) < 15 else "?")
    return " ".join(words)


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, fail_first_transcription=True):
        super().__init__(("127.0.0.1", 0), Handler)
        self.lock = threading.Lock()
        self.log = []
        self.fail_next_transcription = fail_first_transcription

    @property
    def base_url(self):
        return f"http://127.0.0.1:{self.server_address[1]}/api/v1"

    def record(self, entry):
        with self.lock:
            self.log.append(entry)

    def take_failure(self):
        with self.lock:
            fail, self.fail_next_transcription = self.fail_next_transcription, False
            return fail

    def __enter__(self):
        threading.Thread(target=self.serve_forever, daemon=True).start()
        return self

    def __exit__(self, *_):
        self.shutdown()
        self.server_close()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server: Server

    def log_message(self, *_):
        pass

    def send_json(self, status, body):
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def authorized(self, entry):
        entry["authorized"] = self.headers.get("Authorization") == f"Bearer {API_KEY}"
        if not entry["authorized"]:
            self.send_json(401, {"error": {"message": "No auth credentials found", "code": 401}})
        return entry["authorized"]

    def do_GET(self):
        entry = {"method": "GET", "path": self.path}
        self.server.record(entry)
        if self.path.rstrip("/").endswith("/models"):
            self.send_json(200, {"data": []})
        else:
            self.send_json(404, {"error": {"message": "not found"}})

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        entry = {"method": "POST", "path": self.path, "time": time.monotonic()}
        self.server.record(entry)
        if not self.authorized(entry):
            return
        try:
            request = json.loads(body)
        except ValueError:
            entry["error"] = "body is not JSON"
            self.send_json(400, {"error": {"message": entry["error"]}})
            return
        if self.path.endswith("/audio/transcriptions"):
            self.transcription(request, entry)
        elif self.path.endswith("/chat/completions"):
            self.completion(request, entry)
        else:
            self.send_json(404, {"error": {"message": "not found"}})

    def transcription(self, request, entry):
        try:
            if request.get("input_audio", {}).get("format") != "wav":
                raise ValueError("input_audio.format is not wav")
            samples = decode_wav(base64.b64decode(request["input_audio"]["data"], validate=True))
        except (KeyError, TypeError, ValueError, wave.Error, struct.error) as error:
            entry["error"] = f"bad audio: {error}"
            self.send_json(400, {"error": {"message": entry["error"]}})
            return
        entry["seconds"] = len(samples) / SAMPLE_RATE
        entry["model"] = request.get("model")
        if self.server.take_failure():
            entry["status"] = 503
            self.send_json(503, {"error": {"message": "Provider returned error", "code": 503}})
            return
        entry["text"] = transcribe(samples)
        entry["status"] = 200
        self.send_json(200, {"text": entry["text"], "usage": {"seconds": entry["seconds"], "cost": 0.0001}})

    def completion(self, request, entry):
        user = next((m["content"] for m in reversed(request.get("messages", [])) if m.get("role") == "user"), "")
        start, end = user.find("<transcript>\n"), user.find("\n</transcript>")
        if not request.get("stream") or start < 0 or end < 0:
            entry["error"] = "expected a streamed request with the transcript in <transcript> tags"
            self.send_json(400, {"error": {"message": entry["error"]}})
            return
        entry["transcript"] = user[start + len("<transcript>\n"):end]
        entry["model"] = request.get("model")
        entry["status"] = 200
        text = polished(entry["transcript"])
        events = [{"choices": [{"delta": {"content": text[i:i + 6]}}]} for i in range(0, len(text), 6)]
        events.append({"choices": [{"delta": {}, "finish_reason": "stop"}],
                       "usage": {"prompt_tokens": 400, "completion_tokens": 60, "cost": 0.0002}})
        stream = b"".join(f"data: {json.dumps(e, ensure_ascii=False)}\n\n".encode() for e in events) + b"data: [DONE]\n\n"
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        for i in range(0, len(stream), STREAM_PIECE):
            piece = stream[i:i + STREAM_PIECE]
            self.wfile.write(f"{len(piece):x}\r\n".encode() + piece + b"\r\n")
            self.wfile.flush()
            time.sleep(0.002)
        self.wfile.write(b"0\r\n\r\n")
