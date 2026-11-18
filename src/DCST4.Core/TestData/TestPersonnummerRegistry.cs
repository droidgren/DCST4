using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DCST4.Core.TestData
{
    /// <summary>One valid personnummer with the values encoded in it.</summary>
    public sealed class Personnummer
    {
        internal Personnummer(string value, DateTime birthDate, string sex)
        {
            Value = value;
            BirthDate = birthDate;
            Sex = sex;
        }

        /// <summary>12 digits, no hyphen: YYYYMMDDNNNC.</summary>
        public string Value { get; }
        public DateTime BirthDate { get; }
        /// <summary>"M" or "F", from the second-to-last digit (odd = M, even = F).</summary>
        public string Sex { get; }

        /// <summary>Parses a 12-digit number and validates its date and Luhn check digit.</summary>
        public static bool TryParse(string text, out Personnummer result)
        {
            result = null;
            if (text == null || text.Length != 12 || !text.All(c => c >= '0' && c <= '9')) return false;
            if (!DateTime.TryParseExact(text.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthDate)) return false;
            if (!HasValidCheckDigit(text)) return false;
            result = new Personnummer(text, birthDate, (text[10] - '0') % 2 == 1 ? "M" : "F");
            return true;
        }

        /// <summary>Luhn over the last 10 digits, as Swedish personnummer are defined.</summary>
        private static bool HasValidCheckDigit(string twelveDigits)
        {
            var sum = 0;
            for (var i = 0; i < 10; i++)
            {
                var digit = twelveDigits[i + 2] - '0';
                if (i % 2 == 0)
                {
                    digit *= 2;
                    if (digit > 9) digit -= 9;
                }
                sum += digit;
            }
            return sum % 10 == 0;
        }
    }

    /// <summary>
    /// Skatteverket's official test personnummer, embedded in this assembly. This is the only source of random
    /// patient IDs: nothing in the program makes up a number. Refresh the list with tools\Update-TestPersonnummer.ps1.
    /// </summary>
    public sealed class TestPersonnummerRegistry
    {
        private const string ResourceName = "DCST4.Core.TestData.testpersonnummer.txt.gz";
        private static readonly Lazy<TestPersonnummerRegistry> LazyDefault = new Lazy<TestPersonnummerRegistry>(LoadEmbedded);

        private readonly Personnummer[] _all;
        private readonly HashSet<string> _values;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Personnummer[]> _candidates =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Personnummer[]>(StringComparer.Ordinal);

        /// <summary>Numbers rejected at load time because of an invalid date or check digit.</summary>
        public int RejectedCount { get; }
        public int Count => _all.Length;
        /// <summary>The comment line of the embedded list: source URL and fetch date.</summary>
        public string SourceDescription { get; }

        public static TestPersonnummerRegistry Default => LazyDefault.Value;

        public TestPersonnummerRegistry(IEnumerable<string> numbers, string sourceDescription = null)
        {
            var valid = new List<Personnummer>();
            var rejected = 0;
            foreach (var n in numbers)
            {
                if (Personnummer.TryParse(n, out var parsed)) valid.Add(parsed);
                else rejected++;
            }
            _all = valid.ToArray();
            _values = new HashSet<string>(valid.Select(p => p.Value), StringComparer.Ordinal);
            RejectedCount = rejected;
            SourceDescription = sourceDescription;
        }

        public bool Contains(string number) => number != null && _values.Contains(number);

        /// <summary>
        /// Picks a random number for a person already born on <paramref name="today"/>, aged within the range (inclusive).
        /// The list also holds future dates and people born in the 1890s, so this filter is needed for believable patients.
        /// </summary>
        public Personnummer Pick(Random random, DateTime today, int minAge, int maxAge)
        {
            // The filtered list is kept per day and age range, in the list's order, so the same draw gives the same person.
            var key = today.Date.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + minAge.ToString(CultureInfo.InvariantCulture) + "/" + maxAge.ToString(CultureInfo.InvariantCulture);
            var candidates = _candidates.GetOrAdd(key, k => _all.Where(p => IsWithinAge(p.BirthDate, today, minAge, maxAge)).ToArray());
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"The test personnummer list has no person aged {minAge}-{maxAge}.");
            }
            return candidates[random.Next(candidates.Length)];
        }

        /// <summary>
        /// Picks a person who was close to <paramref name="age"/> on <paramref name="onDate"/>, and of the given sex ("M" or
        /// "F") when the list has one. Used when real files are re-identified: the test patient is born before the study
        /// and is about as old as the patient replaced. The age is widened step by step until somebody is found, then the
        /// sex is let go, and with no age known anybody aged 0 to 100 on that date is picked.
        /// </summary>
        public Personnummer PickNear(Random random, DateTime onDate, int? age, string sex)
        {
            var sexes = sex == "M" || sex == "F" ? new[] { sex, null } : new string[] { null };
            foreach (var wanted in sexes)
            {
                if (age.HasValue)
                {
                    foreach (var window in AgeWindows)
                    {
                        var near = Candidates(onDate, Math.Max(0, age.Value - window), age.Value + window, wanted);
                        if (near.Length > 0) return near[random.Next(near.Length)];
                    }
                }
                var any = Candidates(onDate, 0, 100, wanted);
                if (any.Length > 0) return any[random.Next(any.Length)];
            }
            return Pick(random, onDate, 0, 100);
        }

        private static readonly int[] AgeWindows = { 0, 1, 2, 5, 10, 20 };

        /// <summary>Not cached, unlike <see cref="Pick(Random, DateTime, int, int)"/>: every study date and age of a folder would keep a list.</summary>
        private Personnummer[] Candidates(DateTime today, int minAge, int maxAge, string sex) =>
            _all.Where(p => (sex == null || p.Sex == sex) && IsWithinAge(p.BirthDate, today, minAge, maxAge)).ToArray();

        public static int AgeOn(DateTime birthDate, DateTime date)
        {
            var age = date.Year - birthDate.Year;
            if (date.Month < birthDate.Month || (date.Month == birthDate.Month && date.Day < birthDate.Day)) age--;
            return age;
        }

        private static bool IsWithinAge(DateTime birthDate, DateTime today, int minAge, int maxAge)
        {
            if (birthDate.Date > today.Date) return false;
            var age = AgeOn(birthDate, today);
            return age >= minAge && age <= maxAge;
        }

        private static TestPersonnummerRegistry LoadEmbedded()
        {
            var assembly = typeof(TestPersonnummerRegistry).GetTypeInfo().Assembly;
            using (var stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.ASCII))
                {
                    var lines = reader.ReadToEnd().Split('\n');
                    var description = lines.FirstOrDefault(l => l.StartsWith("#", StringComparison.Ordinal))?.TrimStart('#', ' ');
                    var numbers = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));
                    return new TestPersonnummerRegistry(numbers, description);
                }
            }
        }
    }
}
