# pbx-voice

Scheduled phone calls for agents, placed through a SIP extension you already have.

An agent (a Grok Bot, or any other MCP client) schedules a call. A long-lived daemon places it at the right time over your own PBX, plays or listens, and writes the result. The agent does not run the call.

v1 call types:

| Type | Example |
|---|---|
| `alarm` | Call me at 05:30 on weekdays until I say "I'm up" |
| `message` | Call Mom and tell her my flight lands at 15:40 |
| `conversation` | Ask the landlord when the plumber is coming and bring back the answer |

**Status:** in development. Nothing here is ready for use yet.

- SIP runtime: [SipBotLib](https://github.com/calebtt/SipBotLib) (SIPSorcery)
- Speech: xAI text-to-speech for scripted audio, xAI speech-to-text for short spoken replies, and Grok Voice for conversations. No local speech models.

## License

MIT
