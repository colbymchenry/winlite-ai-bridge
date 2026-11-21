// The bridge's own small foundation: a log, a data folder, a generic "run a command and read what it
// printed" helper (kept under the name Agents so the extracted tool code calls it unchanged), and the
// MCP server the caller offers a tool with each session. The bridge knows nothing of the look itself;
// the look's own MCP server is passed in (McpConfig).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WinLiteBridge
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string File { get { return Path.Combine(Paths.Base, "bridge.log"); } }

        public static void Write(string message)
        {
            // Standard error is the bridge's own channel (standard output carries the protocol), and
            // a file so a past run can be read. Never standard output.
            try { Console.Error.WriteLine(message); } catch (Exception) { }
            try
            {
                lock (Gate)
                {
                    System.IO.File.AppendAllText(File, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }
    }

    internal static class Paths
    {
        public static string Base
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinLiteBridge");
                try { Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
        }

        public static string Folder(string name)
        {
            string dir = Path.Combine(Base, name);
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            return dir;
        }
    }

    // The MCP server the caller offers a tool, and what the tool is told, passed in with each session.
    // The bridge passes this through to the tool and never interprets it.
    internal sealed class McpConfig
    {
        public string Name = "";                 // the server's name, as the tool lists it
        public string Command = "";              // the exe that runs the server
        public string[] CommandArguments = new string[0];
        public string KeyVariable = "";          // the environment variable the per-session key goes in
        public string Key = "";                  // the per-session key
        public string Instructions = "";         // what the tool is told, after its own instructions
        public HashSet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal); // the server's tools, by name

        public static McpConfig None { get { return new McpConfig(); } }
    }

    // Generic process helpers. Named Agents so the extracted AiTools and AgentSessions call them
    // unchanged; nothing here is look-specific.
    internal static class Agents
    {
        // The MCP server the bridge offers each tool, set once by the caller at startup (initialize).
        // These three values are the only look-specific knowledge the bridge holds, and the caller
        // provides them; the bridge never reads the look itself. The per-session key is separate.
        public static string ServerName = "";
        public static string ServerExe = "";
        public static string KeyVariable = "";

        public sealed class RunResult
        {
            public int Code;
            public string Output = "";
        }

        // Runs an app's own command, with nothing shown and nothing typed into it.
        public static RunResult Run(string exe, string[] arguments, int timeoutMs)
        {
            string joined = string.Join(" ", arguments.Select(Quote));
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                info.Arguments = "/d /s /c \"" + Quote(exe) + " " + joined + "\"";
            }
            else
            {
                info.FileName = exe;
                info.Arguments = joined;
            }
            using (Process process = Process.Start(info))
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return new RunResult { Code = -1, Output = "It did not answer within " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + " seconds." };
                }
                process.WaitForExit();
                return new RunResult { Code = process.ExitCode, Output = (output.Result + error.Result).Trim() };
            }
        }

        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { return argument; }
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }
}
