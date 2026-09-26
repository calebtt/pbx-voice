# pbx-voice for Grok

This folder gives Grok the pbx-voice MCP tools and a skill that explains how to use them (`skills/pbx-voice/SKILL.md`). The calls are placed by the pbx-voice daemon on the same computer. The MCP server, `pbx-voice mcp-stdio`, starts the daemon when a tool call finds it isn't running, so nothing has to keep it running between calls.

pbx-voice has no scheduler. For a call at a set time, the agent creates a routine that places the call then; the skill explains how.

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
