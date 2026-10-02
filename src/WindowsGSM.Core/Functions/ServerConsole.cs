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
                        if (!process.HasExited && process.ProcessName == "7DaysToDieServer")
                        {
                            SetForegroundWindow(mainWindow);
                            var current = GetForegroundWindow();
                            var wgsmWindow = Process.GetCurrentProcess().MainWindowHandle;
                            if (current != wgsmWindow)
                            {
                                SendWaitToMainWindow("{TAB}");
                                SendWaitToMainWindow(text);
                                SendWaitToMainWindow("{TAB}");
                                SendWaitToMainWindow(text);
                                SendWaitToMainWindow("{ENTER}");
                                SetForegroundWindow(wgsmWindow);
                            }
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

        public static void SetMainWindow(IntPtr hWnd)
        {
            SetForegroundWindow(hWnd);
        }

        public static void SendWaitToMainWindow(string keys)
        {
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
