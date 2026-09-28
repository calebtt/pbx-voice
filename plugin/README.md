# pbx-voice plugin

This folder gives an agent the pbx-voice MCP tools and a skill that explains how to use them (`skills/pbx-voice/SKILL.md`). The calls are placed by the pbx-voice daemon on the same computer. The MCP server, `pbx-voice mcp-stdio`, starts the daemon when a tool call finds it isn't running, so nothing has to keep it running between calls.

pbx-voice has no scheduler. For a call at a set time, the agent creates a routine that places the call then; the skill explains how.

The plugin has a manifest for Grok (`.grok-plugin/plugin.json`) and one for Cursor (`.cursor-plugin/plugin.json`). Both use the same `.mcp.json` and skill.

## What you need

pbx-voice is bring-your-own-PBX. It includes no phone service. You need:

- **A SIP extension on a PBX you run or have an account on,** private or hosted, with its server, username, and password. A dedicated extension is best.
- **An xAI API key** for text-to-speech, speech-to-text, and the voice model that runs conversation calls. Use a key only for pbx-voice, with prepaid credit or a spending limit.
- **The `pbx-voice` binary** from a [release](https://github.com/calebtt/pbx-voice/releases) (glibc-based Linux, x64 or arm64).

## Network and credentials

- **Outbound:**
  - your PBX (SIP and RTP over UDP)
  - public STUN servers, to learn the public address behind NAT: `stun.l.google.com`, `stun1.l.google.com`, and `stun2.l.google.com` on port 19302, and `stun.stunprotocol.org` on port 3478
  - `api.x.ai`, over HTTPS for speech and over a WebSocket for the conversation voice model
- **Listening:**
  - MCP over HTTP on `127.0.0.1:8765` by default, with a bearer token required (`PBX_VOICE_MCP_LISTEN` changes the address, and `off` turns it off)
  - a control socket in the state directory
  - local UDP ports for SIP and RTP
- **Credentials:** `SIP_SERVER`, `SIP_USERNAME`, `SIP_PASSWORD`, and `XAI_API_KEY`. They go in `secrets.env` in the state directory (mode 0600) or in the environment. Tool arguments never carry them. pbx-voice sends no telemetry.

## Install on Grok Bot

Grok Bot doesn't install plugins from a git repository (its Marketplace holds connectors to services), so you give the bot the pieces. Grok Bot's official documentation doesn't cover custom MCP servers yet; step 2 follows a [third-party guide](https://composio.dev/content/how-to-add-mcp-servers-to-grok-bot), so check the result with step 5.

1. **Unpack the release under `/workspace`** on the bot's computer. Files there survive computer updates; installed programs and files elsewhere may not. Then create the policy and secrets:
   ```bash
   mkdir -p /workspace/pbx-voice
   tar -C /workspace/pbx-voice --strip-components=1 -xzf pbx-voice-<version>-linux-x64.tar.gz
   mkdir -p -m 700 /workspace/pbx-voice/state
   cp /workspace/pbx-voice/policy.example.json /workspace/pbx-voice/state/policy.json   # then edit it
   install -m 600 /workspace/pbx-voice/secrets.env.example /workspace/pbx-voice/state/secrets.env   # then edit it
   /workspace/pbx-voice/pbx-voice selftest
   ```
2. **Add the MCP server.** In a chat with the bot:
   > Add a custom MCP server called pbx-voice that runs: `env SIPBOT_STATE_DIR=/workspace/pbx-voice/state /workspace/pbx-voice/pbx-voice mcp-stdio`

   It's listed under Settings → Plugins → Yours, and every bot on your account can use it. When it starts the daemon, the daemon logs to `/workspace/pbx-voice/state/daemon.log`.
3. **Add the skill.** The release includes it as `SKILL.md`:
   > Create a skill called pbx-voice from the instructions in /workspace/pbx-voice/SKILL.md
4. **Allow the pbx-voice tools without approval** (Auto-review: Always allow). Routines that place calls need this: an approval that a routine asks for expires after about 10 minutes, so an alarm waiting for one never rings.
5. **Check it:** ask the bot to run the pbx-voice `status` tool. `registration.registered` and `pbx.reachable` should both be `true`. The first call starts the daemon, which takes a few seconds to register, so if they aren't yet, ask again.

On Grok Bot:
- **The computer has no systemd,** so the unit file in the release doesn't apply. The MCP server starts the daemon. To start or stop it by hand, set `SIPBOT_STATE_DIR=/workspace/pbx-voice/state` and run `pbx-voice start` or `pbx-voice stop`.
- **After Update Computer,** nothing needs reinstalling: the binary and state are in `/workspace`, and the next tool call starts the daemon.
- **The agent shares the daemon's user account.** It can read the secrets and edit the policy, so the policy guides it but can't bind it. See [docs/security.md](../docs/security.md) for what does hold.

## Install on the Grok CLI

1. Put the `pbx-voice` binary on your `PATH` (for example `~/.local/bin/pbx-voice`), and create `policy.json` and `secrets.env` in its state directory (`pbx-voice paths` shows where; see the main [README](../README.md), "Setup").
2. Add this repository as a marketplace and install the plugin:
   ```bash
   grok plugin marketplace add calebtt/pbx-voice
   grok plugin install pbx-voice
   ```

`.mcp.json` runs `pbx-voice mcp-stdio` from your `PATH`. For a daemon on another host, connect over HTTP instead (main README, "Connecting an agent").
