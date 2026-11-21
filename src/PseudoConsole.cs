// L7: a pseudoconsole (Windows' own ConPTY) for the AI tools' own sign-ins and installs
// (docs/10-customization.md, "Connecting an AI", way 2). The tool runs unmodified, as in a terminal;
// what it prints is drawn by TerminalView in the look's colours, and what the person types goes back
// to it. Windows Lite reads no token: it only shows the tool's screen and passes keys through.
//
// Also a job object for the assistant's tool processes, so that a conversation that ends takes the
// whole tree with it (an npm-installed tool is cmd.exe, then node.exe, then the MCP server).
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WinLiteBridge
{
    internal sealed class PseudoConsole : IDisposable
    {
        private IntPtr console = IntPtr.Zero;
        private IntPtr process = IntPtr.Zero;
        private IntPtr thread = IntPtr.Zero;
        private SafeFileHandle inputWrite;
        private SafeFileHandle outputRead;
        private FileStream input;
        private readonly object gate = new object();
        private bool closed;

        // What the program printed, decoded from UTF-8 (on a background thread).
        public event Action<string> Output;

        // The program ended, with its exit code (on a background thread).
        public event Action<int> Exited;

        public int ProcessId { get; private set; }

        public bool HasExited { get; private set; }

        public int ExitCode { get; private set; }

        // Starts a command line in a new pseudoconsole of the given size.
        public void Start(string commandLine, string folder, int columns, int rows)
        {
            SafeFileHandle inputRead, outputWrite;
            if (!Interop.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (!Interop.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            int hr = Interop.CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out console);
            // The console has its own copies now.
            inputRead.Dispose();
            outputWrite.Dispose();
            if (hr != 0) { throw new Win32Exception(hr, "Windows could not create a pseudoconsole (" + hr.ToString("X8") + ")."); }

            IntPtr size = IntPtr.Zero;
            Interop.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            IntPtr attributes = Marshal.AllocHGlobal(size);
            try
            {
                if (!Interop.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                if (!Interop.UpdateProcThreadAttribute(attributes, 0, (IntPtr)Interop.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var startup = new Interop.STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(Interop.STARTUPINFOEX));
                startup.lpAttributeList = attributes;
                Interop.PROCESS_INFORMATION info;
                var line = new StringBuilder(commandLine);
                if (!Interop.CreateProcess(null, line, IntPtr.Zero, IntPtr.Zero, false, Interop.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, folder, ref startup, out info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                process = info.hProcess;
                thread = info.hThread;
                ProcessId = info.dwProcessId;
            }
            finally
            {
                Interop.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            input = new FileStream(inputWrite, FileAccess.Write, 1);
            new Thread(ReadOutput) { IsBackground = true, Name = "pseudoconsole output" }.Start();
            new Thread(WaitForExit) { IsBackground = true, Name = "pseudoconsole wait" }.Start();
        }

        // Sends what the person typed (VT input: text and key sequences).
        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) { return; }
            lock (gate)
            {
                if (closed || input == null) { return; }
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            lock (gate)
            {
                if (closed || console == IntPtr.Zero) { return; }
                Interop.ResizePseudoConsole(console, Size(columns, rows));
            }
        }

        private void ReadOutput()
        {
            Decoder decoder = new UTF8Encoding(false).GetDecoder();
            var buffer = new byte[8192];
            var chars = new char[8192 + 4];
            try
            {
                using (var stream = new FileStream(outputRead, FileAccess.Read, 1))
                {
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) { break; }
                        int count = decoder.GetChars(buffer, 0, read, chars, 0);
                        if (count > 0) { Output?.Invoke(new string(chars, 0, count)); }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void WaitForExit()
        {
            Interop.WaitForSingleObject(process, Interop.INFINITE);
            uint code;
            ExitCode = Interop.GetExitCodeProcess(process, out code) ? unchecked((int)code) : -1;
            HasExited = true;
            // Give the console a moment to pass on the program's last lines, then close it, which
            // ends the output (Windows keeps a pseudoconsole open until it is closed).
            Thread.Sleep(300);
            CloseConsole();
            Exited?.Invoke(ExitCode);
        }

        // Ends the program (Windows sends it the console's close event) and the console.
        public void Stop()
        {
            if (!HasExited && process != IntPtr.Zero)
            {
                CloseConsole();
                if (Interop.WaitForSingleObject(process, 3000) != 0) { Interop.TerminateProcess(process, 1); }
            }
        }

        private void CloseConsole()
        {
            IntPtr handle;
            lock (gate)
            {
                if (closed) { return; }
                closed = true;
                try { if (input != null) { input.Dispose(); } } catch (IOException) { }
                handle = console;
                console = IntPtr.Zero;
            }
            // Outside the lock: before Windows 11 24H2 this waits until the last of the output is
            // read, and the reading thread may need the lock meanwhile, to answer the program.
            if (handle != IntPtr.Zero) { Interop.ClosePseudoConsole(handle); }
        }

        public void Dispose()
        {
            Stop();
            CloseConsole();
            if (thread != IntPtr.Zero) { Interop.CloseHandle(thread); thread = IntPtr.Zero; }
            if (process != IntPtr.Zero && HasExited) { Interop.CloseHandle(process); process = IntPtr.Zero; }
        }

        private static Interop.COORD Size(int columns, int rows)
        {
            return new Interop.COORD { X = (short)Math.Max(20, Math.Min(500, columns)), Y = (short)Math.Max(5, Math.Min(300, rows)) };
        }

        // A command line for a tool: a .cmd or .bat runs through cmd.exe, as Windows runs it.
        public static string CommandLine(string exe, string[] arguments)
        {
            var line = new StringBuilder();
            foreach (string argument in arguments) { line.Append(' ').Append(Quote(argument)); }
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /d /s /c \"" + Quote(exe) + line + "\"";
            }
            return Quote(exe) + line;
        }

        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^' }) < 0) { return argument; }
            return "\"" + argument.Replace("\"", "") + "\"";
        }
    }

    // A job that ends every process in it when it is closed: the assistant's tool, what the tool
    // started, and the look's MCP server it runs.
    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr job;

        public ProcessJob()
        {
            job = Interop.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { return; }
            var limits = new Interop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Interop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf(typeof(Interop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                Interop.SetInformationJobObject(job, Interop.JobObjectExtendedLimitInformation, pointer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public void Add(System.Diagnostics.Process process)
        {
            if (job == IntPtr.Zero || process == null) { return; }
            try { Interop.AssignProcessToJobObject(job, process.Handle); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (job != IntPtr.Zero) { Interop.CloseHandle(job); job = IntPtr.Zero; }
        }
    }

    internal static class Interop
    {
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint INFINITE = 0xFFFFFFFF;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        public static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
