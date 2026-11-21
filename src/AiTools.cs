// L7: the AI command-line tools the assistant runs (docs/10-customization.md, "Connecting an AI",
// way 2): where each is found, how it says whether it is signed in, its own sign-in, and where a
// missing one comes from. Windows Lite reads no token: it asks the tool itself, or, for a tool with
// no such command, looks only at whether the tool's own sign-in file is there.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinLiteBridge
{
    internal enum SignedIn { Unknown, Yes, No }

    // How the assistant talks to a tool: Claude Code's streaming JSON, Codex's app server, or the
    // Agent Client Protocol.
    internal enum AgentProtocol { Claude, Codex, Acp }

    internal sealed class AiTool
    {
        public string Id = "";
        public string Name = "";
        public string Maker = "";
        // Who can use it, in words, for the AI page's list of tools.
        public string Accounts = "";
        public AgentProtocol Protocol;
        // Its command's file names, looked for on the PATH, then in its usual places.
        public string[] Files = new string[0];
        public string[] Places = new string[0];
        // Where an npm shim (a .cmd) points to the program itself, relative to the shim's folder.
        public string[] Programs = new string[0];
        // The tool's own sign-in, run in a pseudoconsole (empty: the tool itself, which asks on its
        // first start); null when it has none. A second sign-in with a device code, where it has one.
        public string[] SignInArguments;
        public string[] DeviceArguments;
        // What the sign-in window says first, where the tool's own sign-in needs a word of help.
        public string SignInNote = "";
        // The tool's own answer to whether it is signed in, read by ReadStatus.
        public string[] StatusArguments = new string[0];
        public Action<Agents.RunResult, ToolState> ReadStatus;
        // For a tool with no such command: its own sign-in files and variables, looked at only for
        // whether they are there.
        public string[] SignInFiles = new string[0];
        public string[] SignInVariables = new string[0];
        // What starts it for the assistant (the protocol's own command, for Codex and ACP), and the
        // mode an ACP agent is asked to work in.
        public string[] AgentArguments = new string[0];
        public string AgentMode = "";
        // Where the person's words go, said under the field (decision 55); the tool and its maker
        // where empty.
        public string Destination = "";
        // A device code its sign-in prints, and the page to enter it on.
        public Regex DeviceCode;
        public Regex DevicePage;
        public string WingetId = "";
        public string InstallPage = "";
        // What the assistant could not take from it, said when a conversation starts.
        public string Limits = "";
    }

    internal sealed class ToolState
    {
        // Where its command is, or null when it is not on this PC.
        public string Path;
        public SignedIn SignedIn = SignedIn.Unknown;
        public string Who = "";
        public string Said = "";
    }

    internal static class AiTools
    {
        // Kimi CLI and other ACP agents are added as each is checked (docs/10-customization.md).
        public static readonly AiTool[] All = { ClaudeCode(), Codex(), GeminiCli(), AntigravityCli(), OpenCode() };

        public static string DestinationOf(AiTool tool)
        {
            return tool.Destination.Length > 0 ? tool.Destination : tool.Name + " (" + tool.Maker + ")";
        }

        public static AiTool Get(string id)
        {
            return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // ---- the tools ----

        private static AiTool ClaudeCode()
        {
            return new AiTool
            {
                Id = "claude-code",
                Name = "Claude Code",
                Maker = "Anthropic",
                Accounts = "Claude Pro, Max, Team and Enterprise plans, and Anthropic Console accounts.",
                Protocol = AgentProtocol.Claude,
                Files = new[] { "claude.exe", "claude.cmd" },
                // The native installer's, winget's, and npm's places (code.claude.com/docs/en/setup).
                Places = new[]
                {
                    @"%USERPROFILE%\.local\bin\claude.exe",
                    @"%LOCALAPPDATA%\Microsoft\WinGet\Links\claude.exe",
                    @"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe\claude.exe",
                    @"%APPDATA%\npm\claude.cmd",
                },
                Programs = new[] { @"node_modules\@anthropic-ai\claude-code\bin\claude.exe" },
                SignInArguments = new[] { "auth", "login" },
                StatusArguments = new[] { "auth", "status" },
                ReadStatus = ClaudeStatus,
                // Its sign-in prints the page's address and asks for a code only where the browser
                // cannot reach it back; there is no device code to copy.
                DevicePage = new Regex(@"https://(claude\.ai|console\.anthropic\.com|platform\.claude\.com)/\S+", RegexOptions.CultureInvariant),
                WingetId = "Anthropic.ClaudeCode",
                InstallPage = "https://code.claude.com/docs/en/setup",
            };
        }

        // "claude auth status" answers in JSON, and exits with 0 when signed in and 1 when not.
        private static void ClaudeStatus(Agents.RunResult result, ToolState state)
        {
            JsonObject answer = JsonIn(result.Output);
            if (answer != null)
            {
                JsonNode loggedIn = answer["loggedIn"];
                if (loggedIn != null && loggedIn.GetValueKind() == JsonValueKind.True) { state.SignedIn = SignedIn.Yes; }
                else if (loggedIn != null && loggedIn.GetValueKind() == JsonValueKind.False) { state.SignedIn = SignedIn.No; }
                state.Who = FindText(answer, "email");
                string method = Text(answer, "authMethod");
                if (state.SignedIn == SignedIn.Yes && state.Who.Length == 0 && method.StartsWith("api_key", StringComparison.Ordinal)) { state.Said = "with an API key"; }
            }
            if (state.SignedIn == SignedIn.Unknown && result.Code == 0) { state.SignedIn = SignedIn.Yes; }
        }

        // Codex's CLI (codex-rs/cli/src/login.rs), read 20 November 2026: "codex login" in the
        // browser, "--device-auth" for a one-time code, "app-server" for JSON-RPC over standard
        // input and output. winget's OpenAI.Codex comes from Codex's own release workflow.
        private static AiTool Codex()
        {
            return new AiTool
            {
                Id = "codex",
                Name = "Codex",
                Maker = "OpenAI",
                Accounts = "ChatGPT plans and OpenAI API keys.",
                Protocol = AgentProtocol.Codex,
                Files = new[] { "codex.exe", "codex.cmd" },
                Places = new[] { @"%LOCALAPPDATA%\Microsoft\WinGet\Links\codex.exe", @"%APPDATA%\npm\codex.cmd" },
                SignInArguments = new[] { "login" },
                DeviceArguments = new[] { "login", "--device-auth" },
                StatusArguments = new[] { "login", "status" },
                ReadStatus = CodexStatus,
                AgentArguments = new[] { "app-server" },
                // "2. Enter this one-time code (expires in 15 minutes)", then the code on a line of its own.
                DeviceCode = new Regex(@"one-time code[^\n]*\r?\n\s*([A-Za-z0-9][A-Za-z0-9-]{4,}[A-Za-z0-9])", RegexOptions.CultureInvariant),
                DevicePage = new Regex(@"https://\S+/codex/device", RegexOptions.CultureInvariant),
                WingetId = "OpenAI.Codex",
                InstallPage = "https://learn.chatgpt.com/docs/codex/cli",
                Limits = "Here Codex works without its shell, web search or image viewer, in its own read-only sandbox.",
            };
        }

        // "codex login status" writes to its error stream: "Logged in using ChatGPT", "API key
        // configured - ...", or "Not logged in" with exit code 1.
        private static void CodexStatus(Agents.RunResult result, ToolState state)
        {
            string output = result.Output ?? "";
            if (output.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0) { state.SignedIn = SignedIn.No; return; }
            if (result.Code != 0) { state.Said = FirstLine(output); return; }
            state.SignedIn = SignedIn.Yes;
            if (output.IndexOf("ChatGPT", StringComparison.Ordinal) >= 0) { state.Said = "with ChatGPT"; }
            else if (output.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0) { state.Said = "with an API key"; }
        }

        // Gemini CLI (geminicli.com/docs, and its source, read 20 November 2026): "gemini --acp". It has
        // no sign-in command; it asks on its first start. For personal Google accounts Google moved to
        // Antigravity CLI on 18 June 2026; Gemini CLI keeps paid API keys and organisations' licences.
        private static AiTool GeminiCli()
        {
            return new AiTool
            {
                Id = "gemini-cli",
                Name = "Gemini CLI",
                Maker = "Google",
                Accounts = "Paid Gemini API keys, and organisations' Gemini Code Assist licences. A personal Google account signs in through Antigravity CLI.",
                Protocol = AgentProtocol.Acp,
                Files = new[] { "gemini.cmd", "gemini.exe" },
                Places = new[] { @"%APPDATA%\npm\gemini.cmd" },
                SignInArguments = new string[0],
                SignInNote = "Gemini CLI starts as it does in a terminal and asks how to sign in. When it is signed in, type /quit.",
                SignInFiles = new[] { @"%USERPROFILE%\.gemini\oauth_creds.json" },
                SignInVariables = new[] { "GEMINI_API_KEY" },
                AgentArguments = new[] { "--acp" },
                InstallPage = "https://github.com/google-gemini/gemini-cli",
            };
        }

        // Antigravity CLI ("agy", antigravity.google/docs, read 20 November 2026): "agy acp" is its ACP
        // server; "agy acp --login" signs in with a code pasted back. Google installs it with a script,
        // which Windows Lite never runs, so the page links to Google's own instructions.
        private static AiTool AntigravityCli()
        {
            return new AiTool
            {
                Id = "antigravity-cli",
                Name = "Antigravity CLI",
                Maker = "Google",
                Accounts = "Personal Google accounts: free, Google AI Pro and Ultra.",
                Protocol = AgentProtocol.Acp,
                Files = new[] { "agy.exe" },
                Places = new[] { @"%LOCALAPPDATA%\agy\bin\agy.exe", @"%LOCALAPPDATA%\Microsoft\WinGet\Links\agy.exe" },
                SignInArguments = new[] { "acp", "--login" },
                SignInNote = "Antigravity CLI's own sign-in: open the page it shows, sign in there, and paste the code it gives you here.",
                AgentArguments = new[] { "acp" },
                InstallPage = "https://antigravity.google/docs/getting-started?tab=cli",
                Limits = "Antigravity CLI keeps its own tools here, in an empty folder; Windows Lite refuses every change they ask to make.",
            };
        }

        // OpenCode (opencode.ai/docs, read 20 November 2026): "opencode acp", signed in to the
        // providers the person chose with "opencode auth login". It answers through those providers.
        private static AiTool OpenCode()
        {
            return new AiTool
            {
                Id = "opencode",
                Name = "OpenCode",
                Maker = "the OpenCode project",
                Accounts = "OpenRouter, Kimi, GLM, DeepSeek and most other providers, and models on this PC.",
                Protocol = AgentProtocol.Acp,
                Files = new[] { "opencode.exe", "opencode.cmd" },
                Places = new[] { @"%LOCALAPPDATA%\Microsoft\WinGet\Links\opencode.exe", @"%APPDATA%\npm\opencode.cmd" },
                SignInArguments = new[] { "auth", "login" },
                SignInFiles = new[] { @"%USERPROFILE%\.local\share\opencode\auth.json" },
                AgentArguments = new[] { "acp" },
                AgentMode = "plan",
                Destination = "OpenCode and the AI provider you chose in it",
                InstallPage = "https://opencode.ai",
                Limits = "OpenCode works here in its plan mode, which edits no files; Windows Lite refuses every change it asks to make.",
            };
        }

        // ---- finding a tool ----

        // The PATH as Windows has it now: this app's own, then the person's and the machine's, so that
        // a tool installed after the app started is found too.
        public static string FreshPath()
        {
            var folders = new List<string>();
            foreach (string path in new[]
            {
                Environment.GetEnvironmentVariable("PATH") ?? "",
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "",
            })
            {
                foreach (string folder in path.Split(';').Select(f => Environment.ExpandEnvironmentVariables(f.Trim())).Where(f => f.Length > 0))
                {
                    if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) { folders.Add(folder); }
                }
            }
            return string.Join(";", folders);
        }

        // Gives this app the fresh PATH, so that what it starts (a sign-in, an install, a
        // conversation) finds what was installed since it started, Node.js among them.
        public static void UseFreshPath()
        {
            try { Environment.SetEnvironmentVariable("PATH", FreshPath()); } catch (Exception ex) { Log.Write("AI tools: PATH: " + ex.Message); }
        }

        // Where the tool's command is, or null.
        public static string Find(AiTool tool)
        {
            foreach (string folder in FreshPath().Split(';'))
            {
                foreach (string file in tool.Files)
                {
                    try
                    {
                        string candidate = System.IO.Path.Combine(folder, file);
                        if (File.Exists(candidate)) { return candidate; }
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
            foreach (string place in tool.Places)
            {
                string candidate = Environment.ExpandEnvironmentVariables(place);
                if (File.Exists(candidate)) { return candidate; }
            }
            return null;
        }

        // The program to start for a conversation: the program itself where an npm shim points to
        // one (a shim needs cmd.exe in between), else the command as found.
        public static string Program(AiTool tool, string path)
        {
            if (path == null || !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) { return path; }
            string folder = System.IO.Path.GetDirectoryName(path) ?? "";
            foreach (string relative in tool.Programs)
            {
                string candidate = System.IO.Path.Combine(folder, relative);
                if (File.Exists(candidate)) { return candidate; }
            }
            return path;
        }

        // Whether the tool is here, and signed in, by its own answer.
        public static ToolState State(AiTool tool)
        {
            UseFreshPath();
            var state = new ToolState { Path = Find(tool) };
            if (state.Path == null) { return state; }
            if (tool.StatusArguments.Length > 0)
            {
                Agents.RunResult result;
                try
                {
                    result = Agents.Run(state.Path, tool.StatusArguments, 30000);
                }
                catch (Exception ex)
                {
                    state.Said = "It did not answer: " + ex.Message;
                    return state;
                }
                if (tool.ReadStatus != null) { tool.ReadStatus(result, state); }
                else { state.SignedIn = result.Code == 0 ? SignedIn.Yes : SignedIn.No; }
                return state;
            }
            // Only whether the tool's own sign-in file or key variable is there; without one the tool
            // may still answer (a key in its own settings, a model on this PC), so it stays unknown.
            bool there = tool.SignInFiles.Any(f => File.Exists(Environment.ExpandEnvironmentVariables(f))) ||
                tool.SignInVariables.Any(v => !string.IsNullOrEmpty(Variable(v)));
            if (there) { state.SignedIn = SignedIn.Yes; }
            return state;
        }

        private static string Variable(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }

        public static string SignInCommand(AiTool tool, string path)
        {
            return PseudoConsole.CommandLine(path, tool.SignInArguments ?? new string[0]);
        }

        public static string DeviceCommand(AiTool tool, string path)
        {
            return PseudoConsole.CommandLine(path, tool.DeviceArguments ?? new string[0]);
        }

        // winget, with the package's exact name and source; the person answers its questions, its
        // agreements among them, in the pseudoconsole.
        public static string InstallCommand(AiTool tool)
        {
            string winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(winget)) { winget = "winget.exe"; }
            return PseudoConsole.CommandLine(winget, new[] { "install", "--exact", "--id", tool.WingetId, "--source", "winget" });
        }

        // ---- reading answers ----

        // The JSON object in a tool's answer, or null.
        public static JsonObject JsonIn(string output)
        {
            string text = (output ?? "").Trim();
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) { return null; }
            try { return JsonNode.Parse(text.Substring(start, end - start + 1)) as JsonObject; } catch (JsonException) { return null; }
        }

        public static string Text(JsonNode node, string name)
        {
            var o = node as JsonObject;
            if (o == null) { return ""; }
            JsonNode value = o[name];
            return value != null && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
        }

        // The first text of that name anywhere in the answer.
        public static string FindText(JsonNode node, string name)
        {
            var o = node as JsonObject;
            if (o != null)
            {
                string here = Text(o, name);
                if (here.Length > 0) { return here; }
                foreach (KeyValuePair<string, JsonNode> pair in o)
                {
                    string inner = FindText(pair.Value, name);
                    if (inner.Length > 0) { return inner; }
                }
            }
            var a = node as JsonArray;
            if (a != null)
            {
                foreach (JsonNode item in a)
                {
                    string inner = FindText(item, name);
                    if (inner.Length > 0) { return inner; }
                }
            }
            return "";
        }

        public static string FirstLine(string text)
        {
            string line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
        }
    }
}
