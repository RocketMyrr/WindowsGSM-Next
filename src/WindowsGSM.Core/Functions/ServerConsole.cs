using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace WindowsGSM.Functions
{
    public class ServerConsole
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_CHAR = 0x0102;
        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;

        private const int MAX_LINE = 500;
        private readonly List<string> _consoleList = new List<string>();
        private readonly List<string> _recorderConsoleList = new List<string>();
        private readonly string _serverId;
        private int _lineNumber = 0;

        public string JoinCodeLine = "";

        public ServerConsole(string serverId)
        {
            _serverId = serverId;
        }

        public ServerConsole(int serverId)
        {
            _serverId = serverId.ToString();
        }

        // NEXT: no UI thread to marshal onto any more. Output arrives on process-reader threads and is
        // read by API/WebSocket threads, so every buffer access below goes through this lock.
        private readonly object _gate = new object();

        /// <summary>
        /// Raised (outside the lock) when a line looks like a join code. The engine routes it to
        /// notifications — the legacy version reached into the WPF MainWindow to send a Discord webhook.
        /// </summary>
        public static event Action<string, string> JoinCodeDetected; // (serverId, line)

        // NEXT: one canonical console per server. Plugins create their own throwaway
        // `new ServerConsole(id)` and wire game output to its AddOutput — legacy forwarded that into
        // MainWindow._serverMetadata[id].ServerConsole. This registry is that forwarding target.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerConsole> _canonical =
            new System.Collections.Concurrent.ConcurrentDictionary<string, ServerConsole>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The server's canonical console buffer (created on first use).</summary>
        public static ServerConsole For(string serverId) => _canonical.GetOrAdd(serverId, id => new ServerConsole(id));

        public void AddOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data != null) { For(_serverId).Add(args.Data); }
        }

        public async void Input(Process process, string text, IntPtr mainWindow)
        {
            if (!process.HasExited)
            {
                if (process.StartInfo.RedirectStandardInput)
                {
                    try
                    {
                        process.StandardInput.WriteLine(text);
                        Add(text);
                    }
                    catch
                    {
                        //ignore
                    }
                }
                else
                {
                    await Task.Run(() =>
                    {
                        // NEXT: 7 Days to Die's graphical window needs legacy's keyboard dance. It now goes through
                        // SetMainWindow, so the keys are aimed at that window (and only sent when it's in front);
                        // a console window of its own is typed into like any other.
                        if (!process.HasExited && process.ProcessName == "7DaysToDieServer" && Engine.Services.ConsoleWindows.GameOf(mainWindow) == null)
                        {
                            SetMainWindow(mainWindow);
                            SendWaitToMainWindow("{TAB}");
                            SendWaitToMainWindow(text);
                            SendWaitToMainWindow("{TAB}");
                            SendWaitToMainWindow(text);
                            SendWaitToMainWindow("{ENTER}");
                        }
                        else
                        {
                            SendMessageToMainWindow(mainWindow, text);
                        }
                    });
                }
            }
        }

        // Monotonic count of lines ever added (never rolls back) plus a generation bumped by Clear(), so a
        // polling or streaming reader can ask for "everything after line N" even though the buffer itself
        // only keeps the last MAX_LINE lines.
        private long _seq;
        private int _generation;

        /// <summary>Raised (outside the lock) for every line appended to the buffer — feeds live console streams.</summary>
        public static event Action<string, string> LineAdded; // (serverId, line)

        public void Clear()
        {
            lock (_gate)
            {
                _consoleList.Clear();
                _generation++;
            }
        }

        /// <summary>
        /// Lines added after position <paramref name="since"/>. When the caller's position is stale — another
        /// generation (the console was cleared, e.g. on restart), or so far behind that the rolling buffer has
        /// already dropped those lines — returns the whole buffer with reset = true.
        /// </summary>
        public (List<string> lines, long seq, int generation, bool reset) GetSince(long since, int generation)
        {
            lock (_gate)
            {
                long seq = _seq;
                if (generation != _generation || since < 0 || since > seq || seq - since > _consoleList.Count)
                {
                    return (new List<string>(_consoleList), seq, _generation, true);
                }
                int take = (int)(seq - since);
                return (_consoleList.GetRange(_consoleList.Count - take, take), seq, _generation, false);
            }
        }

        public string Get()
        {
            lock (_gate) { return string.Join(Environment.NewLine, _consoleList); }
        }

        public string GetPreviousCommand()
        {
            lock (_gate)
            {
                --_lineNumber;
                return (_consoleList.Count == 0) ? string.Empty : _consoleList[LineNumberLocked()];
            }
        }

        public string GetNextCommand()
        {
            lock (_gate)
            {
                ++_lineNumber;
                return (_consoleList.Count == 0) ? string.Empty : _consoleList[LineNumberLocked()];
            }
        }

        public int GetLineNumber()
        {
            lock (_gate) { return LineNumberLocked(); }
        }

        private int LineNumberLocked()
        {
            if (_lineNumber < 0)
            {
                _lineNumber = 0;
            }
            else if (_lineNumber >= _consoleList.Count)
            {
                _lineNumber = (_consoleList.Count <= 0) ? 0 : _consoleList.Count - 1;
            }

            return _lineNumber;
        }

        public void Add(string text)
        {
            bool joinCode = false, added = false;
            lock (_gate)
            {
                if (_serverId == "0")
                {
                    _lineNumber = _consoleList.Count + 1;

                    if (_consoleList.Count > 0 && text == _consoleList[_consoleList.Count - 1])
                    {
                        return;
                    }
                }

                if (_recorderConsoleList.Any())
                {
                    _recorderConsoleList.Add(text);

                    if (_recorderConsoleList.Count > MAX_LINE)
                    {
                        _recorderConsoleList.RemoveAt(0);
                    }
                }

                //check for known join codes
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (text.Contains("join code", StringComparison.InvariantCultureIgnoreCase) || text.Contains("joincode", StringComparison.InvariantCultureIgnoreCase))
                {
                    JoinCodeLine = text;
                    joinCode = true;
                }

                _consoleList.Add(text);
                _seq++;
                added = true;
                if (_consoleList.Count > MAX_LINE)
                {
                    _consoleList.RemoveAt(0);
                }
            }

            // Handlers run outside the lock so a slow subscriber can't stall the process reader.
            if (added) { LineAdded?.Invoke(_serverId, text); }
            if (joinCode) { JoinCodeDetected?.Invoke(_serverId, text); }
        }

 

        public static void SendMessageToMainWindow(IntPtr hWnd, string message)
        {
            // NEXT: a game's own console window: the text goes straight into its console's input (see ConsoleHost.TypeInto).
            if (Engine.Services.ConsoleWindows.GameOf(hWnd) is int pid && ConsoleHost.TypeInto(pid, message + "\r", hWnd)) { return; }

            // Here is a minor error on PostMessage, when it sends repeated char, some char may disappear. Example: send 1111111, windows may receive 1111 or 11111
            for (int i = 0; i < message.Length; i++)
            {
                // This is the solution for the error stated above
                if (i > 0 && message[i] == message[i - 1])
                {
                    // Send a None key, break the repeat bug
                    PostMessage(hWnd, WM_KEYDOWN, (IntPtr)Keys.None, (IntPtr)0);
                }

                PostMessage(hWnd, WM_CHAR, (IntPtr)message[i], (IntPtr)0);
            }

            // Send enter
            PostMessage(hWnd, WM_KEYDOWN, (IntPtr)Keys.Enter, (IntPtr)(0 << 29 | 0));
        }

        // The window a plugin last picked with SetMainWindow (per thread: plugins pick, then press, on one thread).
        [ThreadStatic] private static IntPtr _keysTarget;

        public static void SetMainWindow(IntPtr hWnd)
        {
            _keysTarget = hWnd;
            // NEXT: a game's own console window (often hidden) is reached directly — see SendWaitToMainWindow.
            if (Engine.Services.ConsoleWindows.GameOf(hWnd) != null) { return; }
            SetForegroundWindow(hWnd);
        }

        public static void SendWaitToMainWindow(string keys)
        {
            // NEXT: legacy simulated the keyboard (SendKeys), which only works if the game's window could be brought to
            // the front. A hidden window can't, and Windows often refuses a background program anyway — so a plugin's
            // Ctrl+C or "stop" could land in whatever app the person at the PC was using. For a game's own console
            // window the keys are delivered to that console instead: Ctrl+C as a real Ctrl+C, the rest as typing.
            if (Engine.Services.ConsoleWindows.GameOf(_keysTarget) is int pid)
            {
                SendKeysToConsole(_keysTarget, pid, keys);
                return;
            }
            // Simulated keys go to whatever window is in front. Only send them when that's the game's: with no window
            // picked, or Windows refusing to bring it forward (it usually does for a program in the background), they'd
            // land in whatever app the person at the PC is using — a Ctrl+C or "stop" typed into someone's work. The
            // stop then falls back to Ctrl+C / kill instead.
            if (_keysTarget == IntPtr.Zero || GetForegroundWindow() != _keysTarget) { return; }
            try
            {
                SendKeys.SendWait(keys);
            }
            catch
            {
                /*
                    System.ComponentModel.Win32Exception (0x80004005): Access is denied
                    at System.Windows.Forms.SendKeys.SendInput(Byte[] oldKeyboardState, Queue previousEvents)
                    at System.Windows.Forms.SendKeys.Send(String keys, Control control, Boolean wait)
                    at System.Windows.Forms.SendKeys.SendWait(String keys)

                    This error may happen in Windows Server R2, UAC problem, not sure how to fix

                    https://github.com/WindowsGSM/WindowsGSM/issues/14
                */
            }
        }

        /// <summary>The SendKeys notation plugins use, delivered to a game's console window: ^c, {ENTER}/~, {TAB}, text.</summary>
        internal static void SendKeysToConsole(IntPtr hWnd, int pid, string keys)
        {
            // Typed text collects here ('\r' = Enter) and goes in one piece, before a Ctrl+C and at the end.
            var typed = new System.Text.StringBuilder();
            void Flush()
            {
                if (typed.Length == 0) { return; }
                string text = typed.ToString();
                typed.Clear();
                if (ConsoleHost.TypeInto(pid, text, hWnd)) { return; }
                for (int k = 0; k < text.Length; k++) // fallback: post it to the window
                {
                    if (text[k] == '\r') { PostMessage(hWnd, WM_KEYDOWN, (IntPtr)Keys.Enter, IntPtr.Zero); continue; }
                    if (k > 0 && text[k] == text[k - 1]) { PostMessage(hWnd, WM_KEYDOWN, (IntPtr)Keys.None, IntPtr.Zero); } // see SendMessageToMainWindow
                    PostMessage(hWnd, WM_CHAR, (IntPtr)text[k], IntPtr.Zero);
                }
            }

            int i = 0;
            while (i < keys.Length)
            {
                if (keys[i] == '^')
                {
                    // ^c, ^C, ^(c): Ctrl+C. Other Ctrl combinations aren't something a console game needs.
                    string rest = keys.Substring(i + 1);
                    int used = rest.StartsWith("(c)", StringComparison.OrdinalIgnoreCase) ? 3 : rest.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    if (used > 0) { Flush(); ProcessManagement.SendCtrlC(pid); }
                    i += 1 + used;
                    continue;
                }
                if (keys[i] == '{')
                {
                    int end = keys.IndexOf('}', i + 2); // "{}}" is a literal }
                    if (end < 0) { break; }
                    string key = keys.Substring(i + 1, end - i - 1).ToUpperInvariant();
                    if (key == "ENTER") { typed.Append('\r'); }
                    else if (key == "TAB") { typed.Append('\t'); }
                    else if (key.Length == 1) { typed.Append(keys[i + 1]); } // {+}, {^}, {%}…
                    i = end + 1;
                    continue;
                }
                if (keys[i] == '~') { typed.Append('\r'); i++; continue; }
                if (keys[i] is '+' or '%') { i++; continue; } // Shift/Alt modifiers: the typed character already carries its case
                typed.Append(keys[i]);
                i++;
            }
            Flush();
        }

        public void StartRecorder()
        {
            lock (_gate)
            {
                _recorderConsoleList.Clear();
                _recorderConsoleList.Add("Start:");
            }
        }

        public string StopRecorder()
        {
            lock (_gate)
            {
                var text = string.Join(Environment.NewLine, _recorderConsoleList);
                _recorderConsoleList.Clear();
                return text;
            }
        }
    }
}
