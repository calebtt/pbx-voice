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
- **Start the daemon** with the systemd unit in the tarball if the computer runs systemd (add `Environment=SIPBOT_STATE_DIR=/workspace/pbx-voice/state` and fix `ExecStart`). Otherwise run it in the background: `nohup /workspace/pbx-voice/pbx-voice daemon >> /workspace/pbx-voice/daemon.log 2>&1 &`. After Update Computer, start it again.
- **Scheduled calls need the daemon running when they're due.** Unofficial reports say the computer sleeps when idle, which would stop the daemon; this isn't confirmed yet. A call that comes due while the daemon is down still goes out when it restarts, within 15 minutes for alarms and 5 minutes for other calls; after that it's recorded as `missed`.
- **The agent shares the daemon's user account.** It can read the secrets and edit the policy, so the policy guides it but can't bind it. See [docs/security.md](../docs/security.md) for what does hold.

To use the stdio shim instead of HTTP (same machine, same user), configure the server yourself:

```toml
[mcp_servers.pbx-voice]
command = "pbx-voice"
args = ["mcp-stdio"]
```
