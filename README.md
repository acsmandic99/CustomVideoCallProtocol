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
| `VideoCall.Client.Wpf` | WPF demo client + `VideoCallClient` facade |

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
