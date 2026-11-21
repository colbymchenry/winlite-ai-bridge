// The bridge's protocols checked against recorded exchanges, with no tool and no account: what the
// bridge writes to each tool, and what it makes of each tool's answers. The recorded lines follow
// each protocol's documented shapes; the lab's fake tools (the closed app's tests\guest\fakes) answer
// the same way, and the owner's sign-in to each real tool is the hand check. Run from "--selftest".
//
// Under "--selftest" the caller's initialize is never made, so the server identity the recorded
// exchanges assume ("windows-lite" and its key variable) is set here as the fixture; at runtime the
// caller sets it (Bridge.Initialize).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WinLiteBridge
{
    internal static class AgentSelfTest
    {
        public static void Run(Action<string, bool, string> check)
        {
            // The sample server the recorded exchanges name. LookTool parsing, and every "the look's
            // server with the conversation's key" check, are against this name.
            Agents.ServerName = "windows-lite";
            Agents.ServerExe = @"C:\WinLiteLab\WinLiteShell.exe";
            Agents.KeyVariable = "WINLITE_LOOK_KEY";
            AgentSession.Instructions = "You are the assistant of a themed desktop. Use only the server's tools.";

            Names(check);
            Claude(check);
            Codex(check);
            Acp(check);
        }

        // What a session wrote, line by line, waited for: its answers to promises continue on other
        // threads.
        private sealed class Written
        {
            private readonly List<string> lines = new List<string>();

            public void Add(string line)
            {
                lock (lines)
                {
                    lines.Add(line);
                    Monitor.PulseAll(lines);
                }
            }

            public JsonObject At(int index)
            {
                lock (lines)
                {
                    DateTime until = DateTime.UtcNow.AddSeconds(3);
                    while (lines.Count <= index)
                    {
                        TimeSpan left = until - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) { return null; }
                        Monitor.Wait(lines, left);
                    }
                    return JsonNode.Parse(lines[index]) as JsonObject;
                }
            }

            public string All
            {
                get { lock (lines) { return string.Join(" | ", lines); } }
            }
        }

        private static JsonObject Parse(string json)
        {
            return JsonNode.Parse(json) as JsonObject;
        }

        private static bool IsFalse(JsonNode node)
        {
            return node != null && node.GetValueKind() == JsonValueKind.False;
        }

        private static string Id(JsonObject message)
        {
            return message == null || message["id"] == null ? "" : message["id"].ToJsonString();
        }

        private static void Codex(Action<string, bool, string> check)
        {
            JsonObject thread = CodexSession.ThreadParams("wlk_test", new[] { "mine", Agents.ServerName });
            var config = thread["config"] as JsonObject;
            string server = "mcp_servers." + Agents.ServerName + ".";
            check("Codex: read-only, with no approvals, in the empty folder", AiTools.Text(thread, "approvalPolicy") == "never" && AiTools.Text(thread, "sandbox") == "read-only" &&
                AiTools.Text(thread, "cwd") == AgentSession.WorkFolder && AiTools.Text(thread, "developerInstructions") == AgentSession.Instructions, thread.ToJsonString());
            check("Codex: the look's server with the conversation's key, its tools without a question", config != null && AiTools.Text(config, server + "command") == Agents.ServerExe &&
                AiTools.Text(config[server + "env"], Agents.KeyVariable) == "wlk_test" && AiTools.Text(config, server + "default_tools_approval_mode") == "approve", config == null ? "none" : config.ToJsonString());
            check("Codex: no shell, web search, image viewer or apps", config != null && IsFalse(config["features.shell_tool"]) && IsFalse(config["features.view_image"]) &&
                IsFalse(config["features.apps"]) && AiTools.Text(config, "web_search") == "disabled", config == null ? "none" : config.ToJsonString());
            check("Codex: the person's own servers off for the thread", config != null && IsFalse(config["mcp_servers.mine.enabled"]) && config[server + "enabled"] == null, config == null ? "none" : config.ToJsonString());
            check("Codex: its servers, from its settings", string.Join(",", CodexSession.ServerNames(Parse("{\"config\":{\"model\":\"x\",\"mcp_servers\":{\"a\":{},\"b\":{}}}}"))) == "a,b", "");
            check("Codex: a thread that is not read-only, or asks for approvals, is refused",
                CodexSession.Refused(Parse("{\"thread\":{\"id\":\"t\"},\"approvalPolicy\":\"never\",\"sandbox\":{\"type\":\"dangerFullAccess\"}}")) != null &&
                CodexSession.Refused(Parse("{\"thread\":{\"id\":\"t\"},\"approvalPolicy\":\"on-request\",\"sandbox\":{\"type\":\"readOnly\"}}")) != null &&
                CodexSession.Refused(Parse("{\"thread\":{\"id\":\"t\"},\"approvalPolicy\":\"never\",\"sandbox\":{\"type\":\"readOnly\",\"networkAccess\":false}}")) == null, "");

            AiTool codex = AiTools.Get("codex");
            string device = "Welcome to Codex [v0.171.0]\n1. Open this link in your browser and sign in to your account\n\x1b[94mhttps://auth.openai.com/codex/device\x1b[0m\n" +
                "2. Enter this one-time code (expires in 15 minutes)\n\x1b[94mABCD-12345\x1b[0m\n";
            string plain = SignInCodes.Plain(device);
            check("Codex: its device code and page", SignInCodes.CodeIn(plain, codex.DeviceCode) == "ABCD-12345" && codex.DevicePage.Match(plain).Value == "https://auth.openai.com/codex/device",
                SignInCodes.CodeIn(plain, codex.DeviceCode) ?? "none");

            var wire = new Written();
            var events = new List<AgentEvent>();
            var session = new CodexSession(codex, "wlk_test") { Wire = wire.Add };
            session.Event += events.Add;
            session.Started("thr_1");
            Task turn = session.SendAsync("Make it warm", CancellationToken.None);
            JsonObject start = wire.At(0);
            JsonObject startParams = start == null ? null : start["params"] as JsonObject;
            check("Codex: the person's words as turn/start, without a jsonrpc field", start != null && AiTools.Text(start, "method") == "turn/start" && start["jsonrpc"] == null &&
                AiTools.Text(startParams, "threadId") == "thr_1" && AiTools.Text(startParams?["input"]?[0], "text") == "Make it warm", wire.All);
            foreach (string line in new[]
            {
                "{\"id\":" + Id(start) + ",\"result\":{\"turn\":{\"id\":\"turn_1\",\"status\":\"inProgress\",\"items\":[],\"error\":null}}}",
                "{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thr_1\",\"turn\":{\"id\":\"turn_1\"}}}",
                "{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"itemId\":\"msg_1\",\"delta\":\"Warm \"}}",
                "{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"itemId\":\"msg_1\",\"delta\":\"it is.\"}}",
                "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"item\":{\"type\":\"agentMessage\",\"id\":\"msg_1\",\"text\":\"Warm it is.\"}}}",
                "{\"method\":\"item/started\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"item\":{\"type\":\"mcpToolCall\",\"id\":\"call_1\",\"server\":\"windows-lite\",\"tool\":\"apply_theme\",\"status\":\"inProgress\",\"arguments\":{}}}}",
                "{\"id\":7,\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"itemId\":\"x\",\"command\":\"dir\"}}",
                "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\",\"item\":{\"type\":\"mcpToolCall\",\"id\":\"call_1\",\"server\":\"windows-lite\",\"tool\":\"apply_theme\",\"status\":\"completed\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Applied.\"}]},\"error\":null}}}",
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thr_1\",\"turn\":{\"id\":\"turn_1\",\"status\":\"completed\",\"error\":null,\"items\":[]}}}",
            })
            {
                session.Feed(line);
            }
            string text = string.Concat(events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
            check("Codex: the answer's pieces, each once", text == "Warm it is.", text);
            AgentEvent finished = events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolFinished);
            check("Codex: a look tool used and finished", events.Any(e => e.Kind == AgentEventKind.ToolStarted && AgentSession.LookTool(e.ToolName) == "apply_theme") &&
                finished != null && finished.ToolId == "call_1" && !finished.Failed && finished.Text == "Applied.", string.Join(" | ", events.Select(e => e.Kind + ":" + e.ToolName + ":" + e.Text)));
            JsonObject declined = wire.At(1);
            check("Codex: a command it asks to run is declined", declined != null && Id(declined) == "7" && AiTools.Text(declined["result"], "decision") == "decline", wire.All);
            check("Codex: the turn ends when it says so", turn.Wait(3000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());

            session.Feed("{\"id\":8,\"method\":\"mcpServer/elicitation/request\",\"params\":{\"threadId\":\"thr_1\",\"serverName\":\"windows-lite\",\"mode\":\"form\",\"message\":\"Allow?\",\"requestedSchema\":{},\"_meta\":{\"codex_approval_kind\":\"mcp_tool_call\"}}}");
            session.Feed("{\"id\":9,\"method\":\"mcpServer/elicitation/request\",\"params\":{\"threadId\":\"thr_1\",\"serverName\":\"other\",\"mode\":\"form\",\"message\":\"Allow?\",\"requestedSchema\":{},\"_meta\":{\"codex_approval_kind\":\"mcp_tool_call\"}}}");
            JsonObject accepted = wire.At(2), refused = wire.At(3);
            check("Codex: only the look's tools are confirmed", accepted != null && AiTools.Text(accepted["result"], "action") == "accept" && refused != null && AiTools.Text(refused["result"], "action") == "decline", wire.All);

            turn = session.SendAsync("Stop soon", CancellationToken.None);
            JsonObject second = wire.At(4);
            session.Feed("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thr_1\",\"turn\":{\"id\":\"turn_2\"}}}");
            session.Interrupt();
            JsonObject stop = wire.At(5);
            check("Codex: Stop is its turn/interrupt", stop != null && AiTools.Text(stop, "method") == "turn/interrupt" && AiTools.Text(stop["params"], "turnId") == "turn_2" && AiTools.Text(stop["params"], "threadId") == "thr_1", wire.All);
            session.Feed("{\"id\":" + Id(second) + ",\"result\":{\"turn\":{\"id\":\"turn_2\",\"status\":\"inProgress\",\"items\":[],\"error\":null}}}");
            session.Feed("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thr_1\",\"turn\":{\"id\":\"turn_2\",\"status\":\"interrupted\",\"error\":null,\"items\":[]}}}");
            check("Codex: an interrupted turn ends quietly", turn.Wait(3000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());
        }

        private static void Acp(Action<string, bool, string> check)
        {
            JsonObject server = AcpSession.LookServer("wlk_test");
            check("ACP: the look's server as session/new gives it", AiTools.Text(server, "name") == Agents.ServerName && AiTools.Text(server, "command") == Agents.ServerExe &&
                AiTools.Text(server["env"]?[0], "name") == Agents.KeyVariable && AiTools.Text(server["env"]?[0], "value") == "wlk_test", server.ToJsonString());
            JsonObject gemini = Parse(AcpSession.GeminiSettingsJson("wlk_test"));
            JsonObject geminiServer = gemini?["mcpServers"]?[Agents.ServerName] as JsonObject;
            check("Gemini CLI: its built-in tools off, only the look's server, trusted", gemini?["tools"]?["core"] is JsonArray && ((JsonArray)gemini["tools"]["core"]).Count == 0 &&
                gemini["mcp"]?["allowed"]?[0]?.GetValue<string>() == Agents.ServerName && geminiServer != null && geminiServer["trust"]?.GetValueKind() == JsonValueKind.True &&
                AiTools.Text(geminiServer["env"], Agents.KeyVariable) == "wlk_test", gemini == null ? "none" : gemini.ToJsonString());

            JsonArray options = new JsonArray(
                new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" });
            check("ACP: a look tool by its title", AcpSession.LookCall(Parse("{\"toolCallId\":\"1\",\"title\":\"apply_theme (windows-lite MCP Server)\",\"kind\":\"other\"}"), options, "", ""), "");
            check("ACP: a look tool by OpenCode's name", AcpSession.LookCall(Parse("{\"toolCallId\":\"1\",\"title\":\"windows_lite_validate\"}"), options, "", ""), "");
            check("ACP: a command that names the server is no look tool", !AcpSession.LookCall(Parse("{\"toolCallId\":\"1\",\"title\":\"echo windows-lite\",\"kind\":\"execute\"}"), options, "", "") &&
                !AcpSession.LookCall(Parse("{\"toolCallId\":\"1\",\"title\":\"apply_theme (windows-lite MCP Server)\",\"kind\":\"edit\"}"), options, "", "") &&
                !AcpSession.LookCall(Parse("{\"toolCallId\":\"1\",\"title\":\"del (windows-lite MCP Server)\",\"kind\":\"other\"}"), options, "", ""), "");
            check("ACP: the server named by the agent's own options", AcpSession.LookCall(Parse("{\"toolCallId\":\"1\"}"),
                new JsonArray(new JsonObject { ["optionId"] = "server", ["name"] = "Always allow windows-lite", ["kind"] = "allow_always" }), "", "other"), "");
            check("ACP: allowed once, refused once", AcpSession.Choose(options, true) == "allow" && AcpSession.Choose(options, false) == "reject" && AcpSession.Choose(new JsonArray(), true) == null, "");

            var tool = new AiTool { Id = "lab-acp", Name = "Lab agent", Maker = "the lab", Protocol = AgentProtocol.Acp };
            var wire = new Written();
            var events = new List<AgentEvent>();
            var session = new AcpSession(tool, "wlk_test") { Wire = wire.Add };
            session.Event += events.Add;
            session.Started("sess_1");
            Task turn = session.SendAsync("Make it calm", CancellationToken.None);
            JsonObject prompt = wire.At(0);
            JsonArray blocks = prompt?["params"]?["prompt"] as JsonArray;
            check("ACP: the person's words as session/prompt, the instructions first", prompt != null && AiTools.Text(prompt, "jsonrpc") == "2.0" && AiTools.Text(prompt, "method") == "session/prompt" &&
                AiTools.Text(prompt["params"], "sessionId") == "sess_1" && blocks != null && blocks.Count == 2 && AiTools.Text(blocks[0], "text").EndsWith(AgentSession.Instructions, StringComparison.Ordinal) &&
                AiTools.Text(blocks[1], "text") == "Make it calm", wire.All);
            foreach (string line in new[]
            {
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"sess_1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Calm \"}}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"sess_1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"it is.\"}}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"sess_1\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_1\",\"title\":\"apply_theme (windows-lite MCP Server)\",\"kind\":\"other\",\"status\":\"pending\"}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"sess_1\",\"toolCall\":{\"toolCallId\":\"call_1\"},\"options\":[{\"optionId\":\"proceed_once\",\"name\":\"Allow\",\"kind\":\"allow_once\"},{\"optionId\":\"cancel\",\"name\":\"Reject\",\"kind\":\"reject_once\"}]}}",
                "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"sess_1\",\"toolCall\":{\"toolCallId\":\"call_2\",\"title\":\"Writing to notes.txt\",\"kind\":\"edit\"},\"options\":[{\"optionId\":\"proceed_once\",\"name\":\"Allow\",\"kind\":\"allow_once\"},{\"optionId\":\"cancel\",\"name\":\"Reject\",\"kind\":\"reject_once\"}]}}",
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fs/read_text_file\",\"params\":{\"sessionId\":\"sess_1\",\"path\":\"C:\\\\Users\\\\x\\\\notes.txt\"}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"sess_1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call_1\",\"status\":\"completed\",\"content\":[{\"type\":\"content\",\"content\":{\"type\":\"text\",\"text\":\"Applied.\"}}]}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":" + Id(prompt) + ",\"result\":{\"stopReason\":\"end_turn\"}}",
            })
            {
                session.Feed(line);
            }
            string text = string.Concat(events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
            check("ACP: the answer's pieces", text == "Calm it is.", text);
            AgentEvent finished = events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolFinished);
            check("ACP: a look tool used and finished", finished != null && finished.ToolId == "call_1" && AgentSession.LookTool(finished.ToolName) == "apply_theme" && !finished.Failed && finished.Text == "Applied.",
                string.Join(" | ", events.Select(e => e.Kind + ":" + e.ToolName + ":" + e.Text)));
            JsonObject allowed = wire.At(1), rejected = wire.At(2), refusedFile = wire.At(3);
            check("ACP: the look's tool allowed once, a file edit refused", allowed != null && Id(allowed) == "5" && AiTools.Text(allowed["result"]?["outcome"], "optionId") == "proceed_once" &&
                rejected != null && Id(rejected) == "6" && AiTools.Text(rejected["result"]?["outcome"], "optionId") == "cancel", wire.All);
            check("ACP: no file system offered, so a read is refused", refusedFile != null && Id(refusedFile) == "7" && refusedFile["error"] != null, wire.All);
            check("ACP: the turn ends with its stop reason", turn.Wait(3000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());

            turn = session.SendAsync("Again", CancellationToken.None);
            JsonObject again = wire.At(4);
            JsonArray againBlocks = again?["params"]?["prompt"] as JsonArray;
            check("ACP: the instructions only once", againBlocks != null && againBlocks.Count == 1 && AiTools.Text(againBlocks[0], "text") == "Again", wire.All);
            session.Interrupt();
            JsonObject cancel = wire.At(5);
            check("ACP: Stop is session/cancel", cancel != null && AiTools.Text(cancel, "method") == "session/cancel" && cancel["id"] == null && AiTools.Text(cancel["params"], "sessionId") == "sess_1", wire.All);
            session.Feed("{\"jsonrpc\":\"2.0\",\"id\":" + Id(again) + ",\"result\":{\"stopReason\":\"cancelled\"}}");
            check("ACP: a cancelled turn ends quietly", turn.Wait(3000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());

            turn = session.SendAsync("Once more", CancellationToken.None);
            JsonObject third = wire.At(6);
            session.Feed("{\"jsonrpc\":\"2.0\",\"id\":" + Id(third) + ",\"error\":{\"code\":-32000,\"message\":\"Authentication required\"}}");
            string problem = "";
            try { turn.Wait(3000); } catch (AggregateException ex) { problem = ex.GetBaseException().Message; }
            check("ACP: not signed in, in words", problem == "Lab agent is not signed in. Sign in from Quick Search Settings, under AI.", problem);
        }

        private static void Names(Action<string, bool, string> check)
        {
            check("a look tool's name from Claude Code", AgentSession.LookTool("mcp__windows-lite__apply_theme") == "apply_theme", AgentSession.LookTool("mcp__windows-lite__apply_theme"));
            check("a look tool's name from a title", AgentSession.LookTool("validate (windows-lite MCP Server)") == "validate", AgentSession.LookTool("validate (windows-lite MCP Server)"));
            check("another tool's name stays", AgentSession.LookTool("Bash") == "Bash", AgentSession.LookTool("Bash"));
            check("an empty argument on a command line", AgentSession.Argument("") == "\"\"", AgentSession.Argument(""));
            check("an argument with spaces and quotes", AgentSession.Argument("a \"b\" c\\") == "\"a \\\"b\\\" c\\\\\"", AgentSession.Argument("a \"b\" c\\"));
            check("a plain argument", AgentSession.Argument("--verbose") == "--verbose", AgentSession.Argument("--verbose"));
        }

        private static void Claude(Action<string, bool, string> check)
        {
            string[] arguments = ClaudeSession.Arguments(@"C:\x\claude.json");
            string joined = string.Join(" ", arguments.Select(AgentSession.Argument));
            check("Claude Code: built-in tools off", joined.Contains("--tools \"\""), joined);
            check("Claude Code: only the look's server, its tools allowed", joined.Contains("--strict-mcp-config") && joined.Contains("--allowedTools mcp__windows-lite") && joined.Contains("--mcp-config C:\\x\\claude.json"), joined);
            check("Claude Code: streaming JSON both ways", joined.Contains("--input-format stream-json") && joined.Contains("--output-format stream-json --verbose"), joined);
            check("Claude Code: no key on the command line", !joined.Contains("wlk_"), joined);

            JsonObject config = JsonNode.Parse(ClaudeSession.Config("wlk_test")) as JsonObject;
            JsonObject server = config == null ? null : config["mcpServers"]?[Agents.ServerName] as JsonObject;
            check("Claude Code: the look's server with the conversation's key", server != null && AiTools.Text(server, "command") == Agents.ServerExe &&
                server["args"]?[0]?.GetValue<string>() == "mcp" && AiTools.Text(server["env"], Agents.KeyVariable) == "wlk_test", config == null ? "none" : config.ToJsonString());

            var tool = AiTools.Get("claude-code");
            var written = new List<string>();
            var events = new List<AgentEvent>();
            var session = new ClaudeSession(tool, "wlk_test") { Wire = written.Add };
            session.Event += events.Add;
            Task turn = session.SendAsync("Make the accent sea green", CancellationToken.None);
            JsonObject sent = written.Count == 1 ? JsonNode.Parse(written[0]) as JsonObject : null;
            check("Claude Code: the person's words as a user message", sent != null && AiTools.Text(sent, "type") == "user" && AiTools.Text(sent["message"], "content") == "Make the accent sea green", written.Count == 1 ? written[0] : written.Count + " lines");

            foreach (string line in new[]
            {
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"tools\":[\"mcp__windows-lite__get_guide\"],\"mcp_servers\":[{\"name\":\"windows-lite\",\"status\":\"connected\"}]}",
                "{\"type\":\"stream_event\",\"parent_tool_use_id\":null,\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}}",
                "{\"type\":\"stream_event\",\"parent_tool_use_id\":null,\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Sea \"}}}",
                "{\"type\":\"stream_event\",\"parent_tool_use_id\":null,\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"green it is.\"}}}",
                "{\"type\":\"assistant\",\"parent_tool_use_id\":null,\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Sea green it is.\"}]}}",
                "{\"type\":\"assistant\",\"parent_tool_use_id\":null,\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"mcp__windows-lite__apply_theme\",\"input\":{\"theme\":{}}}]}}",
                "{\"type\":\"user\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"content\":[{\"type\":\"text\",\"text\":\"Applied.\"}],\"is_error\":false}]}}",
                "{\"type\":\"assistant\",\"parent_tool_use_id\":null,\"message\":{\"id\":\"msg_2\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}",
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Done.\",\"session_id\":\"s1\"}",
            })
            {
                session.Feed(line);
            }
            string text = string.Concat(events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
            check("Claude Code: the answer's pieces, each once", text == "Sea green it is.Done.", text);
            AgentEvent started = events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolStarted);
            AgentEvent finished = events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolFinished);
            check("Claude Code: a look tool used and finished", started != null && AgentSession.LookTool(started.ToolName) == "apply_theme" && finished != null && finished.ToolId == "toolu_1" && !finished.Failed && finished.Text == "Applied.",
                string.Join(" | ", events.Select(e => e.Kind + ":" + e.ToolName + ":" + e.Text)));
            check("Claude Code: the turn ends with its result", turn.Wait(2000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());

            turn = session.SendAsync("Again", CancellationToken.None);
            session.Feed("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Not logged in - Please run /login\",\"session_id\":\"s1\"}");
            string problem = "";
            try { turn.Wait(2000); } catch (AggregateException ex) { problem = ex.GetBaseException().Message; }
            check("Claude Code: not signed in, in words", problem.StartsWith("Claude Code is not signed in.", StringComparison.Ordinal), problem);

            written.Clear();
            session.Feed("{\"type\":\"control_request\",\"request_id\":\"r9\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{\"command\":\"dir\"}}}");
            session.Feed("{\"type\":\"control_request\",\"request_id\":\"r10\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"mcp__windows-lite__validate\",\"input\":{\"theme\":{\"schema\":1}}}}");
            string answers = string.Join(" ", written);
            check("Claude Code: other tools refused, the look's allowed", written.Count == 2 && written[0].Contains("\"behavior\":\"deny\"") && written[0].Contains("\"request_id\":\"r9\"") &&
                written[1].Contains("\"behavior\":\"allow\"") && written[1].Contains("\"updatedInput\":{\"theme\":{\"schema\":1}}"), answers);

            written.Clear();
            turn = session.SendAsync("Stop soon", CancellationToken.None);
            session.Interrupt();
            JsonObject stop = written.Count == 2 ? JsonNode.Parse(written[1]) as JsonObject : null;
            string stopId = stop == null ? "" : AiTools.Text(stop, "request_id");
            check("Claude Code: Stop is its interrupt", stop != null && AiTools.Text(stop, "type") == "control_request" && AiTools.Text(stop["request"], "subtype") == "interrupt", string.Join(" ", written));
            session.Feed("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + stopId + "\",\"response\":{}}}");
            session.Feed("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"session_id\":\"s1\"}");
            check("Claude Code: an interrupted turn ends quietly", turn.Wait(2000) && turn.Status == TaskStatus.RanToCompletion, turn.Status.ToString());
        }
    }
}
