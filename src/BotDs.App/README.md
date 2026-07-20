# BotDs.App: Localhost Dashboard Host

ASP.NET Core 10 web application serving a static dashboard, a REST/SSE API, and the bot controller on loopback only. This is a personal local tool; it does not collect credentials, chat content, or sensitive identifiers, and it does not expose control endpoints beyond loopback.

## Run

```bash
dotnet run --project src/BotDs.App
```

Default development URL: `http://localhost:5068` (HTTPS profile on `https://localhost:7254` is available via launch profiles). The `AllowedHosts` setting restricts the host header to `localhost`.

## Configuration

All configuration lives in `appsettings.json` (override with environment variables or user-secrets):

```
BotDs:Dashboard:ApiToken       - Bearer token for read-only API access
BotDs:Dashboard:ControlToken   - Bearer token for control operations (arm, disarm, estop, profile reload, profile set)
BotDs:Evaluator:MaximumTelemetryAgeMs - Maximum age of the latest telemetry frame before it is considered stale (default 5000)
BotDs:Evaluator:EvaluationIntervalMs  - Polling interval for the evaluator loop (default 100)
BotDs:Profiles:Directory       - Relative or absolute path to JSON combat profiles (default ../../profiles)
```

### Token configuration and fail-closed warnings

On startup the host checks both tokens:

- If `BotDs:Dashboard:ApiToken` is empty or missing, a warning is logged and that read credential is disabled. A configured control token can still authorize read endpoints.
- If `BotDs:Dashboard:ControlToken` is empty or missing, a warning is logged and **control operations are disabled** (401 for `/api/control/*` and `POST /api/profiles/reload`).

Both tokens must be non-empty strings for their respective access levels to function. The middleware always rejects requests when the configured token is empty; there is no unauthenticated fallback.

### Loopback restriction

`DashboardSecurityMiddleware` enforces the following before token checks:

1. Remote IP must be a loopback address (`127.0.0.1`, `::1`, or `::ffff:127.0.0.1`).
2. `Host` header must be `localhost`, `127.0.0.1`, or `::1`.
3. `Origin` header, if present, must match the request's own scheme, host, and port.

Non-loopback or cross-origin requests receive 403.

## Profile directory

Profiles are JSON files in the configured `BotDs:Profiles:Directory` (default: `../../profiles` relative to the content root, which resolves to the repository-root `profiles/` directory). The directory is created on startup if absent.

- `POST /api/profiles/reload` triggers an atomic reload of all `*.json` files.
- Duplicate profile IDs, invalid JSON, and validation failures abort the reload and preserve the previous cache.
- Active profile selection is cleared if the active profile is no longer present after a successful reload.
- The controller must be disarmed before a reload or profile change (configuration lease).

## Evaluator loop

`EvaluatorLoop` is a `BackgroundService` that polls at `BotDs:Evaluator:EvaluationIntervalMs` (default 100 ms):

- Skips evaluation when the controller is disarmed, stopped, or faulted.
- Triggers emergency stop if no active profile is set while armed.
- Validates telemetry freshness against `BotDs:Evaluator:MaximumTelemetryAgeMs`.
- Uses a generation counter to reject stale evaluations produced before disarm/profile-change/re-arm boundaries.

## Dashboard controls

The dashboard UI and API expose the following controls:

| Control | Endpoint | Requires | Behaviour |
|---|---|---|---|
| Arm | `POST /api/control/arm` | Control token | Transitions to `WaitingForPlayer`; requires an enabled active profile, fresh telemetry, a live player, and a live hostile target. |
| Disarm | `POST /api/control/disarm` | Control token | Returns an active controller to `Disarmed`; it cannot clear a latched emergency stop. |
| Emergency Stop | `POST /api/control/emergency-stop` | Control token | Transitions to `Stopped`; cannot be cleared by disarm. |
| Clear Stop | `POST /api/control/clear-stop` | Control token | Transitions from `Stopped` back to `Disarmed`. |

