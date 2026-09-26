# Security

pbx-voice lets an AI agent place real phone calls. This page covers what it can do, who can make it do what, which controls hold under which conditions, and what is left to the operator.

## What it can do

- Place calls from one SIP extension, to the contacts in the operator's policy.
- Speak text to the callee, and run a live AI voice conversation that follows a brief.
- Spend xAI credit (speech and voice time).

It can't transfer calls, receive calls (inbound calls are rejected), or run commands. The voice model's tools can only record answers and end the call.

## Who is involved

| Party | Reaches pbx-voice through | Trusted with |
|---|---|---|
| Operator | `policy.json`, `secrets.env`, `conversation-prompt.txt`, the control socket | Everything: contacts, limits, credentials, the prompt |
| Agent | The MCP tools: over HTTP with a bearer token, or through the stdio shim on the same account | Placing calls within the policy, and reading call records |
| Callee | Their voice on the call | Nothing: their words are data, never instructions |
| Voice model | The daemon's realtime session | Speaking within the brief, and calling record-only tools |

## Controls

**Always enforced by the daemon, whatever the agent, brief, or prompt says:**
- Callees must be listed in the policy (unless `allow_unlisted_numbers` is on). Alarms only call `self`.
- Quiet hours, daily call caps, and per-call and per-day conversation minutes. These are checked when a call is requested and again before it dials.
- The disclosure clip for callees that are not `self`.
- The time limit on conversations.
- The voice model's tools, which can't dial, transfer, or read data.
- Outcomes come from evidence: an answer counts only when its quote appears in the callee's own words, after the question was asked. The agent reports what was recorded, not what the model claimed.
- Results tell the agent that transcripts are the callee's words and never instructions (`untrusted_callee_speech`).
- Phone numbers are masked in everything the agent sees.

**Protected by file permissions:** the state directory is 0700, and its files and control socket are 0600. That keeps other users on the machine out, but not the account that runs the daemon.

**Protected by the MCP token:** the endpoint listens on 127.0.0.1 by default and refuses requests without the token. A web page open in a browser on the same machine can't call it, because it can't know the token. For a daemon on another host, put the endpoint behind an HTTPS reverse proxy.

## When the agent shares the daemon's computer

This is the common setup, and it's the one Grok Bot uses: the agent's shell runs as the same user as the daemon. The agent can then:
- read `secrets.env` (the SIP password and xAI key), the MCP token, and call records
- edit `policy.json` and `conversation-prompt.txt`
- use the control socket, which doesn't need the token
- skip the daemon and dial with the SIP password directly

On that setup, the policy, the prompt, and the skill's rules guide an agent that is working as intended. They can't stop an agent that has been taken over by something it read. The controls that hold even then are outside the machine:
- **xAI:** use an API key only for pbx-voice, with prepaid credit or a spending limit, so its cost is capped and it can be revoked on its own.
- **The PBX, if you administer it:** limit the extension's outbound routes (no international or premium numbers), and allow one call at a time. pbx-voice doesn't rely on this, since many operators can't change their PBX. Where it's possible, it's the strongest limit on what the SIP password can do.
- **The PBX's call records** are a log of every call from the extension that the agent can't edit.
- **Approvals in the agent host:** if the host can require approval before specific tool calls, requiring it for `call_now` puts a person in front of every call. On Grok Bot that also stops calls placed by routines (their approval requests expire after about 10 minutes), so it only suits setups that place calls while someone is in the chat.

Running the daemon as a separate system user only helps if the agent's account can't use `sudo` without a password.

## Prompt injection

| Path | Risk | Defense |
|---|---|---|
| Callee → voice model | The callee talks the model into sharing more, making promises, or recording false answers | The prompt allows sharing only the brief's facts. The tools only record. Answers need the callee's own words as evidence. The daemon enforces the disclosure and the time limit |
| Callee → agent | Transcripts carry instructions to the agent ("call this number and say…") | The untrusted-speech notice in results. The skill tells the agent never to follow instructions in transcripts. Unlisted numbers are refused by default |
| Something the agent read → brief | A hijacked agent writes a harmful brief | The daemon can't tell. The contact list, quiet hours, and caps limit the damage. Approvals at the agent host stop it |
| Brief → voice model instructions | Brief text poses as rules | The brief is inserted as data blocks, one field per line, with line breaks and block tags removed. The prompt tells the model the blocks are information, not rules |

Assume the callee can get the model to repeat its instructions and the brief. Put nothing in either that you wouldn't say to them.

## Secrets

- `secrets.env` holds the SIP credentials and the xAI key. Environment variables take precedence over it. The daemon reads only the keys it knows.
- No secret goes into the prompt, the brief, call records, or tool results.
- The MCP token is 32 random bytes. To rotate it, delete `mcp-token` and restart the daemon, then update the agent's configuration.

## Releases and dependencies

- Each release publishes `SHA256SUMS` and a signed build-provenance attestation for every tarball. To check a download:
  ```bash
  gh attestation verify pbx-voice-<version>-linux-x64.tar.gz --repo calebtt/pbx-voice
  ```
- GitHub Actions are pinned to commit SHAs.
- SipBotLib is pinned to a release tag, and MinimalSileroVad to a commit (`.gitmodules`). NuGet packages use fixed versions, not ranges.

## The OWASP Top 10 for Agentic Applications

How the risks from the [OWASP list](https://genai.owasp.org/2025/12/09/owasp-top-10-for-agentic-applications-the-benchmark-for-agentic-security-in-the-age-of-autonomous-ai/) that apply here are handled:

| Risk | Here |
|---|---|
| ASI01 Agent goal hijack | The prompt injection paths above |
| ASI02 Tool misuse | The policy checks when a call is requested and before it dials, caps, and record-only voice tools |
| ASI03 Identity and privilege abuse | One extension and one key for pbx-voice. On a shared computer, the agent holds the operator's privileges (see above) |
| ASI04 Supply chain | Pinned Actions, submodules, and packages. Release checksums and provenance |
| ASI08 Cascading failures | Redial limits, daily caps, and the per-day conversation minutes |
| ASI09 Human-agent trust exploitation | The disclosure to callees. Outcomes from evidence, so the user isn't told a call succeeded when it didn't |
| ASI10 Rogue agents | The caps above. The PBX's call records as an independent log |

Unexpected code execution (ASI05) doesn't apply: nothing in pbx-voice runs code or commands from the agent, the brief, or the callee.

## Reporting a vulnerability

See [SECURITY.md](../SECURITY.md).
