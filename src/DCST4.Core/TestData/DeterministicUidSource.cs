using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DCST4.Core.TestData
{
    /// <summary>
    /// UIDs derived from a root, a seed and counters, so that the same input always gives the same UIDs.
    /// The context holds the seed and everything that must separate one dataset from another, such as the transfer
    /// syntax: two variants of a dataset must never share a SOP Instance UID on a PACS.
    /// </summary>
    public sealed class DeterministicUidSource : IUidSource
    {
        /// <summary>The UUID-derived arc of PS3.5 B.2, which needs no registered organisation root.</summary>
        public const string UuidRoot = "2.25";
        /// <summary>Leaves at least 23 digits for the hashed value, within the 64 characters of a UID.</summary>
        public const int MaxRootLength = 40;
        public const int MaxUidLength = 64;

        private const int ShaBytesInUuid = 16;

        private readonly string _root;
        private readonly string _context;

        /// <param name="root">A UID root, or null or empty for <see cref="UuidRoot"/>.</param>
        /// <param name="context">The seed and whatever else identifies the dataset.</param>
        public DeterministicUidSource(string root, string context)
        {
            _root = string.IsNullOrWhiteSpace(root) ? UuidRoot : root.Trim();
            if (_root.Length > MaxRootLength) throw new ArgumentException($"A UID root can have at most {MaxRootLength} characters.", nameof(root));
            _context = context ?? string.Empty;
        }

        /// <summary>The same root, with counters added to the context.</summary>
        public DeterministicUidSource Within(string scope) => new DeterministicUidSource(_root, _context + "/" + scope);

        public string Uid(string scope)
        {
            var hash = SeededValues.Sha256(_context + "|" + scope);
            return _root == UuidRoot ? UuidRoot + "." + UuidValue(hash) : _root + "." + DecimalValue(hash, MaxUidLength - _root.Length - 1);
        }

        /// <summary>The hash as a 128-bit UUID with its version and variant bits set: version 8, "custom", from RFC 9562.</summary>
        private static string UuidValue(byte[] hash)
        {
            var uuid = hash.Take(ShaBytesInUuid).ToArray();
            uuid[6] = (byte)((uuid[6] & 0x0F) | 0x80);
            uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
            return Unsigned(uuid).ToString(CultureInfo.InvariantCulture);
        }

        private static string DecimalValue(byte[] hash, int maxDigits) =>
            (Unsigned(hash) % BigInteger.Pow(10, maxDigits)).ToString(CultureInfo.InvariantCulture);

        /// <summary>Big-endian bytes as a non-negative number. BigInteger reads little-endian, and a top zero byte keeps it positive.</summary>
        private static BigInteger Unsigned(byte[] bigEndian) => new BigInteger(bigEndian.Reverse().Concat(new byte[] { 0 }).ToArray());
    }

    /// <summary>Numbers and random sequences that follow from a text, for reproducible test data.</summary>
    public static class SeededValues
    {
        public static byte[] Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            }
        }

        public static int Int(string text) => BitConverter.ToInt32(Sha256(text), 0);

        /// <summary>A random sequence of its own for this text, so that one value does not depend on how many others were drawn.</summary>
        public static Random Random(string text) => new Random(Int(text));
    }
}
