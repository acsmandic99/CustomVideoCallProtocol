# CustomVideoCallProtocol

A custom low-latency video call protocol inspired by WebRTC, built as a bachelor's thesis at FTN Novom Sad.

## Key Features

* **Binary packet protocol (big-endian)** for signaling (TCP) and media (UDP)
* **Hybrid NACK/PLI recovery:** lost delta frames are retransmitted from a short buffer, lost keyframes trigger generation of a fresh keyframe from the camera (no stale retransmissions, no accumulated latency)
* **JPEG and H.264 video codecs**, switchable per call and compared under simulated packet loss (0–50%)
* **Audio call support** (8 kHz mono PCM, 20 ms packets)
* **UI-independent core** (class libraries) with a WPF demo client driven by a facade (`VideoCallClient`)
* **LAN tested:** two machines, direct peer-to-peer media

## Projects

| Project | Purpose |
| :--- | :--- |
| `VideoCall.Protocol` | Message definitions, binary codec, packet format |
| `VideoCall.Network` | TCP signaling server/client, packet framing |
| `VideoCall.Media` | UDP media transport, fragmentation, NACK/PLI recovery |
| `VideoCall.Codecs` | Camera/microphone capture, JPEG/H.264 codecs, audio |
| `VideoCall.Client` | `VideoCallClient` facade — protocol API independent of UI |
| `VideoCall.Client.Wpf` | WPF testing/analysis client (source/codec/loss controls) |
| `VideoCall.Client.Wpf.Showcase` | Polished WPF showcase client for presentations and screenshots |
| `VideoCall.Benchmark.Core` | Benchmark engine: transports, metrics, result files |
| `VideoCall.Benchmark.Wpf` | Benchmark GUI (test name, duration, transport, video file, loss sweep) |

## Running the WPF demo

### Quick start

Double-click **`run-wpf-demo.bat`** (or run it from a terminal in the repository root). The script builds the solution and opens three windows: the **signaling server** (console) and **two WPF clients**.

A call is established like this:

1. In each client, enter a name and press **Register** (both users must register before calling).
2. In client A, enter the name of client B in the **Call** field and press **Call**.
3. Client B presses **Accept** — only then is the call established and media starts flowing.
4. Press **Hangup** (either side) to end the call.

### Video sources

The **Source** combo box selects what each client sends:

* **Web camera** — live capture from the default camera.
* **Synthetic** — a generated animated pattern (moving gradient and square). Useful for testing and demos when no camera is available, and for visually estimating latency.
* **Video file** — immediately opens a file dialog; pick a video file (`.mp4`, `.avi`, `.mkv`, ...). The file loops for the whole call: video frames are read through OpenCV, while the audio track is extracted with NAudio and sent through the protocol. If the file has no (readable) audio track, the microphone is used as a fallback.

### Other controls

* **Codec** — H.264 (default) or JPEG, chosen per call.
* **Server** — address of the signaling server (defaults to `127.0.0.1`; use the server machine's LAN IP for calls between two machines).
* **Mic** — input device used for the microphone.
* **Simulate outgoing loss** — 0–50% buttons drop that percentage of this client's outgoing packets, live during the call.
* **Mic / Cam toggles** — mute the microphone or turn the camera off during a call (turning the camera back on triggers an immediate keyframe).

### Two machines (LAN)

Run the server on machine A, point the client on machine B at A's LAN IP, and let both firewalls allow the apps on private networks. Media flows directly between the two machines (peer-to-peer); the server only coordinates signaling.

## Showcase client

`VideoCall.Client.Wpf.Showcase` is a presentation-oriented client with the same `VideoCallClient` facade underneath but a polished light UI and **no settings**: H.264, web camera and default microphone are always used, so it is ideal for screenshots in the thesis and live demos. Start it with **`run-wpf-showcase.bat`** (server + two clients).

The flow is page-based: a registration page first, then a home page from which you call by registered name. During a call the remote video fills the stage with the local camera as a small picture-in-picture (click it to swap), and a control bar offers microphone/camera mute and hangup. The gauge button toggles a small stats HUD (fps, datagrams, NACK/PLI counters, endpoints) — useful to show the recovery mechanisms working live.

The regular `VideoCall.Client.Wpf` remains the testing/analysis client: it keeps the source/codec/microphone selectors and the 0–50 % outgoing loss buttons used for measurements. To impair the showcase client (loss, latency, jitter, reordering, throttling) use an external network emulator such as [Clumsy](https://jagt.github.io/clumsy/) (MIT) with a filter like `udp and (DstPort >= 20000 and DstPort <= 25000)` — the range the clients bind their media sockets to.

## Benchmark

`VideoCall.Benchmark.Wpf` measures the media pipeline against a **video file input**, so every run processes an identical frame sequence. Pick a test name, duration, codec and a video file, then choose the transport:

* **Custom UDP** — the full protocol (fragmentation + reorder window + NACK retransmissions + PLI keyframe requests)
* **Raw UDP** — the same fragmentation with all recovery mechanisms disabled (`MediaSession(recoveryEnabled: false)`), showing exactly what NACK/PLI buys
* **TCP** — a `[4-byte length][frame]` loopback baseline (no loss; the measurement of interest is per-frame latency)

Loss is simulated deterministically with a fixed seed on the sender side. The **loss sweep** option runs 11 consecutive tests at 0–10 % loss (step 1, per the measurement methodology) and writes a merged `sweep-summary.csv`.

Each run writes its own folder `BenchmarkResults/<test-name>-<timestamp>/` with:

| File | Contents |
| :--- | :--- |
| `meta.json` | All test parameters (file, codec, transport, loss, seed, resolution, fps) |
| `summary.csv` | Aggregate metrics |
| `timeseries.csv` | Per-second sent/delivered frames, KB/s, NACK/PLI counts (for graphs) |
| `frames.csv` | Per-frame size, type, encode time and latency |

Collected metrics include: sent/delivered/decodable frames (a delivered delta frame is only *decodable* when all frames since the last delivered keyframe arrived — the renderable-video metric), fps, goodput, average frame size, NACK/PLI/retransmission counts, dropped datagrams, per-frame latency (avg, p50, p95, p99, max), longest delivery gap (freeze duration), encode times and out-of-order deliveries.