The `Clear Stop` control exists because emergency stop latches. Once activated, the controller remains in `Stopped` until explicitly cleared. Disarm is rejected in the stopped/faulted state.

## API endpoints

All endpoints require loopback access and valid tokens (401 without tokens, 403 from non-loopback).

| Method | Path | Token | Description |
|---|---|---|---|
| GET | `/api/status` | API or Control | Current provider, controller, player, target, active profile, and last evaluation. |
| GET | `/api/profiles` | API or Control | List available profiles and the active profile ID. |
| POST | `/api/profiles/reload` | Control | Reload profiles from disk. |
| POST | `/api/control/profile` | Control | Set the active profile (`{ "profileId": "..." }`). |
| POST | `/api/control/arm` | Control | Arm the controller. |
| POST | `/api/control/disarm` | Control | Disarm the controller. |
| POST | `/api/control/emergency-stop` | Control | Emergency stop. |
| POST | `/api/control/clear-stop` | Control | Clear a latched stop. |
| GET | `/api/events` | API or Control | Server-Sent Events stream of status changes (SSE, `text/event-stream`). |

## Logging and audit

- Structured logs are written to the console and to rolling NDJSON files under `logs/` (relative to the working directory).
- Retention: **14-day** rolling window (`retainedFileCountLimit: 14`), one file per day.
- Each log entry includes `InstanceId`, `Application`, `Environment`, and a `CorrelationId` per HTTP request.
- Startup warnings are emitted for missing tokens, failed profile reload, and profile validation issues.

## What is not implemented

The following capabilities are **not present** in this build:

- **Hosted Reader loop**: the Reader library (`BotDs.Reader`) includes V5 sentinel scanning, process attachment, and scanner metrics, but the background loop that continuously reads game state is not wired into the host.
- **Live addon field population**: the `SnapshotPublisher` serves whatever `TelemetryFrame` it holds; nothing currently publishes from the Reader into it.
- **Action actuator**: the `EvaluatorLoop` produces `ActionDecision` values logged to the controller state, but no code translates them into game input. The application **cannot send game input**.

## Privacy boundary

- The application runs on localhost only; no remote endpoints are exposed.
- Credentials and chat content are not collected.
- Sensitive identifiers are omitted from logs.
- Control endpoints are not exposed beyond loopback.
- Origin checks reject cross-origin requests.
- The `context/` directory contains publisher-policy and account-enforcement research retained as historical evidence only; it does not influence runtime behaviour.

## Tests

Relevant xUnit tests in `tests/BotDs.Tests/`:

| Test class | Coverage |
|---|---|
| `DashboardSecurityMiddlewareTests` | Loopback IP rejection, null IP rejection, non-localhost host header rejection, cross-origin rejection, API token acceptance, control token acceptance for reload/control endpoints, API token rejection on control endpoints, empty-token fail-closed, IPv4-mapped loopback acceptance. |
| `ControllerStateMachineTests` | Emergency-stop latching (disarm rejected while stopped), clear-stop returning to disarmed, clear-stop not clearing faulted state, stale evaluation rejection across disarm/re-arm boundaries, inconsistent stop-reason forcing stopped state, generation advancing on every lifecycle boundary, configuration lease preventing arm until disposed, idempotent lease disposal, concurrent configuration lease rejection. |
| `ProfileServiceTests` | Missing directory preserving cache and active profile, duplicate ID rejection and cache preservation, atomic profile swap, active profile cleared on removal, empty directory success, invalid JSON preserving previous cache, semantically invalid profiles preserving cache, and privacy-safe malformed-load diagnostics. |
| `SnapshotPublisherTests` | Age accumulation reset on publish, monotonic time aging, wall-clock rollback not decreasing age, overflow saturation to `TimeSpan.MaxValue`, `ReceivedAtUtc` set to current time, initial disconnected state, provider field preservation, consistent `Latest` reads. |
