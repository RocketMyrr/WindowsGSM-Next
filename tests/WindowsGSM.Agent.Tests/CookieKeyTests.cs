namespace WindowsGSM.Agent.Tests;

/// <summary>Sign-in cookie keys are kept encrypted; plain ones from before are retired.</summary>
public class CookieKeyTests
{
    [Fact]
    public void Plain_keys_are_retired_and_encrypted_ones_kept()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "key-plain.xml"), "<key><descriptor><descriptor><masterKey><!-- Warning: the key below is in an unencrypted form. --><value>abc</value></masterKey></descriptor></descriptor></key>");
            File.WriteAllText(Path.Combine(dir, "key-sealed.xml"), "<key><descriptor><descriptor><encryptedSecret decryptorType=\"DpapiXmlDecryptor\"><encryptedKey><value>xyz</value></encryptedKey></encryptedSecret></descriptor></descriptor></key>");
            File.WriteAllText(Path.Combine(dir, "revocation-1.xml"), "<revocation />");

            Assert.Equal(1, AgentApp.RetirePlainKeys(dir));
            Assert.False(File.Exists(Path.Combine(dir, "key-plain.xml")));
            Assert.True(File.Exists(Path.Combine(dir, "key-sealed.xml")));
            Assert.True(File.Exists(Path.Combine(dir, "revocation-1.xml")));
            Assert.Equal(0, AgentApp.RetirePlainKeys(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
