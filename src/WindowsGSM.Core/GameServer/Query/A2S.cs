using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.GameServer.Query
{
    public class A2S : QueryTemplate
    {
        // A2S header bytes
        private const byte A2S_INFO_HEADER = 0x54; // 'T'
        private const byte A2S_PLAYER_HEADER = 0x55; // 'U'
        private const byte A2S_RULES_HEADER = 0x56; // 'V'

        // Response headers
        private const byte SOURCE_INFO_RESPONSE = 0x49; // 'I'
        private const byte GOLDSOURCE_INFO_RESPONSE = 0x6D; // 'm'
        private const byte PLAYER_RESPONSE = 0x44; // 'D'
        private const byte CHALLENGE_RESPONSE = 0x41; // 'A'

        private static readonly byte[] A2S_INFO_PAYLOAD = Encoding.ASCII.GetBytes("Source Engine Query\0");
        private static readonly byte[] FF_FF_FF_FF = { 0xFF, 0xFF, 0xFF, 0xFF };

        private IPEndPoint _IPEndPoint;
        private int _timeout;

        public A2S() { }

        public A2S(string address, int port, int timeout = 5)
        {
            SetAddressPort(address, port, timeout);
        }

        public void SetAddressPort(string address, int port, int timeout = 5)
        {
            _IPEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
            _timeout = timeout * 1000;
        }

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        /// <summary>Retrieves information about the server (name, map, players, etc.).</summary>
        public async Task<Dictionary<string, string>> GetInfo()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var udp = new UdpClientHandler(_IPEndPoint))
                    {
                        byte[] request = BuildInfoRequest(challenge: null);
                        byte[] payload = SendAndReceive(udp, request);
                        if (payload == null) return null;

                        const int maxChallengeRetries = 3;
                        int attempts = 0;
                        while (payload.Length >= 5 && payload[0] == CHALLENGE_RESPONSE && attempts < maxChallengeRetries)
                        {
                            byte[] challenge = new byte[4];
                            Buffer.BlockCopy(payload, 1, challenge, 0, 4);

                            request = BuildInfoRequest(challenge);
                            payload = SendAndReceive(udp, request);
                            if (payload == null) return null;

                            attempts++;
                        }

                        if (payload.Length >= 1 && payload[0] == CHALLENGE_RESPONSE)
                        {
                            System.Diagnostics.Debug.WriteLine($"A2S GetInfo for {_IPEndPoint}: server kept challenging after {maxChallengeRetries} retries.");
                            return null;
                        }

                        return ParseInfoPayload(payload);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"A2S GetInfo failed for {_IPEndPoint}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>Retrieves information about the players currently on the server.</summary>
        public async Task<List<PlayerData>> GetPlayersData()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var udp = new UdpClientHandler(_IPEndPoint))
                    {
                        byte[] request = BuildPlayerRequest(FF_FF_FF_FF);
                        byte[] payload = SendAndReceive(udp, request);
                        if (payload == null) return null;

                        const int maxChallengeRetries = 3;
                        int attempts = 0;
                        while (payload.Length >= 5 && payload[0] == CHALLENGE_RESPONSE && attempts < maxChallengeRetries)
                        {
                            byte[] challenge = new byte[4];
                            Buffer.BlockCopy(payload, 1, challenge, 0, 4);

                            request = BuildPlayerRequest(challenge);
                            payload = SendAndReceive(udp, request);
                            if (payload == null) return null;

                            attempts++;
                        }

                        if (payload.Length >= 1 && payload[0] == PLAYER_RESPONSE)
                        {
                            return ParsePlayerPayload(payload);
                        }

                        System.Diagnostics.Debug.WriteLine($"A2S GetPlayersData for {_IPEndPoint}: no player data after {attempts} challenge attempt(s).");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"A2S GetPlayersData failed for {_IPEndPoint}: {ex.Message}");
                    return null;
                }
            });
        }

        public async Task<string> GetPlayersAndMaxPlayers()
        {
            try
            {
                var kv = await GetInfo();
                if (kv != null && kv.ContainsKey("Players") && kv.ContainsKey("MaxPlayers"))
                    return kv["Players"] + '/' + kv["MaxPlayers"];
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Request builders
        // ---------------------------------------------------------------

        private static byte[] BuildInfoRequest(byte[] challenge)
        {
            // 0xFFFFFFFF 0x54 "Source Engine Query\0" [optional 4-byte challenge]
            int len = 4 + 1 + A2S_INFO_PAYLOAD.Length + (challenge != null ? 4 : 0);
            var buf = new byte[len];
            int o = 0;
            Buffer.BlockCopy(FF_FF_FF_FF, 0, buf, o, 4); o += 4;
            buf[o++] = A2S_INFO_HEADER;
            Buffer.BlockCopy(A2S_INFO_PAYLOAD, 0, buf, o, A2S_INFO_PAYLOAD.Length); o += A2S_INFO_PAYLOAD.Length;
            if (challenge != null)
            {
                Buffer.BlockCopy(challenge, 0, buf, o, 4);
            }
            return buf;
        }

        private static byte[] BuildPlayerRequest(byte[] challenge)
        {
            // 0xFFFFFFFF 0x55 <4 bytes challenge>
            var buf = new byte[4 + 1 + 4];
            Buffer.BlockCopy(FF_FF_FF_FF, 0, buf, 0, 4);
            buf[4] = A2S_PLAYER_HEADER;
            Buffer.BlockCopy(challenge, 0, buf, 5, 4);
            return buf;
        }

        // ---------------------------------------------------------------
        // UDP send/receive with split-packet reassembly
        // ---------------------------------------------------------------

        /// <summary>
        /// Sends a UDP request via the handler, then reads either a single (-1) packet
        /// or reassembles a split (-2) packet stream. Returns the inner payload (with
        /// the leading -1 single-packet header stripped). Rust commonly sends split
        /// responses up to 64KB across many UDP packets.
        /// </summary>
        private byte[] SendAndReceive(UdpClientHandler udp, byte[] request)
        {
            byte[] first = udp.GetResponse(request, request.Length, _timeout, _timeout).ToArray();
            if (first == null || first.Length < 4) return null;

            int header = BitConverter.ToInt32(first, 0);
            if (header == -1)
            {
                // Single packet — strip the 4-byte header
                var payload = new byte[first.Length - 4];
                Buffer.BlockCopy(first, 4, payload, 0, payload.Length);
                return payload;
            }

            if (header == -2)
            {
                // Split packet — reassemble using ReceiveMore on the same socket
                return ReassembleSplit(udp, first);
            }

            return null;
        }

        /// <summary>
        /// Reassembles a Source-engine split packet response. Layout per packet:
        ///   int32  -2 (0xFFFFFFFE)         header
        ///   int32  ID                      (high bit set = compressed; Rust does not compress)
        ///   byte   total packets
        ///   byte   packet number (0-based)
        ///   int16  split size (max packet size used by server)
        ///   bytes  payload chunk
        /// The first packet's payload begins with the standard -1 single-packet
        /// header (0xFFFFFFFF) followed by the actual response byte (0x49, 0x41, etc).
        /// </summary>
        private byte[] ReassembleSplit(UdpClientHandler udp, byte[] firstPacket)
        {
            // NEXT: GoldSource (Half-Life 1 engine) servers split differently — one byte packs the packet number and
            // count, and there's no size field. Told apart by where the inner -1 header sits in packet 0.
            bool goldSource = IsGoldSourceSplit(firstPacket);
            if (!TryParseSplitPacket(firstPacket, goldSource, out int id, out byte total, out byte number, out byte[] chunk))
                return null;

            if (total == 0) return null;
            if ((id & 0x80000000) != 0)
            {
                // Compressed (bzip2) — Rust doesn't use this, so we don't implement it here.
                return null;
            }

            var chunks = new byte[total][];
            var seen = new bool[total];

            if (number >= total) return null;
            chunks[number] = chunk;
            seen[number] = true;
            int receivedCount = 1;

            // Pull remaining packets via ReceiveMore (no resend)
            while (receivedCount < total)
            {
                byte[] pkt = udp.ReceiveMore(_timeout);
                if (pkt == null || pkt.Length < 4) return null;

                int hdr = BitConverter.ToInt32(pkt, 0);
                if (hdr != -2) continue; // ignore stray single-packet replies

                if (!TryParseSplitPacket(pkt, goldSource, out int pid, out byte ptotal, out byte pnumber, out byte[] pchunk))
                    return null;

                if (pid != id || ptotal != total || pnumber >= total) continue;
                if (seen[pnumber]) continue;

                chunks[pnumber] = pchunk;
                seen[pnumber] = true;
                receivedCount++;
            }

            // Concatenate chunks in order
            int totalLen = 0;
            for (int i = 0; i < total; i++) totalLen += chunks[i].Length;

            var assembled = new byte[totalLen];
            int o = 0;
            for (int i = 0; i < total; i++)
            {
                Buffer.BlockCopy(chunks[i], 0, assembled, o, chunks[i].Length);
                o += chunks[i].Length;
            }

            // The reassembled stream starts with the standard -1 single-packet header — strip it
            if (assembled.Length >= 4 && BitConverter.ToInt32(assembled, 0) == -1)
            {
                var payload = new byte[assembled.Length - 4];
                Buffer.BlockCopy(assembled, 4, payload, 0, payload.Length);
                return payload;
            }

            return assembled;
        }

        private static bool IsGoldSourceSplit(byte[] pkt)
        {
            // Source: -2, id(4), total(1), number(1), size(2), payload — packet 0's payload starts at 12 with FF FF FF FF.
            // GoldSource: -2, id(4), number<<4|total(1), payload — packet 0's payload starts at 9.
            bool ffAt(int i) => pkt.Length >= i + 4 && pkt[i] == 0xFF && pkt[i + 1] == 0xFF && pkt[i + 2] == 0xFF && pkt[i + 3] == 0xFF;
            return pkt.Length > 9 && (pkt[8] >> 4) == 0 && ffAt(9) && !ffAt(12);
        }

        private static bool TryParseSplitPacket(byte[] pkt, bool goldSource, out int id, out byte total, out byte number, out byte[] chunk)
        {
            id = 0; total = 0; number = 0; chunk = null;

            if (goldSource)
            {
                if (pkt.Length < 9) return false;
                id = BitConverter.ToInt32(pkt, 4);
                total = (byte)(pkt[8] & 0x0F);
                number = (byte)(pkt[8] >> 4);
                chunk = new byte[pkt.Length - 9];
                Buffer.BlockCopy(pkt, 9, chunk, 0, chunk.Length);
                return true;
            }

            // -2 (4) + id (4) + total (1) + number (1) + size (2) = 12 bytes minimum
            if (pkt.Length < 12) return false;

            id = BitConverter.ToInt32(pkt, 4);
            total = pkt[8];
            number = pkt[9];
            // pkt[10..12] is split size — not needed for reassembly
            int dataOffset = 12;
            chunk = new byte[pkt.Length - dataOffset];
            Buffer.BlockCopy(pkt, dataOffset, chunk, 0, chunk.Length);
            return true;
        }

        // ---------------------------------------------------------------
        // Payload parsers
        // ---------------------------------------------------------------

        private Dictionary<string, string> ParseInfoPayload(byte[] payload)
        {
            var keys = new Dictionary<string, string>();

            using (var br = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                byte header = br.ReadByte();

                if (header == SOURCE_INFO_RESPONSE)
                {
                    keys["Header"] = header.ToString();
                    keys["Protocol"] = br.ReadByte().ToString();
                    keys["Name"] = ReadString(br);
                    keys["Map"] = ReadString(br);
                    keys["Folder"] = ReadString(br);
                    keys["Game"] = ReadString(br);
                    keys["ID"] = br.ReadInt16().ToString();
                    keys["Players"] = br.ReadByte().ToString();
                    keys["MaxPlayers"] = br.ReadByte().ToString();
                    keys["Bots"] = br.ReadByte().ToString();
                    char c = (char)br.ReadByte();
                    keys["ServerType"] = c == 'd' ? "Dedicated" : c == 'l' ? "Listen" : "SourceTV";
                    c = (char)br.ReadByte();
                    keys["Environment"] = c == 'w' ? "Windows" : c == 'l' ? "Linux" : "Mac";
                    keys["Visibility"] = br.ReadByte() != 0 ? "Private" : "Public";
                    keys["VAC"] = br.ReadByte() != 0 ? "Secured" : "Unsecured";

                    if (int.TryParse(keys["ID"], out int appId) && appId == 2400) // The Ship
                    {
                        keys["Mode"] = br.ReadByte().ToString();
                        keys["Witnesses"] = br.ReadByte().ToString();
                        keys["Duration"] = br.ReadByte().ToString();
                    }

                    keys["Version"] = ReadString(br);

                    // EDF flags — original had `== 1` which is wrong for any flag except 0x01
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        byte edf = br.ReadByte();
                        if ((edf & 0x80) != 0) { keys["Port"] = br.ReadInt16().ToString(); }
                        if ((edf & 0x10) != 0) { keys["SteamID"] = br.ReadUInt64().ToString(); }
                        if ((edf & 0x40) != 0)
                        {
                            keys["SpectatorPort"] = br.ReadInt16().ToString();
                            keys["SpectatorName"] = ReadString(br);
                        }
                        if ((edf & 0x20) != 0)
                        {
                            keys["Keywords"] = ReadString(br);

                            if (keys.TryGetValue("Game", out var game) && game == "Mordhau")
                            {
                                var tags = keys["Keywords"].Split(',');
                                foreach (var tag in tags)
                                {
                                    if (tag.Length > 2 && tag[0] == 'B' && tag[1] == ':' &&
                                        int.TryParse(tag.Replace("B:", string.Empty), out int players))
                                    {
                                        keys["Players"] = players.ToString();
                                        break;
                                    }
                                }
                            }
                        }
                        if ((edf & 0x01) != 0) { keys["GameID"] = br.ReadUInt64().ToString(); }
                    }
                }
                else if (header == GOLDSOURCE_INFO_RESPONSE)
                {
                    keys["Header"] = header.ToString();
                    keys["Address"] = ReadString(br);
                    keys["Name"] = ReadString(br);
                    keys["Map"] = ReadString(br);
                    keys["Folder"] = ReadString(br);
                    keys["Game"] = ReadString(br);
                    keys["Players"] = br.ReadByte().ToString();
                    keys["MaxPlayers"] = br.ReadByte().ToString();
                    keys["Protocol"] = br.ReadByte().ToString();
                    char c = char.ToLower((char)br.ReadByte());
                    keys["ServerType"] = c == 'd' ? "Dedicated" : c == 'l' ? "Listen" : "HLTV";
                    c = (char)br.ReadByte();
                    keys["Environment"] = c == 'w' ? "Windows" : c == 'l' ? "Linux" : "Mac";
                    keys["Visibility"] = br.ReadByte() != 0 ? "Private" : "Public";
                    bool isMod = br.ReadByte() != 0;
                    keys["Mod"] = isMod.ToString();

                    if (isMod)
                    {
                        keys["Link"] = ReadString(br);
                        keys["DownloadLink"] = ReadString(br);
                        br.ReadByte();
                        keys["Version"] = br.ReadInt32().ToString();
                        keys["Size"] = br.ReadInt32().ToString();
                        keys["Type"] = br.ReadByte().ToString();
                        keys["DLL"] = br.ReadByte().ToString();
                    }

                    keys["VAC"] = br.ReadByte() != 0 ? "Secured" : "Unsecured";
                    keys["Bots"] = br.ReadByte().ToString();
                }
                else
                {
                    return null;
                }
            }

            return keys.Count <= 0 ? null : keys;
        }

        private List<PlayerData> ParsePlayerPayload(byte[] payload)
        {
            var list = new List<PlayerData>();
            using (var br = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                byte header = br.ReadByte();
                if (header != PLAYER_RESPONSE) return null;

                int players = br.ReadByte();
                // NEXT: the count byte caps at 255 and some games send more entries, or fewer than they say; read
                // entries while whole ones remain instead of trusting it (a short last entry used to throw and lose
                // the entire list).
                for (int i = 0; br.BaseStream.Length - br.BaseStream.Position >= 10; i++)
                {
                    br.ReadByte(); // index (server-reported, often 0)
                    string name = ReadString(br);
                    if (br.BaseStream.Length - br.BaseStream.Position < 8) break;
                    int score = br.ReadInt32();
                    float rawTime = br.ReadSingle();
                    TimeSpan timeConnected = (float.IsNaN(rawTime) || rawTime < 0)
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds((int)rawTime);
                    list.Add(new PlayerData(i, name, score, timeConnected));
                }
            }
            return list;
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static string ReadString(BinaryReader br)
        {
            using (var ms = new MemoryStream())
            {
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    byte b = br.ReadByte();
                    if (b == 0x00) break;
                    ms.WriteByte(b);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}