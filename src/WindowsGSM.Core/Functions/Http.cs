using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    public static class Http
    {
        private static readonly HttpClient Client = CreateClient(false);
        private static readonly HttpClient CompressedClient = CreateClient(true);

        /// <summary>
        /// For addresses someone typed in (custom add-ons): connects only to addresses that aren't this PC or
        /// link-local — so a URL (or a redirect, or a name that resolves there) can't make WindowsGSM fetch from
        /// itself, or from a cloud host's metadata service at 169.254.169.254. Your own network (a NAS) is fine.
        /// </summary>
        private static readonly HttpClient OutsideClient = CreateOutsideClient();

        private static HttpClient CreateOutsideClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                    var allowed = addresses.Where(a => !Blocked(a) || (AllowThisPcForTests && IPAddress.IsLoopback(a))).ToArray();
                    if (allowed.Length == 0) { throw new HttpRequestException($"Downloads from {context.DnsEndPoint.Host} aren't allowed (it's this PC or a link-local address)."); }
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return client;
        }

        /// <summary>Tests serve their downloads from this PC.</summary>
        internal static bool AllowThisPcForTests { get; set; }

        /// <summary>This PC (loopback, "any"), link-local (incl. 169.254.169.254) or multicast.</summary>
        public static bool Blocked(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) { a = a.MapToIPv4(); }
            if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) { return true; }
            if (a.AddressFamily == AddressFamily.InterNetworkV6) { return a.IsIPv6LinkLocal || a.IsIPv6Multicast; }
            byte[] b = a.GetAddressBytes();
            return b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
        }

        /// <summary><see cref="DownloadFileAsync"/> for an address someone typed in (see OutsideClient).</summary>
        public static Task DownloadUserUrlAsync(string url, string path) => DownloadFileAsync(OutsideClient, url, path);

        private static HttpClient CreateClient(bool automaticDecompression)
        {
            var handler = new HttpClientHandler();
            if (automaticDecompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }

            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return client;
        }

        public static Task<string> DownloadStringAsync(string url, bool automaticDecompression = false)
        {
            return (automaticDecompression ? CompressedClient : Client).GetStringAsync(url);
        }

        public static string DownloadString(string url, bool automaticDecompression = false)
        {
            return DownloadStringAsync(url, automaticDecompression).GetAwaiter().GetResult();
        }

        public static Task DownloadFileAsync(string url, string path, bool automaticDecompression = false) =>
            DownloadFileAsync(automaticDecompression ? CompressedClient : Client, url, path);

        private static async Task DownloadFileAsync(HttpClient client, string url, string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(path))
                {
                    await input.CopyToAsync(output);
                }
            }
        }

        public static async Task<HttpResponseMessage> PostFormAsync(string url, string form)
        {
            using (var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"))
            {
                return await Client.PostAsync(url, content);
            }
        }

        public static async Task<HttpResponseMessage> PostJsonAsync(string url, string json)
        {
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                return await Client.PostAsync(url, content);
            }
        }

        public static async Task<(bool ok, string body, HttpStatusCode status)> GetStringWithHeadersAsync(string url, IEnumerable<KeyValuePair<string, string>> headers = null, bool automaticDecompression = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null)
            {
                foreach (var h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            using var response = await (automaticDecompression ? CompressedClient : Client).SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode, body, response.StatusCode);
        }
    }
}