// The bridge's protocol: JSON-RPC 2.0 on standard input and output, one message per line. The caller
// (Windows Lite's closed app, or anything else) starts this program and drives it. The bridge finds
// the AI tools, runs their own sign-ins in a pseudoconsole, and holds a conversation with a tool over
// the tool's own protocol, offering it the one MCP server the caller names. See README.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WinLiteBridge
{
    internal static class Bridge
    {
        private static readonly object OutGate = new object();
        private static TextWriter Out;
        private static readonly Dictionary<string, AgentSession> Sessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        private static readonly Dictionary<string, CancellationTokenSource> Cancels = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private static PseudoConsole signin;
        private static int nextSession;

        public static int Run(string[] args)
        {
            if (args != null && args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                return SelfTest();
            }

            AiTools.UseFreshPath();
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            Log.Write("Bridge " + typeof(Bridge).Assembly.GetName().Version + " started.");
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) { continue; }
                try { Dispatch(line); }
                catch (Exception ex) { Log.Write("Bridge: " + ex.Message); }
            }
            return 0;
        }

        private static void Dispatch(string line)
        {
            JsonObject message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { return; }
            if (message == null) { return; }
            JsonNode id = message.ContainsKey("id") ? message["id"] : null;
            string method = Str(message, "method");
            JsonObject p = message["params"] as JsonObject ?? new JsonObject();
            switch (method)
            {
                case "initialize": Reply(id, Initialize(p)); break;
                case "tools/list": Reply(id, ToolsList()); break;
                case "tools/signin": SignIn(id, p); break;
                case "tools/install": Install(id, p); break;
                case "terminal/input": TerminalInput(p); Reply(id, new JsonObject()); break;
                case "terminal/resize": TerminalResize(p); Reply(id, new JsonObject()); break;
                case "terminal/stop": StopSignIn(); Reply(id, new JsonObject()); break;
                case "session/new": NewSession(id, p); break;
                case "session/prompt": Prompt(id, p); break;
                case "session/cancel": Cancel(p); Reply(id, new JsonObject()); break;
                case "shutdown": Reply(id, new JsonObject()); CloseAll(); Environment.Exit(0); break;
                default: ReplyError(id, -32601, "Unknown method: " + method); break;
            }
        }

        // ---- initialize ----

        private static JsonObject Initialize(JsonObject p)
        {
            // The caller names its one MCP server (the only look-specific knowledge the bridge holds).
            JsonObject mcp = p["mcpServer"] as JsonObject;
            if (mcp != null)
            {
                Agents.ServerName = Str(mcp, "name");
                Agents.ServerExe = Str(mcp, "command");
                Agents.KeyVariable = Str(mcp, "keyVariable");
                string instructions = Str(mcp, "instructions");
                if (instructions.Length > 0) { AgentSession.Instructions = instructions; }
            }
            return new JsonObject
            {
                ["name"] = "WinLiteBridge",
                ["version"] = (typeof(Bridge).Assembly.GetName().Version ?? new Version(0, 1)).ToString(),
                ["protocol"] = 1,
                ["capabilities"] = new JsonObject { ["signin"] = true, ["sessions"] = true },
            };
        }

        // ---- tools/list ----

        private static JsonObject ToolsList()
        {
            var tools = new JsonArray();
            foreach (AiTool tool in AiTools.All)
            {
                ToolState state;
                try { state = AiTools.State(tool); }
                catch (Exception ex) { state = new ToolState { Said = ex.Message }; }
                tools.Add(new JsonObject
                {
                    ["id"] = tool.Id,
                    ["name"] = tool.Name,
                    ["maker"] = tool.Maker,
                    ["accounts"] = tool.Accounts,
                    ["protocol"] = tool.Protocol.ToString(),
                    ["installed"] = state.Path != null,
                    ["signedIn"] = state.SignedIn.ToString(),
                    ["who"] = state.Who ?? "",
                    ["note"] = state.Said ?? "",
                    // The caller's display needs these too (they describe the tool, not the look), so
                    // the caller holds no catalogue of its own: where it goes, how it is installed, its
                    // limits in a conversation, and the shapes of its own device sign-in.
                    ["destination"] = AiTools.DestinationOf(tool),
                    ["installPage"] = tool.InstallPage ?? "",
                    ["wingetId"] = tool.WingetId ?? "",
                    ["limits"] = tool.Limits ?? "",
                    ["signInNote"] = tool.SignInNote ?? "",
                    ["hasSignIn"] = tool.SignInArguments != null,
                    ["hasDevice"] = tool.DeviceArguments != null && tool.DeviceArguments.Length > 0,
                    ["deviceCode"] = tool.DeviceCode != null ? tool.DeviceCode.ToString() : "",
                    ["devicePage"] = tool.DevicePage != null ? tool.DevicePage.ToString() : "",
                });
            }
            return new JsonObject { ["tools"] = tools };
        }

        // ---- sign-in, in a pseudoconsole ----

        private static void SignIn(JsonNode id, JsonObject p)
        {
            StopSignIn();
            AiTool tool = AiTools.Get(Str(p, "toolId"));
            if (tool == null) { ReplyError(id, 1, "No such tool."); return; }
            string path = AiTools.Find(tool);
            if (path == null) { ReplyError(id, 2, tool.Name + " is not installed."); return; }
            bool device = Bool(p, "device");
            string command = device ? AiTools.DeviceCommand(tool, path) : AiTools.SignInCommand(tool, path);
            if (string.IsNullOrEmpty(command)) { ReplyError(id, 3, tool.Name + " has no sign-in of its own."); return; }
            int columns = (int)Num(p, "columns", 100);
            int rows = (int)Num(p, "rows", 30);
            var console = new PseudoConsole();
            console.Output += text => Notify("terminal/output", new JsonObject { ["text"] = text });
            console.Exited += code => Notify("terminal/exited", new JsonObject { ["code"] = code });
            try { console.Start(command, Path.GetDirectoryName(path) ?? Environment.CurrentDirectory, columns, rows); }
            catch (Exception ex) { ReplyError(id, 4, "Could not start the sign-in: " + ex.Message); return; }
            signin = console;
            Reply(id, new JsonObject { ["started"] = true, ["note"] = tool.SignInNote ?? "" });
        }

        private static void TerminalInput(JsonObject p)
        {
            PseudoConsole console = signin;
            if (console != null) { console.Write(Str(p, "text")); }
        }

        private static void TerminalResize(JsonObject p)
        {
            PseudoConsole console = signin;
            if (console != null) { console.Resize((int)Num(p, "columns", 100), (int)Num(p, "rows", 30)); }
        }

        // A missing tool's install through winget, in a pseudoconsole like a sign-in: winget runs
        // unmodified and the person answers its questions. The relay is the same (terminal/output,
        // terminal/input, terminal/exited, terminal/stop).
        private static void Install(JsonNode id, JsonObject p)
        {
            StopSignIn();
            AiTool tool = AiTools.Get(Str(p, "toolId"));
            if (tool == null) { ReplyError(id, 1, "No such tool."); return; }
            if (string.IsNullOrEmpty(tool.WingetId)) { ReplyError(id, 2, tool.Name + " has no winget package."); return; }
            string command = AiTools.InstallCommand(tool);
            int columns = (int)Num(p, "columns", 100);
            int rows = (int)Num(p, "rows", 30);
            var console = new PseudoConsole();
            console.Output += text => Notify("terminal/output", new JsonObject { ["text"] = text });
            console.Exited += code => Notify("terminal/exited", new JsonObject { ["code"] = code });
            try { console.Start(command, Paths.Base, columns, rows); }
            catch (Exception ex) { ReplyError(id, 4, "Could not start the install: " + ex.Message); return; }
            signin = console;
            Reply(id, new JsonObject { ["started"] = true, ["note"] = "" });
        }

        private static void StopSignIn()
        {
            PseudoConsole console = signin;
            signin = null;
            if (console != null) { try { console.Dispose(); } catch (Exception) { } }
        }

        // ---- a conversation with a tool ----

        private static void NewSession(JsonNode id, JsonObject p)
        {
            AiTool tool = AiTools.Get(Str(p, "toolId"));
            if (tool == null) { ReplyError(id, 1, "No such tool."); return; }
            string key = Str(p, "key");
            AgentSession session;
            try { session = AgentSession.Create(tool, key); }
            catch (Exception ex) { ReplyError(id, 2, ex.Message); return; }
            string sid = "s" + Interlocked.Increment(ref nextSession).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (Sessions) { Sessions[sid] = session; }
            Task.Run(async () =>
            {
                try
                {
                    await session.StartAsync(CancellationToken.None);
                    Reply(id, new JsonObject { ["sessionId"] = sid });
                }
                catch (Exception ex)
                {
                    lock (Sessions) { Sessions.Remove(sid); }
                    try { session.Dispose(); } catch (Exception) { }
                    ReplyError(id, 3, ex.Message);
                }
            });
        }

        private static void Prompt(JsonNode id, JsonObject p)
        {
            string sid = Str(p, "sessionId");
            AgentSession session;
            lock (Sessions) { Sessions.TryGetValue(sid, out session); }
            if (session == null) { ReplyError(id, 1, "No such session."); return; }
            string text = Str(p, "text");
            Action<AgentEvent> handler = e => Notify("session/update", Update(sid, e));
            var cancel = new CancellationTokenSource();
            lock (Cancels) { Cancels[sid] = cancel; }
            session.Event += handler;
            Task.Run(async () =>
            {
                try
                {
                    await session.SendAsync(text, cancel.Token);
                    Reply(id, new JsonObject { ["sessionId"] = sid, ["stopReason"] = "end_turn" });
                }
                catch (OperationCanceledException)
                {
                    Reply(id, new JsonObject { ["sessionId"] = sid, ["stopReason"] = "cancelled" });
                }
                catch (Exception ex)
                {
                    ReplyError(id, 2, ex.Message);
                }
                finally
                {
                    session.Event -= handler;
                    lock (Cancels) { Cancels.Remove(sid); }
                }
            });
        }

        private static void Cancel(JsonObject p)
        {
            string sid = Str(p, "sessionId");
            AgentSession session;
            lock (Sessions) { Sessions.TryGetValue(sid, out session); }
            CancellationTokenSource cancel;
            lock (Cancels) { Cancels.TryGetValue(sid, out cancel); }
            if (cancel != null) { try { cancel.Cancel(); } catch (Exception) { } }
            if (session != null) { try { session.Interrupt(); } catch (Exception) { } }
        }

        private static JsonObject Update(string sid, AgentEvent e)
        {
            return new JsonObject
            {
                ["sessionId"] = sid,
                ["kind"] = e.Kind.ToString(),
                ["text"] = e.Text ?? "",
                ["toolId"] = e.ToolId ?? "",
                ["toolName"] = e.ToolName ?? "",
                ["failed"] = e.Failed,
            };
        }

        private static void CloseAll()
        {
            StopSignIn();
            lock (Sessions)
            {
                foreach (AgentSession session in Sessions.Values) { try { session.Dispose(); } catch (Exception) { } }
                Sessions.Clear();
            }
        }

        // ---- the self-test (offline): the extracted open part lists the tools it finds, then checks
        // each protocol against recorded exchanges with no tool and no account (AgentSelfTest) ----

        private static int SelfTest()
        {
            AiTools.UseFreshPath();
            Console.WriteLine("WinLiteBridge self-test");
            foreach (AiTool tool in AiTools.All)
            {
                ToolState state;
                try { state = AiTools.State(tool); }
                catch (Exception ex) { Console.WriteLine("TOOL " + tool.Id + " error " + ex.Message); continue; }
                Console.WriteLine("TOOL " + tool.Id + " name=" + tool.Name + " protocol=" + tool.Protocol +
                    " installed=" + (state.Path != null) + " signedIn=" + state.SignedIn);
            }
            Console.WriteLine("TOOLS " + AiTools.All.Length);

            int passed = 0, failed = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : ": " + detail));
                if (ok) { passed++; } else { failed++; }
            };
            try { AgentSelfTest.Run(check); }
            catch (Exception ex) { Console.WriteLine("FAIL self-test threw: " + ex.Message); failed++; }
            Console.WriteLine("SELFTEST passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }

        // ---- JSON-RPC plumbing ----

        private static void Reply(JsonNode id, JsonObject result)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });
        }

        private static void ReplyError(JsonNode id, int code, string message)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
        }

        private static void Notify(string method, JsonObject p)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });
        }

        private static void Send(JsonObject message)
        {
            if (Out == null) { return; }
            string text = message.ToJsonString();
            lock (OutGate)
            {
                Out.Write(text);
                Out.Write('\n');
                Out.Flush();
            }
        }

        private static string Str(JsonObject o, string name)
        {
            return o != null && o.ContainsKey(name) && o[name] != null ? o[name].ToString() : "";
        }

        private static bool Bool(JsonObject o, string name)
        {
            try { return o != null && o.ContainsKey(name) && o[name] != null && o[name].GetValue<bool>(); }
            catch (Exception) { return false; }
        }

        private static double Num(JsonObject o, string name, double fallback)
        {
            try { return o != null && o.ContainsKey(name) && o[name] != null ? o[name].GetValue<double>() : fallback; }
            catch (Exception) { return fallback; }
        }
    }
}
