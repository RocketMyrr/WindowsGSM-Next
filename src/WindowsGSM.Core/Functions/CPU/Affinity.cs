using System;
using System.Linq;
using System.Text;

namespace WindowsGSM.Functions.CPU
{
    static class Affinity
    {
        public static string GetAffinityValidatedString(string bits)
        {
            bits = bits ?? string.Empty;
            bits = (bits.Length < Environment.ProcessorCount) ? string.Concat(Enumerable.Repeat("1", Environment.ProcessorCount)) : bits;
            // NEXT: legacy used bits.Take(n).ToString(), which returns the iterator's type name (not the
            // characters), so an over-long saved mask became garbage.
            bits = (bits.Length > Environment.ProcessorCount) ? bits.Substring(0, Environment.ProcessorCount) : bits;

            StringBuilder sb = new StringBuilder();
            foreach (char bit in bits)
            {
                sb.Append((bit == '0') ? '0' : '1');
            }
            string returnBits = sb.ToString();

            // Cannot all '0', at least one '1'
            if (!returnBits.Contains("1"))
            {
                return string.Concat(Enumerable.Repeat("1", Environment.ProcessorCount));
            }

            return returnBits;
        }

        public static IntPtr GetAffinityIntPtr(string bits)
        {
            // NEXT: 64-bit mask. Legacy accumulated into an int with `1 << i`; C# masks the shift count to
            // 5 bits, so on machines with more than 32 logical processors cores 33+ wrapped onto cores 1+.
            // (A process affinity mask covers at most 64 processors — one processor group.)
            string validatedBits = GetAffinityValidatedString(bits);
            long affinity = 0;
            for (int i = 0; i < validatedBits.Length && i < 64; i++)
            {
                if (validatedBits[validatedBits.Length - 1 - i] == '1')
                {
                    affinity |= 1L << i;
                }
            }
            return (IntPtr)affinity;
        }
    }
}
