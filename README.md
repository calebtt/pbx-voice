# pbx-voice

Scheduled phone calls for agents, placed through a SIP extension you already have.

An agent (a Grok Bot, or any other MCP client) schedules a call. A long-lived daemon places it at the right time over your own PBX, plays or listens, and writes the result. The agent does not run the call, and no LLM runs while an alarm or message call is in progress.

| Type | Example | Status |
|---|---|---|
| `alarm` | Call me at 05:30 on weekdays until I say "I'm up" | Available |
| `message` | Call Mom and tell her my flight lands at 15:40 | Available |
| `conversation` | Ask the landlord when the plumber is coming and bring back the answer | Available |

Agents use it through MCP (Streamable HTTP, or a stdio shim); operators can also use `pbx-voice ctl`.

**Status:** early. Run any alarm beside a normal phone alarm until you trust it.

## How it works

- **SIP:** [SipBotLib](https://github.com/calebtt/SipBotLib) (SIPSorcery), as a submodule pinned to a release. MinimalSileroVad is a submodule too. One registration, one call at a time, PCMU (G.711 μ-law) audio.
- **Speech:** xAI text-to-speech renders every clip when the call is scheduled, as 8 kHz μ-law. xAI speech-to-text transcribes short spoken replies ("I'm up", "got it"). No local speech-to-text or text-to-speech.
- **Replies:** the daemon listens only after its prompt has finished, for up to 8 s. [Silero VAD](https://github.com/snakers4/silero-vad) (V5, 8 kHz, through [MinimalSileroVad](https://github.com/calebtt/MinimalSileroVad) and the CPU build of ONNX Runtime) finds where the reply starts and ends: after about 0.6 s of silence, or 5 s of speech. The transcript must match a listed phrase as a whole utterance: "yeah, I'm up" counts, but "yes" alone or "I'm up but tired" does not.
- **Fallbacks:** if speech-to-text fails or takes over 3 s, 0.5–2.5 s of detected speech counts as a reply; a longer capture, such as a voicemail greeting, does not. A keypad press (RFC 4733) also counts. An alarm whose prompt could not be rendered plays a built-in wake tone.
- **Ring time** is counted from the first 180/183, not from the INVITE. A call with neither ringing nor an answer within 7 s of the INVITE is cancelled as a SIP failure.
- **Inbound calls** are never answered: each gets one immediate `480`, so the PBX sends the caller to voicemail or the extension's forwarding.

### Alarm

| Option | Default | |
|---|---|---|
| `ack` | `voice` | `voice`: the call ends as `awake` only when you say an awake phrase. `none`: play the wake-up message twice; the outcome is `played` (voicemail answering looks the same as you answering) |
| `redial`, `max_attempts`, `retry_minutes` | on, 5, 3 | Redial after no answer, busy, a SIP failure, or no recognized reply |
| `ring_seconds` | 45 | |
| `snooze_minutes`, `max_snoozes` | 10, 3 | Say "snooze", "five more minutes", or "ten more minutes" |

Alarms only call contacts marked `self`. Outcomes: `awake`, `played`, `not_acknowledged`, `not_answered`, `missed`, `failed`.

### Message

Plays a pause, then (for contacts that are not `self`) a disclosure that this is an automated assistant calling on your behalf, then the message. With `ack: voice` (the default) it asks for "got it" and replays on "repeat" (up to 3 times). It redials only when nobody answered (2 attempts, 10 minutes apart, 30 s ring), never after the message has played. Outcomes: `confirmed`, `played`, `played_unconfirmed`, `not_answered` (including `hung_up_early`), `missed`, `failed`.

### Conversation

A live voice call run by the Grok Voice realtime model, following a brief:

```json
{
  "type": "conversation",
  "to": "landlord",
  "brief": {
    "goal": "Find out when the plumber is coming to fix the kitchen sink.",
    "message": "Hi, this is about the leak under the kitchen sink.",
    "facts": ["The leak is under the kitchen sink.", "Alex is home after 4 pm on weekdays."],
    "ask": [
      { "name": "visit_time", "question": "When is the plumber coming?", "hint": "day and time window" },
      { "name": "access_needed", "question": "Does someone need to be home to let them in?", "required": false }
    ],
    "max_minutes": 3
  }
}
```

1. **At answer.** The daemon plays the disclosure (contacts that are not `self`) while the voice session opens. It then speaks `message` word for word; the message can't be interrupted.
2. **During the call.** The model asks the questions one at a time and answers only from `facts`. It reads the answers back and ends the call.
3. **What the model can do.** It records answers with the callee's exact words, records questions it couldn't answer, and ends the call. It can't dial, transfer, or read anything else.
4. **Barge-in.** If the callee talks over the model, the model's audio stops.
5. **Time limit.** The daemon ends the call at `max_minutes`, whatever the model does.

**The outcome comes from evidence, not from the model's say-so.**
- An answer counts (`evidence: matched`) only when its quote appears in the callee's own transcribed words, after the question was asked.
- `completed` needs every required answer matched, and, if there was a message, the callee confirming it.
- `voicemail_left` and `declined` also need the daemon's own check.
- Other outcomes: `partial`, `degraded_to_message`, `not_answered`, `missed`, `failed`.

If the voice session can't open within 3 s, a brief with a message falls back to a normal message call; a questions-only brief plays a short apology. A conversation redials (2 attempts, 10 minutes apart, 7 s ring) only if nobody answered. It costs xAI voice time (about $0.08 a minute at the time of writing); the policy caps minutes per call and per day.

## Setup

Requires the .NET 8 SDK to build, a SIP extension on your PBX (a dedicated one is best), and a host that can run ONNX Runtime: glibc-based Linux on x64 or arm64 (Alpine and other musl systems are not supported).

```bash
git clone --recursive https://github.com/calebtt/pbx-voice.git
cd pbx-voice
dotnet publish src/PbxVoice.Daemon -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
./dist/pbx-voice selftest  # checks this host can load ONNX Runtime and run Silero VAD
./dist/pbx-voice paths     # shows the state directory
```

A single-file build unpacks ONNX Runtime to a temp directory on first run. If that directory is mounted `noexec`, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a directory you own, or publish without `-p:PublishSingleFile=true`.

The state directory is `SIPBOT_STATE_DIR`, else `$XDG_STATE_HOME/pbx-voice`, else `~/.local/state/pbx-voice`. The daemon creates it with mode 0700 and writes every file 0600. Put two files in it:

- **`policy.json`**: copy [`docs/policy.example.json`](docs/policy.example.json) and edit it. It holds the contacts, quiet hours, daily caps, phrase lists, and dialing settings. The agent can read it and cannot change it. Edits apply without a restart.
- **`secrets.env`** (or the same variables in the environment, which win): see [`docs/secrets.env.example`](docs/secrets.env.example). `SIP_SERVER`, `SIP_USERNAME`, `SIP_PASSWORD`, optionally `SIP_PORT`, `SIP_FROMNAME`, `SIP_LOCAL_PORT`, and `XAI_API_KEY`. Without an xAI key, alarms still work (wake tone, speech detection) but messages cannot be scheduled.

Run it:

```bash
./dist/pbx-voice daemon                   # foreground; logs to stderr
```

or as a systemd user service with [`docs/pbx-voice.service`](docs/pbx-voice.service). Only one daemon runs per extension. The ASP.NET Core runtime is included in the self-contained build; a framework-dependent build needs the ASP.NET Core 8 runtime.

## Connecting an agent (MCP)

The daemon serves MCP at `http://127.0.0.1:8765/mcp` (Streamable HTTP). Every request needs the bearer token the daemon writes to `mcp-token` in the state directory on first start. `pbx-voice mcp-token` prints the endpoint and header.

Tools: `schedule_call`, `call_now`, `wait_for_call`, `list_schedules`, `cancel_schedule`, `cancel_call`, `list_calls`, `get_call`, `list_contacts`, `status`.
- Policy refusals come back as tool errors with the reason.
- Placing and scheduling calls is rate-limited (10 a minute, 60 an hour) on top of the policy's daily cap.
- Results stay under 20,000 bytes: long transcripts are shortened, and `truncated: true` says so.
- Any result that carries the callee's words includes an `untrusted_callee_speech` notice.

Grok (`~/.grok/config.toml`), over HTTP:

```toml
[mcp_servers.pbx-voice]
url = "http://127.0.0.1:8765/mcp"
headers = { "Authorization" = "Bearer ${PBX_VOICE_MCP_TOKEN}" }
```

or through the stdio shim, which forwards to the running daemon's control socket and needs no token (same user only):

```toml
[mcp_servers.pbx-voice]
command = "/home/you/.local/bin/pbx-voice"
args = ["mcp-stdio"]
```

- **Listen address:** `PBX_VOICE_MCP_LISTEN` changes it (`host:port`, or `off`). On a separate host (deployment mode B), keep it on loopback and publish it through an HTTPS reverse proxy; the daemon warns if it listens elsewhere.
- **Protecting the token:** anyone with the token can place calls to your contacts, so treat it like a password.

## Using it (operator CLI)

```bash
pbx-voice ctl status
pbx-voice ctl list_contacts

# Weekday wake-up call; says "Good morning, it's 5:30 AM. Say 'I'm up' when you're awake."
pbx-voice ctl schedule_call '{"type":"alarm","to":"me","repeat":{"days":"weekdays","time":"05:30","tz":"America/Chicago"}}'

# One-off, with an explicit offset (a time without an offset or tz is refused)
pbx-voice ctl schedule_call '{"type":"alarm","to":"me","at":"2026-10-06T06:00:00-05:00","options":{"max_attempts":3}}'

# A message now, then wait for the result
pbx-voice ctl call_now '{"type":"message","to":"mom","text":"My flight lands at 3:40."}'
pbx-voice ctl wait_for_call '{"call_id":"c_...","timeout_sec":300}'

pbx-voice ctl list_calls '{"limit":10}'
pbx-voice ctl get_call '{"call_id":"c_..."}'
pbx-voice ctl cancel_schedule '{"schedule_id":"s_..."}'
```

Call records (`calls/*.json`) keep each attempt: when ringing started, the SIP result, the replies with their transcripts and how each was recognized, and the outcome. Records are deleted after 30 days (`retention_days`). No audio is kept.

## Wake-up calls: phone setup

The daemon cannot get past your phone's own settings. On the phone that receives alarms:

- save the calling number as a starred contact
- allow starred contacts (or repeat callers) through Do Not Disturb
- turn off "silence unknown callers" for that number

A cell voicemail often answers before the ring time is up. With `ack: voice`, voicemail produces no "I'm up", so the alarm redials and ends as `not_acknowledged`, never `awake`.

## Safety

- Every callee must be in `policy.json` (unless `allow_unlisted_numbers` is on). Alarms only call `self`. Quiet hours block calls to anyone else. Daily caps apply. All of this is checked when a call is scheduled and again when it fires.
- On the PBX, restrict the extension's outbound routes and allow one concurrent call. That is the strongest control against toll fraud because it sits outside this host.
- Calls to contacts that are not `self` start with a disclosure clip. AI-generated voices and call transcription are regulated in many places (in the US, the FCC treats AI voices as "artificial voice" under the TCPA, and some states require all-party consent to record). This is not legal advice; check before calling anyone outside your household.
- If the daemon runs on the same machine and user account as the agent, the agent can read `secrets.env`. Use a dedicated extension with a unique password and an xAI key used only for this, or run the daemon on another host.

Network endpoints used: your PBX (SIP and RTP over UDP), public STUN servers (to learn the public address behind NAT), and `api.x.ai`.

## Development

```bash
dotnet build PbxVoice.sln
dotnet test PbxVoice.sln
```

Unit tests use a fake clock and a scripted phone, so they cover scheduling across DST changes, the call flows, redial rules, and policy checks without a PBX. The Silero tests run the real model on short recorded replies (`tests/PbxVoice.Tests/Fixtures`).

## License

MIT
