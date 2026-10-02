using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WindowsGSM.Launcher
{
    /// <summary>
    /// Start / stop / restart the agent from the Start menu (WindowsGSM.exe --agent-start | --agent-stop |
    /// --agent-restart). Stopping leaves game servers running — they're picked up again when the agent starts.
    /// Stopping uses the agent's local-only endpoint with the key it writes for this computer's apps.
    /// </summary>
    internal static class AgentControl
    {
        private static void TrustLocalAgent()
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
                sender is HttpWebRequest r && (r.RequestUri.IsLoopback || r.RequestUri.Host == "localhost"); // our own agent, maybe self-signed
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>Setup: is the agent for this data folder answering?</summary>
        public static bool IsAgentRunning(string dataRoot)
        {
            TrustLocalAgent();
            return IsRunning(AgentUrl(dataRoot));
        }

        /// <summary>Setup: stops the agent the normal way (game servers keep running). Null when stopped, else why not.</summary>
        public static string StopAgent(Install install)
        {
            TrustLocalAgent();
            Uri url = AgentUrl(install.Data);
            return IsRunning(url) ? Stop(install, url) : null;
        }

        public static int Run(Install install, string action, bool quiet, string waitPid)
        {
            TrustLocalAgent();

            if (!string.IsNullOrEmpty(waitPid) && int.TryParse(waitPid, out int pid))
            {
                try { using (var p = Process.GetProcessById(pid)) { p.WaitForExit(60000); } } catch (ArgumentException) { }
                Thread.Sleep(1000);
            }

            Uri url = AgentUrl(install.Data);
            bool running = IsRunning(url);
            switch (action)
            {
                case "start":
                    if (running) { return Say(quiet, "The WindowsGSM agent is already running.", MessageBoxIcon.Information, 0); }
                    return Start(install, url, quiet);

                case "stop":
                    if (!running) { return Say(quiet, "The WindowsGSM agent isn't running.", MessageBoxIcon.Information, 0); }
                    string stopError = Stop(install, url);
                    return stopError == null
                        ? Say(quiet, "The WindowsGSM agent stopped. Your game servers keep running — they're picked up again when it starts.", MessageBoxIcon.Information, 0)
                        : Say(quiet, stopError, MessageBoxIcon.Warning, 1);

                default: // restart
                    if (running)
                    {
                        string error = Stop(install, url);
                        if (error != null) { return Say(quiet, error, MessageBoxIcon.Warning, 1); }
                    }
                    return Start(install, url, quiet);
            }
        }

        private static int Start(Install install, Uri url, bool quiet)
        {
            install.Start(install.AgentExe, new string[0], hidden: true);
            // A first start can take a while (plugins compile).
            for (int i = 0; i < 90; i++)
            {
                Thread.Sleep(1000);
                if (IsRunning(url)) { return Say(quiet, "The WindowsGSM agent is running.", MessageBoxIcon.Information, 0); }
            }
            return Say(quiet, "The agent didn't answer within 90 seconds. Check its log in " + Path.Combine(install.Data, "logs") + ".", MessageBoxIcon.Warning, 1);
        }

        private static string Stop(Install install, Uri url)
        {
            string keyFile = Path.Combine(install.Data, "configs", "next", "local-control.key");
            if (!File.Exists(keyFile)) { return "Couldn't find the agent's key (" + keyFile + ")."; }
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(new Uri(url, "api/v2/local/stop-agent"));
                req.Method = "POST";
                req.Headers.Add("X-WGSM-CSRF", "1");
                req.Headers.Add("X-WGSM-Local-Key", File.ReadAllText(keyFile).Trim());
                req.ContentLength = 0;
                req.Timeout = 10000;
                using (req.GetResponse()) { }
            }
            catch (WebException ex) { return "The agent refused to stop: " + ex.Message; }
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(1000);
                if (!IsRunning(url)) { return null; }
            }
            return "The agent didn't stop within a minute.";
        }

        private static bool IsRunning(Uri url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(new Uri(url, "api/v2/info"));
                req.Timeout = 3000;
                using (var res = (HttpWebResponse)req.GetResponse()) { return (int)res.StatusCode < 500; }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r) { return (int)r.StatusCode < 500; }
            catch { return false; }
        }

        /// <summary>The agent's local address, from its settings (as the desktop app reads them).</summary>
        private static Uri AgentUrl(string dataRoot)
        {
            int port = 8971;
            bool https = false;
            try
            {
                string file = Path.Combine(dataRoot ?? "", "configs", "next", "agent.json");
                if (File.Exists(file))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    if (d.TryGetValue("Port", out object p) && p is int n && n > 0 && n < 65536) { port = n; }
                    https = (d.TryGetValue("UseHttps", out object h) && h is bool b1 && b1) || (d.TryGetValue("AcmeEnabled", out object a) && a is bool b2 && b2);
                }
            }
            catch { /* the defaults are what a fresh agent uses */ }
            return new Uri((https ? "https" : "http") + "://localhost:" + port + "/");
        }

        private static int Say(bool quiet, string text, MessageBoxIcon icon, int code)
        {
            if (!quiet) { MessageBox.Show(text, "WindowsGSM", MessageBoxButtons.OK, icon); }
            return code;
        }
    }
}
