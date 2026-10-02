using System.Security.Cryptography;
using System.Text.Json;
using WindowsGSM.Agent.Security;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// "I can't sign in": <c>wgsm-agent --reset-password &lt;user&gt; [--disable-2fa]</c>, run on the machine itself.
/// Gives the account a new temporary password (printed once), signs it out everywhere and clears a lockout;
/// with --disable-2fa also turns two-factor off (a lost phone). Being able to run it means having this
/// Windows account and the data folder — the same trust as editing the files by hand, minus the editing.
/// </summary>
public static class PasswordReset
{
    public sealed record Result(bool Ok, string Message);

    public static Result Run(string dataRoot, string username, bool disableTwoFactor)
    {
        if (!Directory.Exists(dataRoot)) { return new(false, $"There's no data folder at {dataRoot}."); }

        // users.json belongs to the running agent — it would overwrite a change made behind its back.
        DataRootLock held;
        try { held = DataRootLock.Acquire(dataRoot); }
        catch (DataRootInUseException)
        {
            return new(false, "WindowsGSM is running on this folder. Stop it first — tray icon → \"Stop everything and quit\" — then run this again.");
        }

        using (held)
        {
            string configDir = Path.Combine(dataRoot, "configs", "next");
            if (!File.Exists(Path.Combine(configDir, "users.json"))) { return new(false, $"No WindowsGSM accounts in {dataRoot} yet — just open the panel and create the owner account."); }
            var users = new UserStore(configDir);
            var user = users.Get(username);
            if (user == null)
            {
                return new(false, $"There's no account called \"{username}\". Accounts here: {string.Join(", ", users.All().Select(u => u.Username))}.");
            }

            string password = NewPassword();
            string? problem = users.ResetCredentials(user.Username, password, disableTwoFactor);
            if (problem != null) { return new(false, problem); }
            int signedOut = new SessionStore(configDir, TimeSpan.FromDays(1)).RemoveAllForUser(user.Username);

            string twoFactor = user.TwoFactorEnabled
                ? disableTwoFactor ? " Two-factor sign-in is now off — turn it on again under Your account." : " Two-factor sign-in is still on (add --disable-2fa if you lost the phone)."
                : string.Empty;
            return new(true,
                $"New password for {user.Username}:  {password}\n\n" +
                $"Sign in with it, then change it under Your account.{twoFactor}" +
                (user.Enabled ? string.Empty : " The account was disabled and is now enabled again.") +
                (signedOut > 0 ? $" {signedOut} old session(s) were signed out." : string.Empty));
        }
    }

    /// <summary>The data folder of an installed copy (install.json two levels up from versions\&lt;v&gt;\), if this is one.</summary>
    public static string? InstalledDataRoot(string exeDir)
    {
        try
        {
            string file = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "install.json"));
            if (!File.Exists(file)) { return null; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "data", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) { return p.Value.GetString(); }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>16 characters that are easy to read and type: no 0/O, 1/l/I.</summary>
    private static string NewPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[16];
        for (int i = 0; i < chars.Length; i++) { chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]; }
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4) + "-" + new string(chars, 8, 4) + "-" + new string(chars, 12, 4);
    }
}
