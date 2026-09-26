# pbx-voice Grok plugin

This plugin gives Grok the pbx-voice MCP tools and a skill that explains how to use them. The calls themselves are placed by the pbx-voice daemon, which you install and run separately (see the main [README](../README.md)); plugins deliver files, not programs.

## Install

1. Install and start the daemon, and create its `policy.json` (main README, "Setup").
2. Give Grok the daemon's token and, if it isn't on this machine, its URL:
   ```bash
   # The daemon writes the token on first start; `pbx-voice paths` shows where.
   export PBX_VOICE_MCP_TOKEN="$(cat ~/.local/state/pbx-voice/mcp-token)"
   # Only for a daemon on another host (behind HTTPS):
   # export PBX_VOICE_MCP_URL=https://pbx-voice.example.com/mcp
   ```
3. Add this repository as a marketplace and install the plugin:
   ```bash
   grok plugin marketplace add calebtt/pbx-voice
   grok plugin install pbx-voice
   ```

`.mcp.json` connects to `${PBX_VOICE_MCP_URL:-http://127.0.0.1:8765/mcp}` with `Authorization: Bearer ${PBX_VOICE_MCP_TOKEN}`. The token stays in your environment, not in any file here.

## Running on Grok Bot

Grok Bot runs the daemon on its own cloud computer, which all of your bots share.
- **Keep everything under `/workspace`.** Files there survive computer updates; installed programs and files elsewhere may not. Unpack the release there, and point the state directory there too:
  ```bash
  mkdir -p /workspace/pbx-voice
  tar -C /workspace/pbx-voice --strip-components=1 -xzf pbx-voice-<version>-linux-x64.tar.gz
  export SIPBOT_STATE_DIR=/workspace/pbx-voice/state   # for the daemon, `ctl`, and `paths`
  ```
- **Start the daemon in the background.** The computer has no systemd, so the unit file in the tarball doesn't apply, and nothing restarts the daemon after a crash or Update Computer:
  ```bash
  setsid nohup /workspace/pbx-voice/pbx-voice daemon >> /workspace/pbx-voice/daemon.log 2>&1 &
  ```
- **Calls at a set time come from your bot's routines,** not from the daemon: the routine places the call with `call_now` and waits for the outcome (the skill explains how). Allow the pbx-voice tools without approval (Auto-review: Always allow), because an approval a routine asks for expires after about 10 minutes. The daemon has to be running when the routine fires; unofficial reports say the computer sleeps when idle, which would stop it.
- **The agent shares the daemon's user account.** It can read the secrets and edit the policy, so the policy guides it but can't bind it. See [docs/security.md](../docs/security.md) for what does hold.

To use the stdio shim instead of HTTP (same machine, same user), configure the server yourself:

```toml
[mcp_servers.pbx-voice]
command = "pbx-voice"
args = ["mcp-stdio"]
```
