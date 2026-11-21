// L7: the assistant's conversations with the AI tools (docs/10-customization.md, "Connecting an AI",
// way 2), each over the tool's own documented protocol, as T3 Code does it: Claude Code's streaming
// JSON (the protocol Anthropic's Agent SDK uses), Codex's app server, and the Agent Client Protocol.
// The tool runs unmodified, signed in as the person, in an empty folder of the look's data, with the
// Look API as its only MCP server and only the Look API's tools switched on where the tool allows it.
// The conversation's own key (WINLITE_LOOK_KEY) signs its changes, and a job ends the whole process
// tree with the conversation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WinLiteBridge
{
    internal enum AgentEventKind { Text, ToolStarted, ToolFinished, Note }

    internal sealed class AgentEvent
    {
        public AgentEventKind Kind;
        public string Text = "";
        public string ToolId = "";
        public string ToolName = "";
        public bool Failed;
    }

    internal abstract class AgentSession : IDisposable
    {
        // What every tool is told, after its own instructions, set by the caller (initialize). The
        // bridge keeps none of its own: the caller provides the system prompt for its MCP server. The
        // default only tells the tool to use the one server it is given, with no look knowledge.
        public static string Instructions =
            "You make changes only through the tools of the one MCP server you are given. " +
            "You have no other tools here and nothing of this PC to read, so never ask for files, commands or settings. " +
            "Answer briefly, in the person's language, and say in one sentence what you changed.";

        protected readonly AiTool Tool;
        protected readonly string Key;
        private Process process;
        private ProcessJob job;
        private Stream input;
        private readonly object writeGate = new object();
        private readonly StringBuilder errors = new StringBuilder();
        private TaskCompletionSource<bool> turnDone;
        private bool interrupted;
        private volatile bool disposed;

        // The tool's text and its use of the look's tools, as they come (on a background thread).
        public event Action<AgentEvent> Event;

        // For the self-test: what would be written to the tool, instead of a process.
        internal Action<string> Wire;

        protected AgentSession(AiTool tool, string key)
        {
            Tool = tool;
            Key = key;
        }

        public static AgentSession Create(AiTool tool, string key)
        {
            switch (tool.Protocol)
            {
                case AgentProtocol.Claude: return new ClaudeSession(tool, key);
                case AgentProtocol.Codex: return new CodexSession(tool, key);
                default: return new AcpSession(tool, key);
            }
        }

        // The Look API's tools (Look/McpServer.cs), by their own names.
        public static readonly HashSet<string> LookToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_guide", "get_look", "get_schema", "list_fonts", "list_themes", "validate", "render_preview",
            "try_theme", "apply_theme", "undo", "restore", "reset", "save_theme", "export_theme", "import_theme",
        };

        // The app's version, as each protocol's client information gives it.
        protected static string Version
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        // The empty folder each conversation runs in: nothing of the person's to read.
        public static string WorkFolder { get { return Paths.Folder("Assistant"); } }

        // Starts the tool and makes the protocol's opening handshake.
        public abstract Task StartAsync(CancellationToken cancel);

        // Sends the person's words, and returns when the tool's turn is over.
        public abstract Task SendAsync(string text, CancellationToken cancel);

        // Asks the tool to stop its turn, as its own Stop does.
        public void Interrupt()
        {
            if (turnDone == null || interrupted) { return; }
            interrupted = true;
            try { SendInterrupt(); } catch (Exception ex) { Log.Write("assistant: interrupt: " + ex.Message); }
        }

        protected abstract void SendInterrupt();

        // One message from the tool.
        protected abstract void Receive(JsonObject message);

        // The look's tool's own name, from the name the tool gives it ("mcp__windows-lite__validate",
        // "windows-lite.validate", "validate (windows-lite MCP Server)").
        public static string LookTool(string name)
        {
            string text = (name ?? "").Trim();
            string underscored = Agents.ServerName.Replace('-', '_');
            foreach (string prefix in new[]
            {
                "mcp__" + Agents.ServerName + "__", "mcp__" + underscored + "__", Agents.ServerName + "__", Agents.ServerName + ".", Agents.ServerName + "/",
                Agents.ServerName + ":", Agents.ServerName + "_", underscored + "_",
            })
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return text.Substring(prefix.Length); }
            }
            int bracket = text.IndexOf(" (", StringComparison.Ordinal);
            if (bracket > 0 && text.IndexOf(Agents.ServerName, bracket, StringComparison.OrdinalIgnoreCase) > 0) { return text.Substring(0, bracket); }
            return text;
        }

        // A file of the conversation's own, for a tool that reads the look's server from one (the key
        // stays off command lines). Earlier conversations' files of that kind go first; their keys
        // were cut off when those conversations ended.
        protected static string KeyFile(string prefix)
        {
            string folder = Paths.Folder("AssistantKeys");
            foreach (string old in Directory.GetFiles(folder, prefix + "-*.json"))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return System.IO.Path.Combine(folder, prefix + "-" + Guid.NewGuid().ToString("N") + ".json");
        }

        // ---- the tool's process ----

        protected void StartProcess(string[] arguments, IDictionary<string, string> environment)
        {
            AiTools.UseFreshPath();
            string path = AiTools.Find(Tool);
            if (path == null) { throw new InvalidOperationException(Tool.Name + " is not on this PC any more."); }
            string program = AiTools.Program(Tool, path);
            string line = string.Join(" ", arguments.Select(Argument));
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = WorkFolder,
            };
            if (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
                info.Arguments = "/d /s /c \"" + Argument(program) + " " + line + "\"";
            }
            else
            {
                info.FileName = program;
                info.Arguments = line;
            }
            // Set by Claude Code in what it starts; a conversation is not one of those.
            info.EnvironmentVariables.Remove("CLAUDECODE");
            foreach (KeyValuePair<string, string> pair in environment) { info.EnvironmentVariables[pair.Key] = pair.Value; }
            try
            {
                process = Process.Start(info);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(Tool.Name + " could not be started: " + ex.Message, ex);
            }
            job = new ProcessJob();
            job.Add(process);
            input = process.StandardInput.BaseStream;
            new Thread(ReadOutput) { IsBackground = true, Name = "assistant output" }.Start();
            new Thread(ReadErrors) { IsBackground = true, Name = "assistant errors" }.Start();
        }

        // An argument as Windows programs read their command line.
        public static string Argument(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) { return value; }
            var text = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { text.Append('\\', slashes * 2 + 1).Append('"'); }
                else { text.Append('\\', slashes).Append(c); }
                slashes = 0;
            }
            text.Append('\\', slashes * 2).Append('"');
            return text.ToString();
        }

        private void ReadOutput()
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null) { Feed(line); }
            }
            catch (Exception ex)
            {
                if (!disposed) { Log.Write("assistant: reading " + Tool.Name + ": " + ex.Message); }
            }
            Ended();
        }

        private void ReadErrors()
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    lock (errors)
                    {
                        errors.AppendLine(line);
                        if (errors.Length > 4000) { errors.Remove(0, errors.Length - 4000); }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // One line from the tool (or from the self-test).
        internal void Feed(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) { return; }
            JsonObject message;
            try
            {
                message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject;
            }
            catch (JsonException)
            {
                Log.Write("assistant: " + Tool.Name + " wrote a line that is not JSON: " + AiTools.FirstLine(line));
                return;
            }
            if (message == null) { return; }
            try
            {
                Receive(message);
            }
            catch (Exception ex)
            {
                Log.Write("assistant: " + Tool.Name + ": " + ex.Message);
            }
        }

        private void Ended()
        {
            if (disposed) { return; }
            string said;
            lock (errors) { said = errors.ToString().Trim(); }
            string last = said.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            string why = Tool.Name + " stopped." + (last.Length > 0 ? " It said: " + (last.Length > 300 ? last.Substring(0, 300) + "..." : last) : "");
            EndTurn(new InvalidOperationException(why));
            Failed(why);
        }

        // Every waiting request fails with the tool's end.
        protected virtual void Failed(string why)
        {
        }

        protected void Write(JsonNode message)
        {
            string text = message.ToJsonString();
            Action<string> wire = Wire;
            if (wire != null) { wire(text); return; }
            if (input == null || disposed) { throw new InvalidOperationException(Tool.Name + " is not running."); }
            byte[] bytes = new UTF8Encoding(false).GetBytes(text + "\n");
            lock (writeGate)
            {
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }

        protected void Raise(AgentEventKind kind, string text, string toolId, string toolName, bool failed)
        {
            Action<AgentEvent> handler = Event;
            if (handler != null) { handler(new AgentEvent { Kind = kind, Text = text ?? "", ToolId = toolId ?? "", ToolName = toolName ?? "", Failed = failed }); }
        }

        // ---- turns ----

        protected bool TurnOpen { get { return turnDone != null; } }

        protected bool WasInterrupted { get { return interrupted; } }

        protected Task BeginTurn()
        {
            if (turnDone != null) { throw new InvalidOperationException(Tool.Name + " is still answering."); }
            interrupted = false;
            turnDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return turnDone.Task;
        }

        // The turn is over: well, or with what went wrong.
        protected void EndTurn(Exception problem)
        {
            TaskCompletionSource<bool> done = turnDone;
            if (done == null) { return; }
            turnDone = null;
            if (problem == null) { done.TrySetResult(true); } else { done.TrySetException(problem); }
        }

        // Waits for the turn; a stop by the person gives the tool a moment to end its turn, as
        // its own Stop does, and ends the conversation if it does not.
        protected async Task Await(Task done, CancellationToken cancel)
        {
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancel.Register(() => stopped.TrySetResult(true)))
            {
                if (await Task.WhenAny(done, stopped.Task).ConfigureAwait(false) == done)
                {
                    await done.ConfigureAwait(false);
                    return;
                }
            }
            Interrupt();
            if (await Task.WhenAny(done, Task.Delay(10000)).ConfigureAwait(false) != done)
            {
                EndTurn(null);
                Dispose();
            }
            throw new OperationCanceledException(cancel);
        }

        protected static async Task<T> Within<T>(Task<T> task, int milliseconds, CancellationToken cancel, string late)
        {
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancel.Register(() => stopped.TrySetResult(true)))
            {
                Task first = await Task.WhenAny(task, stopped.Task, Task.Delay(milliseconds)).ConfigureAwait(false);
                if (first == task) { return await task.ConfigureAwait(false); }
                if (first == stopped.Task) { throw new OperationCanceledException(cancel); }
                throw new TimeoutException(late);
            }
        }

        public virtual void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            EndTurn(new InvalidOperationException("The conversation ended."));
            if (process != null)
            {
                try { input.Close(); } catch (Exception) { }
                try { process.WaitForExit(2000); } catch (Exception) { }
                try { if (!process.HasExited) { process.Kill(); } } catch (Exception) { }
                process.Dispose();
                process = null;
            }
            if (job != null) { job.Dispose(); job = null; }
        }
    }

    // Claude Code over standard input and output in streaming JSON, as Anthropic's Agent SDK runs it
    // (code.claude.com/docs/en/agent-sdk, and the Python SDK's subprocess transport): its built-in
    // tools off (--tools ""), the look's server the only one (--strict-mcp-config) with its tools
    // allowed (--allowedTools mcp__windows-lite), and the person's own settings only.
    internal sealed class ClaudeSession : AgentSession
    {
        private string configFile;
        private int requests;
        private readonly Dictionary<string, TaskCompletionSource<JsonObject>> waiting = new Dictionary<string, TaskCompletionSource<JsonObject>>(StringComparer.Ordinal);
        private readonly HashSet<string> streamed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> tools = new Dictionary<string, string>(StringComparer.Ordinal);
        private string streaming = "";

        public ClaudeSession(AiTool tool, string key) : base(tool, key)
        {
        }

        public static string[] Arguments(string configFile)
        {
            return new[]
            {
                "--output-format", "stream-json", "--verbose",
                "--append-system-prompt", Instructions,
                "--tools", "",
                "--allowedTools", "mcp__" + Agents.ServerName,
                "--permission-mode", "default",
                "--mcp-config", configFile,
                "--strict-mcp-config",
                "--include-partial-messages",
                "--setting-sources=user",
                "--input-format", "stream-json",
            };
        }

        // The look's server, as Claude Code's --mcp-config reads it, with the conversation's key in
        // the server's own environment (a file, so the key is never on a command line).
        public static string Config(string key)
        {
            var server = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = Agents.ServerExe,
                ["args"] = new JsonArray("mcp"),
                ["env"] = new JsonObject { [Agents.KeyVariable] = key },
            };
            return new JsonObject { ["mcpServers"] = new JsonObject { [Agents.ServerName] = server } }.ToJsonString();
        }

        public override async Task StartAsync(CancellationToken cancel)
        {
            configFile = KeyFile("claude");
            File.WriteAllText(configFile, Config(Key), new UTF8Encoding(false));
            StartProcess(Arguments(configFile), new Dictionary<string, string>());
            // The handshake the Agent SDK makes first; it needs no model call. Claude Code starts the
            // look's server meanwhile.
            try
            {
                await Within(Request(new JsonObject { ["subtype"] = "initialize", ["hooks"] = null }), 90000, cancel, Tool.Name + " did not answer.").ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Log.Write("assistant: " + ex.Message);
            }
        }

        public override Task SendAsync(string text, CancellationToken cancel)
        {
            Task done = BeginTurn();
            Write(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
                ["parent_tool_use_id"] = null,
                ["session_id"] = "",
            });
            return Await(done, cancel);
        }

        protected override void SendInterrupt()
        {
            Request(new JsonObject { ["subtype"] = "interrupt" });
        }

        private Task<JsonObject> Request(JsonObject request)
        {
            var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            string id = "req_" + Interlocked.Increment(ref requests) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            lock (waiting) { waiting[id] = answer; }
            Write(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
            return answer.Task;
        }

        protected override void Failed(string why)
        {
            lock (waiting)
            {
                foreach (TaskCompletionSource<JsonObject> one in waiting.Values) { one.TrySetException(new InvalidOperationException(why)); }
                waiting.Clear();
            }
        }

        protected override void Receive(JsonObject message)
        {
            switch (AiTools.Text(message, "type"))
            {
                case "control_response": Answered(message["response"] as JsonObject); break;
                case "control_request": Asked(message); break;
                case "system": Init(message); break;
                case "stream_event": Streamed(message["event"] as JsonObject); break;
                case "assistant": Said(message); break;
                case "user": Results(message); break;
                case "result": Over(message); break;
            }
        }

        private void Answered(JsonObject response)
        {
            if (response == null) { return; }
            string id = AiTools.Text(response, "request_id");
            TaskCompletionSource<JsonObject> answer;
            lock (waiting)
            {
                if (!waiting.TryGetValue(id, out answer)) { return; }
                waiting.Remove(id);
            }
            if (AiTools.Text(response, "subtype") == "error") { answer.TrySetException(new InvalidOperationException(AiTools.Text(response, "error"))); }
            else { answer.TrySetResult(response["response"] as JsonObject ?? new JsonObject()); }
        }

        // Claude Code asks only where it was told to (it is not told to here). The look's own tools
        // are allowed as they are, anything else refused, and any other question answered as unknown.
        private void Asked(JsonObject message)
        {
            string id = AiTools.Text(message, "request_id");
            var request = message["request"] as JsonObject;
            JsonObject response;
            if (request != null && AiTools.Text(request, "subtype") == "can_use_tool")
            {
                string name = AiTools.Text(request, "tool_name");
                bool look = name.StartsWith("mcp__" + Agents.ServerName + "__", StringComparison.Ordinal);
                JsonNode given = request["input"];
                response = look
                    ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = given == null ? new JsonObject() : given.DeepClone() }
                    : new JsonObject { ["behavior"] = "deny", ["message"] = "Only the look's own tools can be used here." };
                Write(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = response } });
                return;
            }
            Write(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = "Not supported here." } });
        }

        // The look's server failed to start: said, since nothing can change without it.
        private void Init(JsonObject message)
        {
            if (AiTools.Text(message, "subtype") != "init") { return; }
            var servers = message["mcp_servers"] as JsonArray;
            if (servers == null) { return; }
            foreach (JsonObject server in servers.OfType<JsonObject>())
            {
                if (AiTools.Text(server, "name") != Agents.ServerName) { continue; }
                string status = AiTools.Text(server, "status");
                if (status == "failed") { Raise(AgentEventKind.Note, "The look's tools did not start in " + Tool.Name + ".", "", "", true); }
            }
        }

        private void Streamed(JsonObject e)
        {
            if (e == null) { return; }
            switch (AiTools.Text(e, "type"))
            {
                case "message_start":
                    streaming = AiTools.Text(e["message"], "id");
                    break;
                case "content_block_delta":
                    var delta = e["delta"] as JsonObject;
                    if (delta != null && AiTools.Text(delta, "type") == "text_delta")
                    {
                        streamed.Add(streaming);
                        Raise(AgentEventKind.Text, AiTools.Text(delta, "text"), "", "", false);
                    }
                    break;
            }
        }

        // A whole message: its text where it did not come in pieces, and the tools it uses.
        private void Said(JsonObject message)
        {
            if (message["parent_tool_use_id"] != null) { return; }
            var body = message["message"] as JsonObject;
            if (body == null) { return; }
            string id = AiTools.Text(body, "id");
            var content = body["content"] as JsonArray;
            if (content == null) { return; }
            foreach (JsonObject block in content.OfType<JsonObject>())
            {
                switch (AiTools.Text(block, "type"))
                {
                    case "text":
                        if (!streamed.Contains(id)) { Raise(AgentEventKind.Text, AiTools.Text(block, "text"), "", "", false); }
                        break;
                    case "tool_use":
                        string useId = AiTools.Text(block, "id");
                        if (tools.ContainsKey(useId)) { break; }
                        string name = AiTools.Text(block, "name");
                        tools[useId] = name;
                        Raise(AgentEventKind.ToolStarted, "", useId, name, false);
                        break;
                }
            }
        }

        private void Results(JsonObject message)
        {
            var body = message["message"] as JsonObject;
            var content = body == null ? null : body["content"] as JsonArray;
            if (content == null) { return; }
            foreach (JsonObject block in content.OfType<JsonObject>())
            {
                if (AiTools.Text(block, "type") != "tool_result") { continue; }
                string useId = AiTools.Text(block, "tool_use_id");
                string name;
                if (!tools.TryGetValue(useId, out name)) { continue; }
                JsonNode error = block["is_error"];
                bool failed = error != null && error.GetValueKind() == JsonValueKind.True;
                Raise(AgentEventKind.ToolFinished, ResultText(block["content"]), useId, name, failed);
            }
        }

        private static string ResultText(JsonNode content)
        {
            if (content == null) { return ""; }
            if (content.GetValueKind() == JsonValueKind.String) { return content.GetValue<string>(); }
            var parts = content as JsonArray;
            if (parts == null) { return ""; }
            return string.Join("\n", parts.OfType<JsonObject>().Where(p => AiTools.Text(p, "type") == "text").Select(p => AiTools.Text(p, "text")));
        }

        private void Over(JsonObject message)
        {
            JsonNode error = message["is_error"];
            bool failed = error != null && error.GetValueKind() == JsonValueKind.True;
            if (!failed || WasInterrupted) { EndTurn(null); return; }
            EndTurn(new InvalidOperationException(Problem(AiTools.Text(message, "result"), AiTools.Text(message, "subtype"))));
        }

        // What went wrong, in words, with the way out where there is one.
        public static string Problem(string result, string subtype)
        {
            string text = (result ?? "").Trim();
            if (text.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("authenticate", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Claude Code is not signed in. Sign in again from Quick Search Settings, under AI.";
            }
            if (text.Length == 0) { text = subtype == "error_max_turns" ? "it reached its limit of turns" : "it stopped with " + (subtype.Length > 0 ? subtype : "a problem"); }
            return "Claude Code: " + text;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (configFile != null)
            {
                try { File.Delete(configFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                configFile = null;
            }
        }
    }

    // An error a JSON-RPC peer answered with.
    internal sealed class RpcException : Exception
    {
        public RpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; private set; }
    }

    // JSON-RPC 2.0 over standard input and output, one message a line: Codex's app server (which
    // leaves out the "jsonrpc" field) and the Agent Client Protocol.
    internal abstract class RpcSession : AgentSession
    {
        private readonly bool versioned;
        private int next;
        private readonly Dictionary<string, TaskCompletionSource<JsonNode>> waiting = new Dictionary<string, TaskCompletionSource<JsonNode>>(StringComparer.Ordinal);

        protected RpcSession(AiTool tool, string key, bool versioned) : base(tool, key)
        {
            this.versioned = versioned;
        }

        private JsonObject Message()
        {
            var message = new JsonObject();
            if (versioned) { message["jsonrpc"] = "2.0"; }
            return message;
        }

        protected Task<JsonNode> Call(string method, JsonNode parameters)
        {
            int id = Interlocked.Increment(ref next);
            string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var answer = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waiting) { waiting[key] = answer; }
            JsonObject message = Message();
            message["id"] = id;
            message["method"] = method;
            message["params"] = parameters;
            try
            {
                Write(message);
            }
            catch (Exception ex)
            {
                lock (waiting) { waiting.Remove(key); }
                answer.TrySetException(ex);
            }
            return answer.Task;
        }

        protected void Notify(string method, JsonNode parameters)
        {
            JsonObject message = Message();
            message["method"] = method;
            message["params"] = parameters;
            Write(message);
        }

        protected void Reply(JsonNode id, JsonNode result)
        {
            JsonObject message = Message();
            message["id"] = id == null ? null : id.DeepClone();
            message["result"] = result;
            Write(message);
        }

        protected void ReplyError(JsonNode id, int code, string text)
        {
            JsonObject message = Message();
            message["id"] = id == null ? null : id.DeepClone();
            message["error"] = new JsonObject { ["code"] = code, ["message"] = text };
            Write(message);
        }

        protected override void Receive(JsonObject message)
        {
            JsonNode id = message["id"];
            string method = AiTools.Text(message, "method");
            if (method.Length == 0)
            {
                if (id == null) { return; }
                string key = id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : id.ToJsonString();
                TaskCompletionSource<JsonNode> answer;
                lock (waiting)
                {
                    if (!waiting.TryGetValue(key, out answer)) { return; }
                    waiting.Remove(key);
                }
                var error = message["error"] as JsonObject;
                if (error != null) { answer.TrySetException(new RpcException(Number(error["code"]), AiTools.Text(error, "message"))); }
                else { answer.TrySetResult(message["result"]); }
                return;
            }
            JsonObject parameters = message["params"] as JsonObject ?? new JsonObject();
            if (id != null) { Requested(id, method, parameters); } else { Notified(method, parameters); }
        }

        // A request from the tool, which gets one answer.
        protected abstract void Requested(JsonNode id, string method, JsonObject parameters);

        protected abstract void Notified(string method, JsonObject parameters);

        protected override void Failed(string why)
        {
            lock (waiting)
            {
                foreach (TaskCompletionSource<JsonNode> one in waiting.Values) { one.TrySetException(new InvalidOperationException(why)); }
                waiting.Clear();
            }
        }

        protected static int Number(JsonNode node)
        {
            var value = node as JsonValue;
            if (value == null) { return 0; }
            int whole;
            if (value.TryGetValue(out whole)) { return whole; }
            double number;
            if (value.TryGetValue(out number)) { return (int)number; }
            return 0;
        }
    }

    // Codex's app server (learn.chatgpt.com/docs/app-server, and its schema, read 20 November 2026):
    // JSON-RPC without the "jsonrpc" field. Each conversation is a thread in the empty folder, in
    // Codex's read-only sandbox with no approvals; its shell, web search, image viewer and apps are
    // switched off, and the look's server is the only MCP server (the person's own are switched off
    // for the thread). Codex has no switch for its file-editing tool, which the read-only sandbox
    // stops. A thread that does not come back read-only and without approvals is refused.
    internal sealed class CodexSession : RpcSession
    {
        private string threadId = "";
        private string turnId = "";
        private readonly HashSet<string> streamed = new HashSet<string>(StringComparer.Ordinal);

        public CodexSession(AiTool tool, string key) : base(tool, key, false)
        {
        }

        // For the self-test: a thread started without a process.
        internal void Started(string thread)
        {
            threadId = thread;
        }

        public override async Task StartAsync(CancellationToken cancel)
        {
            StartProcess(Tool.AgentArguments, new Dictionary<string, string>());
            await Within(Call("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "windows_lite", ["title"] = "Windows Lite", ["version"] = Version },
            }), 60000, cancel, Tool.Name + " did not answer.").ConfigureAwait(false);
            Notify("initialized", new JsonObject());
            var theirs = new List<string>();
            try
            {
                theirs = ServerNames(await Within(Call("config/read", new JsonObject()), 30000, cancel, "Codex did not list its settings.").ConfigureAwait(false));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Write("assistant: Codex settings: " + ex.Message);
            }
            JsonNode started;
            try
            {
                started = await Within(Call("thread/start", ThreadParams(Key, theirs)), 120000, cancel, Tool.Name + " did not start a conversation.").ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw new InvalidOperationException(Problem(ex.Message));
            }
            string refused = Refused(started);
            if (refused != null) { throw new InvalidOperationException(refused); }
            threadId = AiTools.Text((started as JsonObject)?["thread"], "id");
            if (threadId.Length == 0) { throw new InvalidOperationException(Tool.Name + " started no conversation."); }
        }

        public static JsonObject ThreadParams(string key, IEnumerable<string> theirs)
        {
            string server = "mcp_servers." + Agents.ServerName + ".";
            var config = new JsonObject
            {
                [server + "command"] = Agents.ServerExe,
                [server + "args"] = new JsonArray("mcp"),
                [server + "env"] = new JsonObject { [Agents.KeyVariable] = key },
                [server + "required"] = true,
                [server + "startup_timeout_sec"] = 30,
                // With no approvals, Codex refuses an MCP tool that would need one; the look's run
                // without a question.
                [server + "default_tools_approval_mode"] = "approve",
                ["features.shell_tool"] = false,
                ["features.view_image"] = false,
                ["features.apps"] = false,
                ["web_search"] = "disabled",
            };
            foreach (string name in theirs)
            {
                if (string.Equals(name, Agents.ServerName, StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(new[] { '.', ' ', '"' }) >= 0) { continue; }
                config["mcp_servers." + name + ".enabled"] = false;
            }
            return new JsonObject
            {
                ["cwd"] = WorkFolder,
                ["approvalPolicy"] = "never",
                ["sandbox"] = "read-only",
                ["serviceName"] = "windows_lite",
                ["developerInstructions"] = Instructions,
                ["config"] = config,
            };
        }

        // The names of the MCP servers in Codex's own settings (config/read).
        public static List<string> ServerNames(JsonNode node)
        {
            var o = node as JsonObject;
            if (o != null)
            {
                foreach (KeyValuePair<string, JsonNode> pair in o)
                {
                    var servers = pair.Value as JsonObject;
                    if (pair.Key == "mcp_servers" && servers != null) { return servers.Select(p => p.Key).ToList(); }
                }
                foreach (KeyValuePair<string, JsonNode> pair in o)
                {
                    List<string> inner = ServerNames(pair.Value);
                    if (inner.Count > 0) { return inner; }
                }
            }
            var a = node as JsonArray;
            if (a != null)
            {
                foreach (JsonNode item in a)
                {
                    List<string> inner = ServerNames(item);
                    if (inner.Count > 0) { return inner; }
                }
            }
            return new List<string>();
        }

        // Why a thread Codex started is refused, or null: it must be read-only, without approvals.
        public static string Refused(JsonNode started)
        {
            var result = started as JsonObject;
            if (result == null) { return null; }
            JsonNode policy = result["approvalPolicy"];
            if (policy != null && !(policy.GetValueKind() == JsonValueKind.String && policy.GetValue<string>() == "never"))
            {
                return "Codex did not take the assistant's limits: it would ask for approvals.";
            }
            JsonNode sandbox = result["sandbox"];
            if (sandbox != null)
            {
                string type = sandbox.GetValueKind() == JsonValueKind.String ? sandbox.GetValue<string>() : AiTools.Text(sandbox, "type");
                if (type != "readOnly" && type != "read-only") { return "Codex did not take the assistant's limits: its sandbox is " + (type.Length > 0 ? type : "unknown") + ", not read-only."; }
            }
            return null;
        }

        public override async Task SendAsync(string text, CancellationToken cancel)
        {
            Task done = BeginTurn();
            turnId = "";
            try
            {
                JsonNode answer = await Within(Call("turn/start", new JsonObject
                {
                    ["threadId"] = threadId,
                    ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                }), 60000, cancel, Tool.Name + " did not take the message.").ConfigureAwait(false);
                string id = AiTools.Text((answer as JsonObject)?["turn"], "id");
                if (id.Length > 0) { turnId = id; }
            }
            catch (Exception ex)
            {
                EndTurn(ex is RpcException ? new InvalidOperationException(Problem(ex.Message)) : ex);
            }
            await Await(done, cancel).ConfigureAwait(false);
        }

        protected override void SendInterrupt()
        {
            if (threadId.Length == 0 || turnId.Length == 0) { return; }
            Call("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId });
        }

        protected override void Notified(string method, JsonObject p)
        {
            switch (method)
            {
                case "turn/started":
                    {
                        string id = AiTools.Text(p["turn"], "id");
                        if (id.Length > 0) { turnId = id; }
                        break;
                    }
                case "item/agentMessage/delta":
                    streamed.Add(AiTools.Text(p, "itemId"));
                    Raise(AgentEventKind.Text, AiTools.Text(p, "delta"), "", "", false);
                    break;
                case "item/started":
                    {
                        var item = p["item"] as JsonObject;
                        if (item != null && AiTools.Text(item, "type") == "mcpToolCall") { Raise(AgentEventKind.ToolStarted, "", AiTools.Text(item, "id"), ToolName(item), false); }
                        break;
                    }
                case "item/completed":
                    {
                        var item = p["item"] as JsonObject;
                        if (item == null) { break; }
                        string type = AiTools.Text(item, "type");
                        if (type == "agentMessage" && !streamed.Contains(AiTools.Text(item, "id")))
                        {
                            Raise(AgentEventKind.Text, AiTools.Text(item, "text"), "", "", false);
                        }
                        else if (type == "mcpToolCall")
                        {
                            var error = item["error"] as JsonObject;
                            bool failed = AiTools.Text(item, "status") == "failed" || error != null;
                            Raise(AgentEventKind.ToolFinished, error != null ? AiTools.Text(error, "message") : ResultText(item["result"]), AiTools.Text(item, "id"), ToolName(item), failed);
                        }
                        break;
                    }
                case "turn/completed":
                    {
                        var turn = p["turn"] as JsonObject;
                        string status = AiTools.Text(turn, "status");
                        if (status == "completed" || (status == "interrupted" && WasInterrupted)) { EndTurn(null); }
                        else if (status == "interrupted")
                        {
                            Raise(AgentEventKind.Note, Tool.Name + " stopped its answer.", "", "", false);
                            EndTurn(null);
                        }
                        else { EndTurn(new InvalidOperationException(Problem(AiTools.Text(turn == null ? null : turn["error"], "message")))); }
                        break;
                    }
            }
        }

        // Codex asks only where it was told to (with no approvals, it is not): nothing that runs a
        // command or changes a file is allowed, and only the look's own tools are confirmed.
        protected override void Requested(JsonNode id, string method, JsonObject p)
        {
            switch (method)
            {
                case "item/commandExecution/requestApproval":
                case "item/fileChange/requestApproval":
                    Reply(id, new JsonObject { ["decision"] = "decline" });
                    return;
                case "mcpServer/elicitation/request":
                    {
                        var meta = p["_meta"] as JsonObject;
                        bool look = AiTools.Text(p, "serverName") == Agents.ServerName && AiTools.Text(p, "mode") != "url" && AiTools.Text(meta, "codex_approval_kind") == "mcp_tool_call";
                        Reply(id, look
                            ? new JsonObject { ["action"] = "accept", ["content"] = new JsonObject(), ["_meta"] = null }
                            : new JsonObject { ["action"] = "decline", ["content"] = null, ["_meta"] = null });
                        return;
                    }
                case "item/permissions/requestApproval":
                    Reply(id, new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" });
                    return;
                default:
                    ReplyError(id, -32601, "Windows Lite's assistant does not answer " + method + ".");
                    return;
            }
        }

        private static string ToolName(JsonObject item)
        {
            return AiTools.Text(item, "server") + "." + AiTools.Text(item, "tool");
        }

        private static string ResultText(JsonNode result)
        {
            var content = (result as JsonObject)?["content"] as JsonArray;
            if (content == null) { return ""; }
            return string.Join("\n", content.OfType<JsonObject>().Where(c => AiTools.Text(c, "type") == "text").Select(c => AiTools.Text(c, "text")));
        }

        public static string Problem(string message)
        {
            string text = (message ?? "").Trim();
            if (text.IndexOf("401", StringComparison.Ordinal) >= 0 || text.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("log in", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Codex is not signed in. Sign in again from Quick Search Settings, under AI.";
            }
            return "Codex: " + (text.Length > 0 ? text : "it stopped with a problem.");
        }
    }

    // The Agent Client Protocol (agentclientprotocol.com, protocol version 1, read 20 November 2026),
    // for Gemini CLI, Antigravity CLI and OpenCode. The assistant offers the agent no file system and
    // no terminal, so it works only with its own tools, in the empty folder, and every permission it
    // asks for is refused unless it is for one of the look's tools. Gemini CLI gets settings of its
    // own for the conversation, through its documented variables: its built-in tools off, the look's
    // server the only one (trusted, so its tools need no question), and the empty folder trusted, as
    // Gemini CLI's folder trust needs before it starts any MCP server. OpenCode is asked for its plan
    // mode, which edits no files.
    internal sealed class AcpSession : RpcSession
    {
        private string sessionId = "";
        private bool instructed;
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> files = new List<string>();

        public AcpSession(AiTool tool, string key) : base(tool, key, true)
        {
        }

        // For the self-test: a session started without a process.
        internal void Started(string session)
        {
            sessionId = session;
        }

        public override async Task StartAsync(CancellationToken cancel)
        {
            var environment = new Dictionary<string, string>();
            bool inSettings = Tool.Id == "gemini-cli" && GeminiSettings(environment);
            StartProcess(Tool.AgentArguments, environment);
            await Within(Call("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "windows-lite", ["title"] = "Windows Lite", ["version"] = Version },
            }), 90000, cancel, Tool.Name + " did not answer.").ConfigureAwait(false);
            JsonNode session;
            try
            {
                session = await Within(Call("session/new", new JsonObject
                {
                    ["cwd"] = WorkFolder,
                    ["mcpServers"] = inSettings ? new JsonArray() : new JsonArray(LookServer(Key)),
                }), 120000, cancel, Tool.Name + " did not start a conversation.").ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw new InvalidOperationException(Problem(ex.Code, ex.Message));
            }
            sessionId = AiTools.Text(session, "sessionId");
            if (sessionId.Length == 0) { throw new InvalidOperationException(Tool.Name + " started no conversation."); }
            if (Tool.AgentMode.Length > 0) { await UseMode(session as JsonObject, cancel).ConfigureAwait(false); }
        }

        // The look's server as ACP's session/new gives it: a stdio server with its environment.
        public static JsonObject LookServer(string key)
        {
            return new JsonObject
            {
                ["name"] = Agents.ServerName,
                ["command"] = Agents.ServerExe,
                ["args"] = new JsonArray("mcp"),
                ["env"] = new JsonArray(new JsonObject { ["name"] = Agents.KeyVariable, ["value"] = key }),
            };
        }

        // Gemini CLI's settings for this conversation (GEMINI_CLI_SYSTEM_SETTINGS_PATH, which outranks
        // the person's own, and GEMINI_CLI_TRUSTED_FOLDERS_PATH). None where an organisation set Gemini
        // CLI's system settings: those stay the organisation's.
        private bool GeminiSettings(IDictionary<string, string> environment)
        {
            string organisation = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "gemini-cli", "settings.json");
            if (File.Exists(organisation) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_CLI_SYSTEM_SETTINGS_PATH")))
            {
                Raise(AgentEventKind.Note, "Your organisation's settings for Gemini CLI apply here, so its own tools stay on; Windows Lite refuses every change they ask to make.", "", "", false);
                return false;
            }
            string settings = KeyFile("gemini");
            string trusted = KeyFile("gemini-trust");
            File.WriteAllText(settings, GeminiSettingsJson(Key), new UTF8Encoding(false));
            File.WriteAllText(trusted, new JsonObject { [WorkFolder] = "TRUST_FOLDER" }.ToJsonString(), new UTF8Encoding(false));
            files.Add(settings);
            files.Add(trusted);
            environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = settings;
            environment["GEMINI_CLI_TRUSTED_FOLDERS_PATH"] = trusted;
            return true;
        }

        public static string GeminiSettingsJson(string key)
        {
            var server = new JsonObject
            {
                ["command"] = Agents.ServerExe,
                ["args"] = new JsonArray("mcp"),
                ["env"] = new JsonObject { [Agents.KeyVariable] = key },
                ["trust"] = true,
            };
            return new JsonObject
            {
                ["tools"] = new JsonObject { ["core"] = new JsonArray() },
                ["mcp"] = new JsonObject { ["allowed"] = new JsonArray(Agents.ServerName) },
                ["mcpServers"] = new JsonObject { [Agents.ServerName] = server },
            }.ToJsonString();
        }

        // The mode the tool is asked to work in (OpenCode's plan), as a config option or a mode.
        private async Task UseMode(JsonObject session, CancellationToken cancel)
        {
            string mode = Tool.AgentMode;
            try
            {
                var options = session == null ? null : session["configOptions"] as JsonArray;
                JsonObject option = options == null ? null : options.OfType<JsonObject>().FirstOrDefault(o => AiTools.Text(o, "id") == "mode");
                if (option != null && option.ToJsonString().IndexOf("\"" + mode + "\"", StringComparison.Ordinal) >= 0)
                {
                    await Within(Call("session/set_config_option", new JsonObject { ["sessionId"] = sessionId, ["configId"] = "mode", ["value"] = mode }), 30000, cancel, "mode").ConfigureAwait(false);
                    return;
                }
                var modes = session == null ? null : session["modes"] as JsonObject;
                var available = modes == null ? null : modes["availableModes"] as JsonArray;
                if (available != null && available.OfType<JsonObject>().Any(m => AiTools.Text(m, "id") == mode))
                {
                    await Within(Call("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = mode }), 30000, cancel, "mode").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Write("assistant: " + Tool.Name + " mode: " + ex.Message);
            }
            Raise(AgentEventKind.Note, Tool.Name + " did not switch to its " + mode + " mode; Windows Lite refuses every change it asks to make.", "", "", false);
        }

        public override async Task SendAsync(string text, CancellationToken cancel)
        {
            Task done = BeginTurn();
            var prompt = new JsonArray();
            // ACP has no place for a client's instructions, so the first message carries them.
            if (!instructed)
            {
                prompt.Add(new JsonObject { ["type"] = "text", ["text"] = Instructions });
                instructed = true;
            }
            prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            Task<JsonNode> answer = Call("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = prompt });
            _ = answer.ContinueWith(Answered, TaskScheduler.Default);
            await Await(done, cancel).ConfigureAwait(false);
        }

        private void Answered(Task<JsonNode> t)
        {
            if (t.IsFaulted)
            {
                Exception ex = t.Exception.GetBaseException();
                var rpc = ex as RpcException;
                EndTurn(new InvalidOperationException(rpc != null ? Problem(rpc.Code, rpc.Message) : ex.Message));
                return;
            }
            string reason = AiTools.Text(t.Result, "stopReason");
            if (reason == "refusal") { Raise(AgentEventKind.Note, Tool.Name + " declined to answer that.", "", "", false); }
            else if (reason == "max_tokens" || reason == "max_turn_requests") { Raise(AgentEventKind.Note, Tool.Name + " reached its limit for one answer.", "", "", false); }
            EndTurn(null);
        }

        protected override void SendInterrupt()
        {
            if (sessionId.Length > 0) { Notify("session/cancel", new JsonObject { ["sessionId"] = sessionId }); }
        }

        protected override void Notified(string method, JsonObject p)
        {
            if (method != "session/update") { return; }
            var update = p["update"] as JsonObject;
            if (update == null) { return; }
            switch (AiTools.Text(update, "sessionUpdate"))
            {
                case "agent_message_chunk":
                    {
                        var content = update["content"] as JsonObject;
                        if (content != null && AiTools.Text(content, "type") == "text") { Raise(AgentEventKind.Text, AiTools.Text(content, "text"), "", "", false); }
                        break;
                    }
                case "tool_call":
                    {
                        string id = AiTools.Text(update, "toolCallId");
                        titles[id] = AiTools.Text(update, "title");
                        kinds[id] = AiTools.Text(update, "kind");
                        Raise(AgentEventKind.ToolStarted, "", id, titles[id], false);
                        Finished(id, update);
                        break;
                    }
                case "tool_call_update":
                    {
                        string id = AiTools.Text(update, "toolCallId");
                        string title = AiTools.Text(update, "title");
                        if (title.Length > 0) { titles[id] = title; }
                        Finished(id, update);
                        break;
                    }
            }
        }

        private void Finished(string id, JsonObject update)
        {
            string status = AiTools.Text(update, "status");
            if (status != "completed" && status != "failed") { return; }
            string title;
            titles.TryGetValue(id, out title);
            Raise(AgentEventKind.ToolFinished, ContentText(update["content"]), id, title ?? "", status == "failed");
        }

        private static string ContentText(JsonNode content)
        {
            var list = content as JsonArray;
            if (list == null) { return ""; }
            var parts = new List<string>();
            foreach (JsonObject item in list.OfType<JsonObject>())
            {
                var inner = item["content"] as JsonObject;
                if (inner != null && AiTools.Text(inner, "type") == "text") { parts.Add(AiTools.Text(inner, "text")); }
            }
            return string.Join("\n", parts);
        }

        // The agent asks only for permissions: the look's own tools are allowed once, anything else
        // refused. No file system or terminal was offered, so any request for one is refused too.
        protected override void Requested(JsonNode id, string method, JsonObject p)
        {
            if (method != "session/request_permission")
            {
                ReplyError(id, -32601, "Windows Lite's assistant does not offer " + method + ".");
                return;
            }
            var call = p["toolCall"] as JsonObject ?? new JsonObject();
            var options = p["options"] as JsonArray ?? new JsonArray();
            string callId = AiTools.Text(call, "toolCallId");
            string title, kind;
            titles.TryGetValue(callId, out title);
            kinds.TryGetValue(callId, out kind);
            string chosen = Choose(options, LookCall(call, options, title ?? "", kind ?? ""));
            Reply(id, new JsonObject
            {
                ["outcome"] = chosen == null ? new JsonObject { ["outcome"] = "cancelled" } : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen },
            });
        }

        // Whether a permission is asked for one of the look's own tools: never for a tool that edits,
        // deletes, moves, runs or fetches, and only where the agent's own words name the look's server.
        // A title can carry what the AI wrote (a command, a path), so it counts only when it is exactly
        // a look tool's name with the server's.
        public static bool LookCall(JsonObject call, JsonArray options, string knownTitle, string knownKind)
        {
            string kind = AiTools.Text(call, "kind");
            if (kind.Length == 0) { kind = knownKind; }
            if (kind.Length > 0 && kind != "other") { return false; }
            string title = AiTools.Text(call, "title");
            if (title.Length == 0) { title = knownTitle; }
            string name = LookTool(title);
            if (name != title.Trim() && LookToolNames.Contains(name)) { return true; }
            foreach (JsonObject option in options.OfType<JsonObject>())
            {
                if (AiTools.Text(option, "name").IndexOf(Agents.ServerName, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        // The option to answer with: once, where the agent offers that.
        public static string Choose(JsonArray options, bool allow)
        {
            foreach (string kind in allow ? new[] { "allow_once", "allow_always" } : new[] { "reject_once", "reject_always" })
            {
                JsonObject option = options.OfType<JsonObject>().FirstOrDefault(o => AiTools.Text(o, "kind") == kind);
                if (option != null) { return AiTools.Text(option, "optionId"); }
            }
            return null;
        }

        private string Problem(int code, string message)
        {
            string text = (message ?? "").Trim();
            if (code == -32000 || text.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Tool.Name + " is not signed in. Sign in from Quick Search Settings, under AI.";
            }
            return Tool.Name + ": " + (text.Length > 0 ? text : "it stopped with a problem (" + code + ").");
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in files)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            files.Clear();
        }
    }
}
