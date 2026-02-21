# Copilot Beacon

## The Big Picture

GitHub Copilot in VS Code sometimes needs your attention — it finishes generating a response, asks you to approve an edit, or waits for confirmation before running a command. Today the only way to know is to stare at the VS Code window.

**Copilot Beacon** turns those invisible state changes into a physical light on your desk. When Copilot needs you, the beacon pulses. When you've handled it, the beacon goes idle. The goal is a zero-friction ambient indicator so you can context-switch away from VS Code and still know exactly when Copilot needs you back.

The system has three pieces:

| Component | Repo | Role |
|-----------|------|------|
| **copilot-beacon-core** | this repo | Windows service that detects Copilot state and emits SSE events |
| **arduino-bridge** | separate repo | .NET console app that consumes SSE and writes serial commands to an Arduino |
| **arduino firmware** | separate repo | Microcontroller sketch that drives an LED/NeoPixel based on serial commands |

## This Repo — copilot-beacon-core

This is the **detection and event server**. It runs as a local HTTP server on Windows, watches VS Code for Copilot activity through multiple detection strategies, and broadcasts normalized state events over Server-Sent Events (SSE).

### What It Detects

| Detector | How It Works |
|----------|-------------|
| **CopilotPaneDetector** | Scans the VS Code UI Automation tree for known CSS class names (`chat-confirmation-widget-container`, `chat-response-loading`) to detect confirmation dialogs and loading states |
| **ToastDetector** | Listens for Windows toast notifications from VS Code and matches keywords like "allow edits", "new chat response", etc. |
| **ForegroundDetector** | Monitors Win32 foreground window changes — when VS Code comes to foreground, emits a Clear event (user is looking at it) |
| **AfkDetector** | Tracks user input idle time via `GetLastInputInfo` — when the user returns from AFK while VS Code is foreground, emits a Clear event |
| **FakeEventEmitter** | Test mode — cycles through Waiting → Done → Clear every few seconds without needing a real Copilot session |

### Events

Three event types flow through the system:

| Event | Meaning | Beacon Behavior |
|-------|---------|-----------------|
| `Waiting` | Copilot needs user action (confirmation dialog, approval prompt) | Pulse amber |
| `Done` | Copilot finished generating a response | Flash green |
| `Clear` | User is engaged with VS Code — no beacon needed | Off / idle |

Events are published on an internal `EventBus` (backed by `System.Threading.Channels`) and streamed to all connected SSE clients.

### HTTP Endpoints

All on `http://127.0.0.1:17321` by default.

| Endpoint | Description |
|----------|-------------|
| `GET /events` | SSE stream of real-time events |
| `GET /health` | `{ "ok": true, "version": "0.1.0" }` |
| `GET /state` | `{ "active": bool, "mode": "idle" \| "waiting" \| "done" }` |

### SSE Wire Format

```
event: Waiting
data: {"event":"Waiting","source":"Pane","timestamp":"2026-02-21T15:30:00Z","reason":"Confirmation dialog visible in Copilot chat pane"}

```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 9, ASP.NET Core Minimal APIs |
| Target | `net9.0-windows10.0.22621.0` (Windows 11 SDK) |
| UI Automation | `System.Windows.Automation` (WPF reference via `UseWPF`) |
| Toast Notifications | WinRT `UserNotificationListener` |
| Win32 Interop | P/Invoke via `LibraryImport` — `SetWinEventHook`, `GetLastInputInfo`, `GetForegroundWindow`, message pump |
| Event Bus | `System.Threading.Channels` (unbounded, multi-subscriber) |
| SSE | Raw `text/event-stream` over ASP.NET Core `HttpResponse` |
| Config | JSON file (`core.config.json`) + environment variables |
| Formatting | CSharpier |

## Project Structure

```
├── docs/
│   └── arduino-receiver-context.md    # Context doc for the Arduino bridge agent
├── scripts/
│   ├── discover-automation-classes.cmd # Launcher (ensures pwsh 7+)
│   └── discover-automation-classes-impl.ps1  # Scans UI Automation tree for Copilot class names
├── src/
│   ├── copilot-beacon-core/
│   │   ├── Config/
│   │   │   └── CoreConfig.cs          # All config models (port, keywords, pane detector, etc.)
│   │   ├── Events/
│   │   │   ├── CopilotEvent.cs        # Event model — BeaconEventType, BeaconEventSource, CopilotEvent
│   │   │   └── EventBus.cs            # Channel-based pub/sub with state gating
│   │   ├── Native/
│   │   │   └── Win32.cs               # P/Invoke declarations
│   │   ├── Server/
│   │   │   ├── Endpoints.cs           # Route registration
│   │   │   └── SseHandler.cs          # SSE streaming logic
│   │   ├── Services/
│   │   │   ├── AfkDetector.cs         # AFK detection via GetLastInputInfo
│   │   │   ├── CopilotPaneDetector.cs # UI Automation tree scanner
│   │   │   ├── FakeEventEmitter.cs    # Synthetic event cycle for testing
│   │   │   ├── ForegroundDetector.cs  # Win32 foreground window hook
│   │   │   └── ToastDetector.cs       # WinRT toast notification listener
│   │   ├── Program.cs                 # Entry point, DI wiring
│   │   └── core.config.json           # Runtime configuration
│   └── copilot-beacon-test-client/    # Simple console SSE consumer for testing
└── VsCodeCopilotStatusHooks.sln
```

## Running

```powershell
cd src/copilot-beacon-core
dotnet run
```

### Fake Mode (no real Copilot needed)

```powershell
$env:BEACON_FAKE_MODE = "1"
dotnet run
```

### Test Client

```powershell
cd src/copilot-beacon-test-client
dotnet run
```

## Configuration

All settings live in `core.config.json` under the `Core` key:

| Setting | Default | Purpose |
|---------|---------|---------|
| `Port` | `17321` | HTTP listen port |
| `VscodeProcessName` | `Code` | Process name to watch |
| `AfkThresholdSeconds` | `30` | Seconds of idle before considering user AFK |
| `PollIntervalMs` | `250` | UI Automation scan interval |
| `FakeMode` | `false` | Enable synthetic event cycling |
| `Keywords` | — | Toast notification keyword matching lists |
| `PaneDetector` | — | UI Automation class names and window filters |

## Discovery Script

When Copilot updates change the internal class names used for detection, run:

```powershell
scripts\discover-automation-classes.cmd
```

This scans the VS Code UI Automation tree for the current class names and includes a guided discovery mode that walks you through triggering each Copilot state to identify the new patterns.
