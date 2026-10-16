using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindowsGSM.GameServer.Addon
{
    class AMXModX
    {
        public static async Task<bool> Install(string serverId, string modFolder)
        {
            string path = Functions.ServerPath.GetServersServerFiles(serverId, modFolder);

            // Install AMX MOD X
            if (!await SourceMod.DropAsync(path, "https://www.amxmodx.org/amxxdrop/1.10", "amxmodx-latest-base-windows"))
            {
                return false;
            }

            // Install MetaMod-P
            string MMDllPath = Path.Combine(path, "addons", "metamod", "dlls");
            Directory.CreateDirectory(MMDllPath);
            if (!await Install_MetaMod_P(MMDllPath))
            {
                return false;
            }

            try
            {
                // Edit liblist.gam
                string liblistPath = Path.Combine(path, "liblist.gam");
                File.WriteAllText(liblistPath, Regex.Replace(File.ReadAllText(liblistPath), "gamedll.\".*?\"", @"gamedll ""addons\metamod\dlls\metamod.dll"""));

                // Create plugins.ini
                File.WriteAllText(Path.Combine(path, "addons", "metamod", "plugins.ini"), "win32 addons/amxmodx/dlls/amxmodx_mm.dll");
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static async Task<bool> Install_MetaMod_P(string MMFolder)
        {
            string version = "1.21p37";
            string fileName = $"metamod-p-{version}-windows.zip";
            string filePath = Path.Combine(MMFolder, fileName);
            try
            {
                await Functions.Http.DownloadFileAsync($"https://downloads.sourceforge.net/project/metamod-p/Metamod-P%20Binaries/{version}/{fileName}", filePath);
                // NEXT: overwrites (so a reinstall really updates) and never writes outside the folder.
                await Task.Run(() => WindowsGSM.Engine.Services.AddonService.ExtractSafely(filePath, MMFolder));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(filePath); } catch { }
            }
        }
    }
}
