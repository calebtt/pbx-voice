# pbx-voice

Phone calls for agents, placed through a SIP extension you already have.

An agent (a Grok Bot, or any other MCP client) asks for a call. The daemon places it over your own PBX, plays or listens, and writes the result. The agent does not run the call, and no LLM runs while an alarm or message call is in progress. When to call is up to the agent: pbx-voice has no scheduler (see [Calling at a set time](#calling-at-a-set-time)).

| Type | Example | Status |
|---|---|---|
| `alarm` | Call me at 05:30 on weekdays until I say "I'm up" | Available |
| `message` | Call Mom and tell her my flight lands at 15:40 | Available |
| `conversation` | Ask the landlord when the plumber is coming and bring back the answer | Available |

Agents use it through MCP (a stdio shim on the same computer, or Streamable HTTP); operators can also use `pbx-voice ctl`.

**Status:** early. Run any alarm beside a normal phone alarm until you trust it.

## How it works

- **SIP:** [SipBotLib](https://github.com/calebtt/SipBotLib) (SIPSorcery), as a submodule pinned to a release. MinimalSileroVad is a submodule too. One registration, one call at a time, PCMU (G.711 μ-law) audio.
- **Speech:** xAI text-to-speech renders every clip when the call is requested, as 8 kHz μ-law. xAI speech-to-text transcribes short spoken replies ("I'm up", "got it"). No local speech-to-text or text-to-speech.
- **Replies:** the daemon listens only after its prompt has finished, for up to 8 s. [Silero VAD](https://github.com/snakers4/silero-vad) (V5, 8 kHz, through [MinimalSileroVad](https://github.com/calebtt/MinimalSileroVad) and the CPU build of ONNX Runtime) finds where the reply starts and ends: after about 0.6 s of silence, or 5 s of speech. The transcript must match a listed phrase as a whole utterance: "yeah, I'm up" counts, but "yes" alone or "I'm up but tired" does not.
- **Fallbacks:** if speech-to-text fails or takes over 3 s, 0.5–2.5 s of detected speech counts as a reply; a longer capture, such as a voicemail greeting, does not. A keypad press (RFC 4733) also counts. An alarm whose prompt could not be rendered plays a built-in wake tone.
- **Ring time** is counted from the first 180/183, not from the INVITE. A call with neither ringing nor an answer within 7 s of the INVITE is cancelled as a SIP failure.
- **Inbound calls** are never answered: each gets one immediate `480` (`inbound_reject_status` in the policy), so the PBX sends the caller to voicemail or the extension's forwarding.

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

If the voice session can't open within 3 s, a brief with a message falls back to a normal message call; a questions-only brief plays a short apology. A conversation redials (2 attempts, 10 minutes apart, 30 s ring) only if nobody answered. It costs xAI voice time (about $0.08 a minute at the time of writing); the policy caps minutes per call and per day.

**The conversation prompt is a text file.**
- The default is built in. `pbx-voice prompt` prints it.
- To change it, save your version as `conversation-prompt.txt` in the state directory; `pbx-voice paths` shows where.
  - The daemon reads the file before each call.
  - An invalid edit is ignored: the last valid prompt stays in use, and `status` shows the error under `conversation_prompt`.
  - Delete the file to go back to the default.
- Each conversation's record includes the prompt's source and SHA-256, so any transcript can be traced to the exact prompt.
- The brief is inserted as data, not as instructions: the `{{call}}` and `{{brief}}` placeholders become `<call>` and `<brief>` blocks, one field per line, with line breaks and block tags removed. Text in a brief can't add a heading or rule of its own.
- Treat the prompt like code: keep it under version control, and put no secrets in it. Assume a callee can get the model to repeat it.
- The limits that matter don't depend on the prompt. The daemon enforces the disclosure, the time limit, the record-only tools, and the evidence checks.

## Calling at a set time

pbx-voice places a call when it's asked to (`call_now`) and has no scheduler of its own. The agent's scheduler decides when: a Grok Bot routine, cron, or anything else that can run the agent or a command at a set time.
- **On Grok Bot:** ask the bot for the call, for example "wake me up at 5:30 on weekdays". It creates a routine for that time (a Weekdays routine at 5:30). The routine's instruction places the call with `call_now`, waits with `wait_for_call` until there's an outcome, and reports it. The documented routine schedules all repeat, so for a one-off call the bot deletes the routine after its first run.
- **Allow the pbx-voice tools without approval for routines.** An approval requested by a routine expires after about 10 minutes, so an alarm waiting for one never rings.
- **Keep waiting while it's `in_progress`.** An alarm that redials can take about 20 minutes, and `wait_for_call` waits at most 900 s, so the agent calls it again until there's an outcome.
- **From cron** (use full paths, since cron's `PATH` is short, and set `SIPBOT_STATE_DIR` if you moved the state directory): `30 5 * * 1-5 $HOME/.local/bin/pbx-voice start && $HOME/.local/bin/pbx-voice ctl call_now '{"type":"alarm","to":"me"}'`.

The daemon still enforces the policy (contacts, quiet hours, caps) on every call, whoever asked for it.

## Setup

You need a SIP extension on your PBX (a dedicated one is best) and a host that can run ONNX Runtime: glibc-based Linux on x64 or arm64 (Alpine and other musl systems are not supported).

**From a release:** each release has a self-contained build for linux-x64 and linux-arm64 (no .NET needed), with the example configuration files, the systemd unit, and the agent skill (`SKILL.md`).

```bash
v=0.1.0
curl -LO https://github.com/calebtt/pbx-voice/releases/download/v$v/pbx-voice-$v-linux-x64.tar.gz
curl -LO https://github.com/calebtt/pbx-voice/releases/download/v$v/SHA256SUMS
sha256sum --check --ignore-missing SHA256SUMS
tar -xzf pbx-voice-$v-linux-x64.tar.gz
./pbx-voice-$v-linux-x64/pbx-voice selftest
```

Each tarball also has a signed build-provenance attestation; [docs/security.md](docs/security.md) shows how to check it.

**From source,** with the .NET 8 SDK:

```bash
git clone --recursive https://github.com/calebtt/pbx-voice.git
cd pbx-voice
dotnet publish src/PbxVoice.Daemon -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
./dist/pbx-voice selftest  # checks this host can load ONNX Runtime and run Silero VAD
./dist/pbx-voice paths     # shows the state directory
```

A single-file build (the releases are one) unpacks its native libraries, ONNX Runtime among them, to `~/.cache/dotnet_bundle_extract` on first run, and again if they are deleted. If that directory is mounted `noexec`, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a directory you own, or publish without `-p:PublishSingleFile=true`. A framework-dependent build needs the ASP.NET Core 8 runtime.

The state directory is `SIPBOT_STATE_DIR`, else `$XDG_STATE_HOME/pbx-voice`, else `~/.local/state/pbx-voice`. The daemon creates it with mode 0700 and writes every file 0600. Put two files in it:

- **`policy.json`**: copy [`docs/policy.example.json`](docs/policy.example.json) and edit it. It holds the contacts, quiet hours, daily caps, phrase lists, and dialing settings. No MCP tool can change it, and `list_contacts` shows agents only masked numbers (an agent with a shell on the same account can still edit the file; see Safety). Edits apply without a restart.
- **`secrets.env`** (or the same variables in the environment, which win): see [`docs/secrets.env.example`](docs/secrets.env.example). `SIP_SERVER`, `SIP_USERNAME`, `SIP_PASSWORD`, optionally `SIP_PORT`, `SIP_FROMNAME`, `SIP_LOCAL_PORT`, and `XAI_API_KEY`. Without an xAI key, alarms still work (wake tone, speech detection), but messages and conversations are refused.

Run it:

```bash
./dist/pbx-voice daemon                   # foreground; logs to stderr
./dist/pbx-voice start                    # or in the background; logs to daemon.log in the state directory
./dist/pbx-voice stop                     # refused during a call unless --force
```

or as a systemd user service with [`docs/pbx-voice.service`](docs/pbx-voice.service).
- **Without a service manager** (Grok Bot's computer has none): connect the agent through the stdio shim (below). It starts the daemon when a tool call finds it isn't running, so nothing needs to keep it running between calls.
- **Detached:** a daemon started by `start` or the shim runs in its own session and isn't the shim's child, so it outlives the agent session that started it.
- **One daemon per extension.** A second start finds the running one and uses it.

## Connecting an agent (MCP)

Tools: `call_now`, `wait_for_call`, `cancel_call`, `list_calls`, `get_call`, `list_contacts`, `status`.
- Policy refusals come back as tool errors with the reason.
- Placing calls is rate-limited (10 a minute, 60 an hour) on top of the policy's daily cap.
- Results stay under 20,000 bytes: long transcripts are shortened, and `truncated: true` says so.
- Any result that carries the callee's words includes an `untrusted_callee_speech` notice.

**When the agent runs on the same computer** (Grok Bot, or the Grok CLI), connect through the stdio shim, `pbx-voice mcp-stdio`. It forwards to the daemon's control socket, and it starts the daemon if a tool call finds it isn't running (`PBX_VOICE_AUTOSTART=0` turns that off). It needs no token, and it works for the same user only.
- **Grok Bot:** see [plugin/README.md](plugin/README.md#install-on-grok-bot).
- **Grok CLI** (`~/.grok/config.toml`), or install the plugin (`plugin/`), which configures this for you:
  ```toml
  [mcp_servers.pbx-voice]
  command = "/home/you/.local/bin/pbx-voice"
  args = ["mcp-stdio"]
  ```

**When the daemon runs on another host,** the agent connects over HTTP. The daemon serves MCP at `http://127.0.0.1:8765/mcp` (Streamable HTTP). Every request needs the bearer token the daemon writes to `mcp-token` in the state directory on first start; `pbx-voice mcp-token` prints the endpoint and header. The daemon must already be running, because HTTP can't start it.

```toml
[mcp_servers.pbx-voice]
url = "https://pbx-voice.example.com/mcp"
headers = { "Authorization" = "Bearer ${PBX_VOICE_MCP_TOKEN}" }
```

- **Listen address:** `PBX_VOICE_MCP_LISTEN` changes it (`host:port`, or `off`). On a separate host, keep it on loopback and publish it through an HTTPS reverse proxy; the daemon warns if it listens elsewhere.
- **Protecting the token:** anyone with the token can place calls to your contacts, so treat it like a password.

## Using it (operator CLI)

```bash
pbx-voice ctl status
pbx-voice ctl list_contacts

# A wake-up call now; says "Good morning, it's <the time now>. Say 'I'm up' when you're awake."
pbx-voice ctl call_now '{"type":"alarm","to":"me","options":{"max_attempts":3}}'

# A message, then wait for the result
pbx-voice ctl call_now '{"type":"message","to":"mom","text":"My flight lands at 3:40."}'
pbx-voice ctl wait_for_call '{"call_id":"c_...","timeout_sec":300}'

pbx-voice ctl list_calls '{"limit":10}'
pbx-voice ctl get_call '{"call_id":"c_..."}'
pbx-voice ctl cancel_call '{"call_id":"c_..."}'
```

Call records (`calls/*.json`) keep each attempt: when ringing started, the SIP result, the replies with their transcripts and how each was recognized, and the outcome. Records are deleted after 30 days (`retention_days`). No audio is kept.

## Wake-up calls: phone setup

The daemon cannot get past your phone's own settings. On the phone that receives alarms:

- save the calling number as a starred contact
- allow starred contacts (or repeat callers) through Do Not Disturb
- turn off "silence unknown callers" for that number

A cell voicemail often answers before the ring time is up. With `ack: voice`, voicemail produces no "I'm up", so the alarm redials and ends as `not_acknowledged`, never `awake`.

## Safety

- Every callee must be in `policy.json` (unless `allow_unlisted_numbers` is on). Alarms only call `self`. Quiet hours block calls to anyone else. Daily caps apply. All of this is checked when a call is requested and again before it dials.
- If you administer the PBX, restrict the extension's outbound routes and allow one concurrent call. Where that's possible, it's the strongest control against toll fraud, because it sits outside this host.
- Calls to contacts that are not `self` start with a disclosure clip. AI-generated voices and call transcription are regulated in many places (in the US, the FCC treats AI voices as "artificial voice" under the TCPA, and some states require all-party consent to record). This is not legal advice; check before calling anyone outside your household.
- If the daemon runs on the same machine and user account as the agent (Grok Bot, for example), the agent can read `secrets.env` and edit the policy; the policy then guides the agent but can't bind it. Use an xAI key only for pbx-voice, with prepaid credit or a spending limit. [docs/security.md](docs/security.md) covers what holds on that setup, prompt injection, and how to verify releases.

Network endpoints used: your PBX (SIP and RTP over UDP), public STUN servers (to learn the public address behind NAT), and `api.x.ai`.

## Development

```bash
dotnet build PbxVoice.sln
dotnet test PbxVoice.sln
```

Unit tests use a fake clock and a scripted phone, so they cover policy days and quiet hours across DST changes, the call flows, redial rules, and policy checks without a PBX. The Silero tests run the real model on short recorded replies (`tests/PbxVoice.Tests/Fixtures`).

## Grok plugin

[`plugin/`](plugin/) holds the MCP connection and the agent skill. On the Grok CLI, this repository is also its marketplace (`grok plugin marketplace add calebtt/pbx-voice`). Grok Bot doesn't install plugins from a repository; [`plugin/README.md`](plugin/README.md) gives the steps for both.

Releases (tags `v*`) publish self-contained builds for linux-x64 and linux-arm64 with the example configuration files, the systemd unit, and `SKILL.md`, plus `SHA256SUMS` and build-provenance attestations.

## License

MIT
