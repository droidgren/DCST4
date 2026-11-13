using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Codecs.JpegXl
{
    /// <summary>
    /// A canonical prefix code over a small alphabet, described in the stream the way JPEG XL reads it, which is the
    /// Brotli format (ISO/IEC 18181-1 C.2.2, RFC 7932 3.4 and 3.5).
    /// </summary>
    internal sealed class JxlPrefixCode
    {
        private const int MaxCodeLength = 15;
        private const int MaxCodeLengthCodeLength = 5;
        private static readonly int[] CodeLengthCodeOrder = { 1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        /// <summary>How a code length code length is written: the bits, lowest first, and how many (RFC 7932 3.5).</summary>
        private static readonly int[] LengthCodeBits = { 0, 7, 3, 2, 1, 15 };
        private static readonly int[] LengthCodeCount = { 2, 4, 3, 2, 2, 4 };

        private readonly int[] _lengths;
        private readonly int[] _reversedCodes;

        /// <param name="counts">How often each symbol occurs. Its length is the alphabet size, which must be at least 2.</param>
        public JxlPrefixCode(long[] counts)
        {
            if (counts.Length < 2) throw new ArgumentException("The alphabet must have at least two symbols.", nameof(counts));
            AlphabetSize = counts.Length;
            UsedSymbols = Enumerable.Range(0, counts.Length).Where(s => counts[s] > 0).ToArray();
            if (UsedSymbols.Length == 0) throw new ArgumentException("No symbol is used.", nameof(counts));

            _lengths = UsedSymbols.Length == 1 ? new int[counts.Length] : LimitedLengths(counts, MaxCodeLength);
            _reversedCodes = ReversedCanonicalCodes(_lengths, MaxCodeLength);
        }

        public int AlphabetSize { get; }
        private int[] UsedSymbols { get; }

        /// <summary>Writes a symbol. With a single used symbol that takes no bits.</summary>
        public void WriteSymbol(JxlBitWriter writer, int symbol)
        {
            if (UsedSymbols.Length == 1)
            {
                if (symbol != UsedSymbols[0]) throw new ArgumentOutOfRangeException(nameof(symbol));
                return;
            }
            var length = _lengths[symbol];
            if (length == 0) throw new ArgumentOutOfRangeException(nameof(symbol), $"Symbol {symbol} has no code.");
            writer.Write(length, _reversedCodes[symbol]);
        }

        /// <summary>The description of the code. The decoder knows the alphabet size from before.</summary>
        public void WriteDescription(JxlBitWriter writer)
        {
            if (UsedSymbols.Length == 1)
            {
                // A simple code (simple_code_or_skip 1) with one symbol, which then costs no bits.
                writer.Write(2, 1);
                writer.Write(2, 0);
                writer.Write(SimpleSymbolBits(AlphabetSize), UsedSymbols[0]);
                return;
            }

            // A complex code that skips no code length code lengths.
            writer.Write(2, 0);

            var last = Array.FindLastIndex(_lengths, l => l != 0);
            var lengthCounts = new long[18];
            for (var symbol = 0; symbol <= last; symbol++) lengthCounts[_lengths[symbol]]++;
            var usedLengthSymbols = lengthCounts.Count(c => c > 0);

            int[] lengthCodeLengths;
            if (usedLengthSymbols == 1)
            {
                // A single code length symbol is coded with no bits. Its own length can be anything but zero.
                lengthCodeLengths = new int[18];
                lengthCodeLengths[Array.FindIndex(lengthCounts, c => c > 0)] = 1;
            }
            else
            {
                lengthCodeLengths = LimitedLengths(lengthCounts, MaxCodeLengthCodeLength);
            }

            // The decoder stops reading code length code lengths once they fill the code, so the trailing zeros are left
            // out. With a single code length symbol the code never fills, and all 18 are written.
            var space = 32;
            foreach (var index in CodeLengthCodeOrder)
            {
                var value = lengthCodeLengths[index];
                writer.Write(LengthCodeCount[value], LengthCodeBits[value]);
                if (value != 0)
                {
                    space -= 32 >> value;
                    if (space <= 0) break;
                }
            }

            if (usedLengthSymbols == 1) return;
            var lengthCodes = ReversedCanonicalCodes(lengthCodeLengths, MaxCodeLengthCodeLength);
            for (var symbol = 0; symbol <= last; symbol++)
            {
                var value = _lengths[symbol];
                writer.Write(lengthCodeLengths[value], lengthCodes[value]);
            }
        }

        private static int SimpleSymbolBits(int alphabetSize)
        {
            var bits = 0;
            while ((1 << bits) < alphabetSize) bits++;
            return bits;
        }

        /// <summary>Huffman code lengths no longer than <paramref name="maxLength"/>, by package-merge. At least two symbols must be used.</summary>
        internal static int[] LimitedLengths(long[] counts, int maxLength)
        {
            var symbols = Enumerable.Range(0, counts.Length).Where(s => counts[s] > 0).OrderBy(s => counts[s]).ThenBy(s => s).ToArray();
            if (symbols.Length < 2) throw new ArgumentException("At least two symbols must be used.", nameof(counts));
            if (symbols.Length > 1 << maxLength) throw new ArgumentException("Too many symbols for the length limit.", nameof(counts));

            var leaves = symbols.Select(s => new Item(counts[s], new[] { s })).ToList();
            var current = leaves;
            for (var level = 1; level < maxLength; level++)
            {
                var packages = new List<Item>();
                for (var i = 0; i + 1 < current.Count; i += 2) packages.Add(new Item(current[i].Weight + current[i + 1].Weight, current[i].Symbols.Concat(current[i + 1].Symbols).ToArray()));
                current = Merge(leaves, packages);
            }

            var lengths = new int[counts.Length];
            foreach (var item in current.Take(2 * symbols.Length - 2))
            {
                foreach (var symbol in item.Symbols) lengths[symbol]++;
            }
            return lengths;
        }

        private static List<Item> Merge(List<Item> a, List<Item> b)
        {
            var merged = new List<Item>(a.Count + b.Count);
            int i = 0, j = 0;
            while (i < a.Count || j < b.Count)
            {
                // Leaves before packages of the same weight, so that the result does not depend on how ties fall.
                if (j >= b.Count || (i < a.Count && a[i].Weight <= b[j].Weight)) merged.Add(a[i++]);
                else merged.Add(b[j++]);
            }
            return merged;
        }

        /// <summary>
        /// Canonical codes: shorter first, and by symbol within a length (RFC 7932 3.2). The decoder reads a code from its
        /// first bit, which this writer puts lowest, so each code is stored with its bits reversed.
        /// </summary>
        private static int[] ReversedCanonicalCodes(int[] lengths, int maxLength)
        {
            var countPerLength = new int[maxLength + 1];
            foreach (var length in lengths) if (length > 0) countPerLength[length]++;

            var next = new int[maxLength + 2];
            var code = 0;
            for (var length = 1; length <= maxLength; length++)
            {
                code = (code + countPerLength[length - 1]) << 1;
                next[length] = code;
            }

            var codes = new int[lengths.Length];
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol];
                if (length == 0) continue;
                codes[symbol] = Reverse(next[length]++, length);
            }
            return codes;
        }

        private static int Reverse(int code, int length)
        {
            var reversed = 0;
            for (var i = 0; i < length; i++)
            {
                reversed = (reversed << 1) | (code & 1);
                code >>= 1;
            }
            return reversed;
        }

        private sealed class Item
        {
            public Item(long weight, int[] symbols)
            {
                Weight = weight;
                Symbols = symbols;
            }

            public long Weight { get; }
            public int[] Symbols { get; }
        }
    }
}
