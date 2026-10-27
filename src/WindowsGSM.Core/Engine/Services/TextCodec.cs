#nullable enable
using System.Text;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// Reads a text file in the encoding it was written in, so saving it back changes only what was edited: UTF-16 or
    /// UTF-8 (each with its byte-order mark kept), else an older Windows code page. NEXT: files were read as UTF-8 only,
    /// so saving an older-format config turned its accented letters into "\uFFFD" for good.
    /// </summary>
    public static class TextCodec
    {
        public static (string Text, Encoding Encoding) Decode(byte[] bytes)
        {
            if (HasUtf16Bom(bytes))
            {
                var utf16 = bytes[0] == 0xFF ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true) : new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                return (utf16.GetString(bytes, 2, bytes.Length - 2), utf16);
            }
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int skip = bom ? 3 : 0;
            try { return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip), new UTF8Encoding(bom)); }
            // shortcut: Latin-1 for every older code page — every byte comes back exactly as it was, though characters
            // typed in that aren't in Latin-1 (€ in Windows-1252) save as "?"; use the real code page if that matters.
            catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(bytes), Encoding.Latin1); }
        }

        public static bool HasUtf16Bom(byte[] bytes) => bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));

        /// <summary>Text from the editor (browsers send "\n") with the file's own line endings.</summary>
        public static string KeepLineEndings(string original, string edited) =>
            original.Contains("\r\n") ? edited.Replace("\r\n", "\n").Replace("\n", "\r\n") : edited;
    }
}
