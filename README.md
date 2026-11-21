# WinLite AI Bridge

The open-source part of [Windows Lite](https://github.com/colbymchenry/windows-lite)'s look that
touches the AI command-line tools on your PC. It is a small, standalone program that:

- **finds** the AI coding tools you have installed (Claude Code, Codex, Gemini CLI, OpenCode,
  Antigravity CLI, and others);
- **runs each tool's own sign-in** in a pseudoconsole (Windows' own ConPTY), the same screen a
  terminal would show, and passes your keystrokes straight through;
- **holds a conversation** with a signed-in tool over the tool's own documented protocol (Claude
  Code's streaming JSON, Codex's app server, or the Agent Client Protocol), with one MCP server the
  caller provides and nothing else.

Windows Lite's paid app starts this program and talks to it over a small JSON-RPC protocol on
standard input and output. **The bridge never sends a sign-in token anywhere, and never writes one
down.** Each tool signs you in through its maker's own flow and keeps its own credentials where it
always has; the bridge only shows you the tool's screen and relays what you type. Because this part
is open, anyone can check that promise.

## Why it is its own program

Windows Lite is a closed, paid product. The part that handles your AI tools' sign-ins is kept open
so that:

1. you (or anyone) can read exactly what happens to a sign-in — it is handled here, in the open,
   not in the closed app;
2. it can meet the "local and open-source apps" condition that some makers (for example OpenAI's
   "Sign in with ChatGPT") set for using a subscription sign-in.

The closed app provides the look's own MCP server to the tool; the bridge passes it through. The
bridge knows nothing about the look itself.

## Protocol (standard input/output, JSON-RPC 2.0, one message per line)

| Method | Direction | What it does |
| --- | --- | --- |
| `initialize` | caller → bridge | Handshake; the bridge replies with its version and capabilities. |
| `tools/list` | caller → bridge | The AI tools found on the PC, each with whether it is signed in. |
| `tools/signin` | caller → bridge | Start a tool's own sign-in in a pseudoconsole. |
| `tools/install` | caller → bridge | Install a missing tool through winget, in a pseudoconsole. |
| `terminal/input` | caller → bridge | Keystrokes for the running sign-in or install. |
| `terminal/resize` | caller → bridge | The console was resized. |
| `terminal/output` | bridge → caller | What the sign-in's screen printed (a notification). |
| `session/new` | caller → bridge | Start a conversation with a signed-in tool, given the MCP server to offer it. |
| `session/prompt` | caller → bridge | Send the person's words; the tool's events stream back as `session/update`. |
| `session/update` | bridge → caller | A streamed event from the tool (text, a tool call, a note). |
| `session/cancel` | caller → bridge | Interrupt the current turn. |

See [PROTOCOL.md](PROTOCOL.md) for the message shapes.

## Build

```
dotnet build WinLiteBridge.csproj -c Release
```

It targets .NET Framework 4.8, which every Windows 11 has, so nothing extra is installed to run it.

## Licence

MIT. See [LICENSE](LICENSE).
