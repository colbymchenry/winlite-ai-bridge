# WinLite AI Bridge protocol

JSON-RPC 2.0 on standard input and output, one JSON object per line (newline-delimited). The caller
is the client; the bridge is the server. Requests carry an `id`; notifications do not.

All methods are the caller's unless marked **(bridge → caller)**.

## initialize

The handshake. The caller names the one MCP server the bridge should offer each tool, and the system
instructions for it. These are the only caller-specific values the bridge holds; it never reads them
beyond passing them to the tool.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "mcpServer":{"name":"example-server","command":"C:\\path\\to\\server.exe","keyVariable":"EXAMPLE_KEY","instructions":"You are ..."}
}}
```

Reply: `{"name":"WinLiteBridge","version":"0.1.0","protocol":1,"capabilities":{"signin":true,"sessions":true}}`

## tools/list

The AI command-line tools the bridge finds on the PC, each with whether it is installed and signed in.
Signed-in status is read by asking the tool itself (or, for a tool with no such command, only whether
its own sign-in file is present).

Reply: `{"tools":[{...}, ...]}`, each tool an object with:

- `id`, `name`, `maker`, `accounts`, `protocol` (`Claude`, `Codex` or `Acp`) — the tool.
- `installed` (bool), `signedIn` (`Yes`, `No` or `Unknown`), `who`, `note` — its state now.
- `destination` — where the person's words go, in words.
- `installPage`, `wingetId` — how a missing one is got (`wingetId` empty where it has no winget package).
- `limits` — what the tool cannot do in a conversation here, in words.
- `signInNote`, `hasSignIn` (bool), `hasDevice` (bool) — its sign-in: a note, whether it has one, and
  whether it has a device-code sign-in too.
- `deviceCode`, `devicePage` — regular-expression shapes of the device code and the page to enter it
  on, so the caller can copy the code from the tool's screen; empty where it has no device sign-in.

The caller needs no catalogue of its own: everything the AI page shows is here.

## tools/signin

Start a tool's own sign-in in a pseudoconsole (Windows' own ConPTY). The tool runs unmodified; the
bridge only relays its screen and your keystrokes.

```json
{"jsonrpc":"2.0","id":3,"method":"tools/signin","params":{"toolId":"claude-code","columns":100,"rows":30,"device":false}}
```

Reply: `{"started":true,"note":"..."}`. While it runs:

- **terminal/output (bridge → caller)** — `{"text":"..."}` — what the sign-in's screen printed.
- **terminal/input** — `{"text":"..."}` — keystrokes to send to it (VT sequences for keys).
- **terminal/resize** — `{"columns":100,"rows":30}` — the console was resized.
- **terminal/exited (bridge → caller)** — `{"code":0}` — the sign-in ended.
- **terminal/stop** — end the sign-in now.

## tools/install

A missing tool's install through winget, in a pseudoconsole like a sign-in: winget runs unmodified
and the person answers its questions. The relay is the same (terminal/output, terminal/input,
terminal/resize, terminal/exited, terminal/stop).

```json
{"jsonrpc":"2.0","id":3,"method":"tools/install","params":{"toolId":"codex","columns":100,"rows":30}}
```

Reply: `{"started":true,"note":"..."}`, or an error where the tool has no winget package.

## session/new

Start a conversation with a signed-in tool. `key` is the per-session key placed in the MCP server's
`keyVariable`, so the server can tie the tool's changes to this conversation.

```json
{"jsonrpc":"2.0","id":4,"method":"session/new","params":{"toolId":"claude-code","key":"<per-session key>"}}
```

Reply: `{"sessionId":"s1"}`

## session/prompt

Send the person's words. The tool's output streams back as `session/update` notifications; the reply
comes when the turn ends.

```json
{"jsonrpc":"2.0","id":5,"method":"session/prompt","params":{"sessionId":"s1","text":"make it warmer"}}
```

- **session/update (bridge → caller)** — `{"sessionId":"s1","kind":"Text|ToolStarted|ToolFinished|Note","text":"...","toolId":"...","toolName":"...","failed":false}`
- Reply: `{"sessionId":"s1","stopReason":"end_turn|cancelled"}`

## session/cancel

Interrupt the current turn: `{"sessionId":"s1"}`.

## shutdown

End the bridge: every session and any sign-in is closed, and the process exits.

## Notes

- Standard output carries only protocol messages. The bridge's own logging goes to standard error
  and to `%LOCALAPPDATA%\WinLiteBridge\bridge.log`.
- The bridge never writes a sign-in token anywhere and never sends one to the caller; each tool keeps
  its own credentials where it always has.
