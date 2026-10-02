using CoreRCON;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    public class RconClient
    {
        private RCON Connection = null;
        public async Task<string> Connect(string ip, int port, string password)
        {
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "logs"));
            try
            {
                Connection?.Dispose();
                Connection = new RCON(GetEndpoint(ip, port), password);
                await Connection.ConnectAsync();
            }
            catch (Exception e)
            {
                Connection?.Dispose();
                Connection = null;
                string logPath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "logs");
                Directory.CreateDirectory(logPath);
                //we don't have the correct Log here, so we just return and let the caller take care of it
                return e.Message;
            }
            return "";
        }

        private static IPEndPoint GetEndpoint(string ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("RCON IP address is empty.");
            }

            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(port), $"RCON port must be between {IPEndPoint.MinPort} and {IPEndPoint.MaxPort}.");
            }

            if (IPAddress.TryParse(ip.Trim(), out IPAddress address))
            {
                return new IPEndPoint(address, port);
            }

            IPAddress resolvedAddress = Dns.GetHostAddresses(ip.Trim())
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork || x.AddressFamily == AddressFamily.InterNetworkV6);

            if (resolvedAddress == null)
            {
                throw new FormatException($"RCON host '{ip}' could not be resolved.");
            }

            return new IPEndPoint(resolvedAddress, port);
        }

        public void Disconnect()
        {
            try { Connection?.Dispose(); } catch { /* ignore */ }
            Connection = null;
        }

        public async Task<string> Send(string command)
        {
            if (Connection == null)
            {
                return "CONNECTION FAILED";
            }
            else if (!Connection.Connected)
            {
                return "CONNECTION FAILED";
            }

            var response = await Connection.SendCommandAsync(command, TimeSpan.FromSeconds(5));
            return response;
        }

        public static async Task<string> SendCommandAsync(string ip, int port, string password, string command)
        {
            RCON connection = null;

            try
            {
                connection = new RCON(GetEndpoint(ip, port), password);
                await connection.ConnectAsync();

                var response = await connection.SendCommandAsync(command, TimeSpan.FromSeconds(10));
                return response;
            }
            catch (Exception e)
            {
                return e.Message;
            }
            finally
            {
                connection?.Dispose();
            }

        }
    }
}
