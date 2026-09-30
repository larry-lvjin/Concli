using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConCli.Terminal
{
    /// <summary>
    /// Wraps a Windows pseudo console (ConPTY) hosting a child process.
    /// Owns both pipes, so the raw VT byte stream can be tee'd to disk on the way
    /// through — see <see cref="ConPtyOptions.TeePath"/>. Requires Windows 10 1809+.
    /// </summary>
    public sealed class ConPty : IDisposable
    {
        // ── Win32 ─────────────────────────────────────────────
        private const int  STARTF_USESTDHANDLES            = 0x00000100;
        private const int  EXTENDED_STARTUPINFO_PRESENT    = 0x00080000;
        private const int  CREATE_UNICODE_ENVIRONMENT      = 0x00000400;
        private const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess; public IntPtr hThread;
            public int dwProcessId; public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
            public int dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEXW
        {
            public STARTUPINFOW StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput,
            uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList,
            int dwAttributeCount, int dwFlags, ref nint lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags,
            nint Attribute, IntPtr lpValue, nint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        // ── State ─────────────────────────────────────────────
        private readonly ConPtyOptions _opt;
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _inWrite = IntPtr.Zero;     // we write  -> child stdin
        private IntPtr _outRead = IntPtr.Zero;     // child stdout -> we read
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private IntPtr _attrList = IntPtr.Zero;

        private FileStream? _reader;
        private FileStream? _writer;
        private FileStream? _tee;
        private Task? _readLoop;
        private Task? _waitLoop;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _writeLock = new();
        private int _disposed;

        /// <summary>Decoded UTF-8 text from the child. Raised on a background thread.</summary>
        public event Action<string>? Output;

        /// <summary>Child process exited. Raised on a background thread.</summary>
        public event Action<int>? Exited;

        public bool IsRunning => _hProcess != IntPtr.Zero && _disposed == 0;

        public ConPty(ConPtyOptions options) => _opt = options;

        public void Start()
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = false,
            };

            // The PTY reads our input pipe and writes our output pipe; we keep the far ends.
            if (!CreatePipe(out IntPtr inRead, out _inWrite, ref sa, 0))
                throw new Win32Exception_("CreatePipe(stdin)");
            if (!CreatePipe(out _outRead, out IntPtr outWrite, ref sa, 0))
                throw new Win32Exception_("CreatePipe(stdout)");

            var size = new COORD { X = _opt.Cols, Y = _opt.Rows };
            int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);

            // CreatePseudoConsole duplicates these; our copies are dead weight either way.
            CloseHandle(inRead);
            CloseHandle(outWrite);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

            StartChild();

            _reader = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(_outRead, false),
                                     FileAccess.Read, 0, false);
            _writer = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(_inWrite, false),
                                     FileAccess.Write, 0, false);

            if (!string.IsNullOrWhiteSpace(_opt.TeePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_opt.TeePath!)!);
                _tee = new FileStream(_opt.TeePath!, FileMode.Append, FileAccess.Write, FileShare.Read);
            }

            _readLoop = Task.Run(ReadLoopAsync);
            _waitLoop = Task.Run(WaitLoop);
        }

        private void StartChild()
        {
            nint listSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            _attrList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize))
                throw new Win32Exception_("InitializeProcThreadAttributeList");

            if (!UpdateProcThreadAttribute(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                           _hPC, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception_("UpdateProcThreadAttribute");

            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            si.lpAttributeList = _attrList;

            // Without this the child keeps the std handles it would otherwise inherit from
            // our console, so its output goes there instead of the PTY — the pseudoconsole
            // attaches (titles come through) but the screen buffer stays empty. Null handles
            // plus USESTDHANDLES force it to open CONIN$/CONOUT$ on its new console.
            si.StartupInfo.dwFlags   = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput  = IntPtr.Zero;
            si.StartupInfo.hStdOutput = IntPtr.Zero;
            si.StartupInfo.hStdError  = IntPtr.Zero;

            // CreateProcessW writes into lpCommandLine; the CLR marshals into a temp buffer,
            // so passing a managed string is safe here.
            string cmd = _opt.CommandLine;

            // Environment is fixed at creation — this is the only moment a profile's endpoint
            // and API key can be handed to the CLI. Nothing can change it afterwards, which is
            // why switching profile mid-session is impossible and switching model is not.
            IntPtr envBlock = BuildEnvironmentBlock(_opt.Environment);
            try
            {
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                                    envBlock, _opt.WorkingDirectory, ref si, out var pi))
                    throw new Win32Exception_($"CreateProcess({cmd})");

                _hProcess = pi.hProcess;
                _hThread = pi.hThread;
                ProcessId = pi.dwProcessId;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }
        }

        /// <summary>
        /// Our own environment with <paramref name="overrides"/> applied, in the packed
        /// "NAME=VALUE\0…\0\0" form CreateProcess wants. Returns Zero for "no overrides",
        /// which makes the child inherit ours — not the same thing as an empty block, which
        /// would give it no PATH at all.
        /// </summary>
        private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides)
        {
            if (overrides is null || overrides.Count == 0) return IntPtr.Zero;

            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                vars[(string)e.Key] = (string?)e.Value ?? "";

            foreach (var (k, v) in overrides)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (v is null) vars.Remove(k); else vars[k] = v;
            }

            // The block must be sorted by name; the ordinal-ignore-case order above is what
            // the loader itself uses, and it puts the "=C:" drive-cwd entries first for free.
            var sb = new StringBuilder();
            foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');

            // StringToHGlobalUni copies embedded nulls verbatim and adds the final one,
            // which is exactly the double-null terminator the block needs.
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        public int ProcessId { get; private set; }

        private async Task ReadLoopAsync()
        {
            var buf = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();   // carries split multi-byte sequences across reads
            var chars = new char[8192 * 2];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await _reader!.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token)
                                          .ConfigureAwait(false);
                    if (n <= 0) break;

                    if (_tee != null)
                    {
                        await _tee.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                        await _tee.FlushAsync().ConfigureAwait(false);
                    }

                    int c = decoder.GetChars(buf, 0, n, chars, 0);
                    if (c > 0) Output?.Invoke(new string(chars, 0, c));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }          // pipe closed by peer — normal on exit
            catch (ObjectDisposedException) { }
        }

        private void WaitLoop()
        {
            WaitForSingleObject(_hProcess, 0xFFFFFFFF);
            GetExitCodeProcess(_hProcess, out int code);
            Exited?.Invoke(code);
        }

        public void Write(string data)
        {
            if (_writer == null || _disposed != 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(data);
            lock (_writeLock)
            {
                try { _writer.Write(bytes, 0, bytes.Length); _writer.Flush(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Resize(short cols, short rows)
        {
            if (_hPC == IntPtr.Zero || _disposed != 0) return;
            if (cols < 1 || rows < 1) return;
            ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _cts.Cancel();

            // Closing the PTY signals the child to exit and unblocks the read loop.
            if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

            if (_hProcess != IntPtr.Zero)
            {
                // Give it a moment to go quietly, then insist. Leaving a stray
                // powershell.exe behind is the failure mode we care most about.
                if (WaitForSingleObject(_hProcess, 3000) != 0)
                    try { TerminateProcess(_hProcess, 1); } catch { }
            }

            try { _readLoop?.Wait(2000); } catch { }
            try { _waitLoop?.Wait(2000); } catch { }

            _reader?.Dispose();
            _writer?.Dispose();
            _tee?.Dispose();

            if (_inWrite  != IntPtr.Zero) { CloseHandle(_inWrite);  _inWrite  = IntPtr.Zero; }
            if (_outRead  != IntPtr.Zero) { CloseHandle(_outRead);  _outRead  = IntPtr.Zero; }
            if (_hThread  != IntPtr.Zero) { CloseHandle(_hThread);  _hThread  = IntPtr.Zero; }
            if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }

            if (_attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
                _attrList = IntPtr.Zero;
            }

            _cts.Dispose();
        }

        private sealed class Win32Exception_ : System.ComponentModel.Win32Exception
        {
            public Win32Exception_(string what)
                : base(Marshal.GetLastWin32Error(), $"{what} failed: {Marshal.GetLastWin32Error()}") { }
        }
    }

    public sealed class ConPtyOptions
    {
        public string CommandLine { get; init; } = Shell.DefaultCommandLine();
        public string? WorkingDirectory { get; init; }
        public short Cols { get; init; } = 120;
        public short Rows { get; init; } = 30;

        /// <summary>Raw byte tee target. Null disables logging.</summary>
        public string? TeePath { get; init; }

        /// <summary>
        /// Variables added to (or, with a null value, removed from) the inherited environment.
        /// Null or empty means plain inheritance.
        /// </summary>
        public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    }

    public static class Shell
    {
        /// <summary>
        /// PowerShell 7 if present, else Windows PowerShell 5.1. This machine only has 5.1;
        /// do not hardcode pwsh.exe.
        /// </summary>
        public static string ResolveExe()
        {
            foreach (var c in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "PowerShell", "7", "pwsh.exe"),
            })
                if (File.Exists(c)) return c;

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        /// <summary>
        /// Forces UTF-8 on the way in and out. Windows PowerShell 5.1 defaults to the OEM
        /// code page, which mangles CJK output through the PTY.
        /// </summary>
        public static string DefaultCommandLine() => CommandLineWith(null);

        /// <summary>
        /// Same shell, with one command run after the UTF-8 setup. Appending to -Command is
        /// deterministic; writing the same text to the pty right after Start() would race the
        /// shell's own startup.
        /// </summary>
        public static string CommandLineWith(string? initialCommand)
        {
            string exe = ResolveExe();
            const string init = "chcp 65001 > $null; " +
                                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                                "$OutputEncoding = [Text.Encoding]::UTF8";

            string script = string.IsNullOrWhiteSpace(initialCommand)
                ? init
                : init + "; " + initialCommand;

            // -Command takes one quoted argument, so inner quotes must be doubled.
            return $"\"{exe}\" -NoLogo -NoExit -Command \"{script.Replace("\"", "\\\"")}\"";
        }

        /// <summary>
        /// Where a bare command name resolves to on PATH, or null if it does not. Used only to
        /// tell the user a CLI is not installed *before* they pick it, rather than after a tab
        /// opens and prints "not recognized".
        ///
        /// PATHEXT matters more than usual here: claude and codex are npm shims, which on this
        /// machine are .ps1 and .cmd files, not .exe. Probing for .exe alone reports every CLI
        /// as missing.
        /// </summary>
        public static string? Which(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string cmd = command.Trim().Trim('"');

            if (cmd.Contains(Path.DirectorySeparatorChar) || cmd.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(cmd) ? Path.GetFullPath(cmd) : null;

            var exts = (Environment.GetEnvironmentVariable("PATHEXT")
                        ?? ".COM;.EXE;.BAT;.CMD")
                       .Split(';', StringSplitOptions.RemoveEmptyEntries)
                       .Select(e => e.Trim())
                       .ToList();
            // npm on Windows writes a .ps1 alongside the .cmd, and PATHEXT does not list it.
            if (!exts.Any(e => e.Equals(".PS1", StringComparison.OrdinalIgnoreCase))) exts.Add(".ps1");

            // The bare name is only probed when it already carries an extension. npm installs
            // three files side by side — "claude" (a bash shim), "claude.cmd" and "claude.ps1"
            // — and the extension-less one is not executable on Windows. Returning it would
            // report the CLI as installed and then hand PowerShell a file it cannot run.
            if (Path.HasExtension(cmd)) exts.Insert(0, "");

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                foreach (string ext in exts)
                {
                    string candidate;
                    try { candidate = Path.Combine(d, cmd + ext); }
                    catch (ArgumentException) { break; }   // a junk PATH entry
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }
}
