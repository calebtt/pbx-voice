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

To use the stdio shim instead of HTTP (same machine, same user), configure the server yourself:

```toml
[mcp_servers.pbx-voice]
command = "pbx-voice"
args = ["mcp-stdio"]
```
