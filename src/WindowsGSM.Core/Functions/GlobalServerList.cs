using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    static class GlobalServerList
    {
        public static async Task<bool> IsServerOnSteamServerList(string publicIP, string port)
        {
            try
            {
                string json = await WindowsGSM.Functions.Http.DownloadStringAsync($"http://api.steampowered.com/ISteamApps/GetServersAtAddress/v0001?addr={publicIP}&format=json");
                return json.Contains($"\"addr\":\"{publicIP}:{port}\"");
            }
            catch
            {
                return false;
            }
        }
    }
}
