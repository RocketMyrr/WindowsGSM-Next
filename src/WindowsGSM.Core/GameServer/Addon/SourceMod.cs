using System.IO;
using System.Threading.Tasks;

namespace WindowsGSM.GameServer.Addon
{
    class SourceMod
    {
        // NEXT: the current stable branches (legacy pinned 1.10).
        private const string SourceModVersion = "1.12", MetaModVersion = "1.12";

        public static async Task<bool> Install(string serverId, string modFolder)
        {
            string path = Functions.ServerPath.GetServersServerFiles(serverId, modFolder);
            return await DropAsync(path, $"https://sm.alliedmods.net/smdrop/{SourceModVersion}", "sourcemod-latest-windows")
                && await DropAsync(path, $"https://mms.alliedmods.net/mmsdrop/{MetaModVersion}", "mmsource-latest-windows");
        }

        /// <summary>AlliedModders' "drop" folders name the newest zip in a small text file; downloads it and extracts it over <paramref name="path"/>.</summary>
        internal static async Task<bool> DropAsync(string path, string drop, string latestFile)
        {
            string zip = null;
            try
            {
                string fileName = (await Functions.Http.DownloadStringAsync($"{drop}/{latestFile}")).Trim();
                if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return false; }
                zip = Path.Combine(path, fileName);
                await Functions.Http.DownloadFileAsync($"{drop}/{fileName}", zip);
                // NEXT: overwrites (so a reinstall really updates) and never writes outside the folder.
                await Task.Run(() => WindowsGSM.Engine.Services.AddonService.ExtractSafely(zip, path));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (zip != null) { try { File.Delete(zip); } catch { } }
            }
        }
    }
}
