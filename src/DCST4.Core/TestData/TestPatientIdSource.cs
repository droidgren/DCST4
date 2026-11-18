using System;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.TestData
{
    /// <summary>Where an Auto Patient ID comes from, in Generate &amp; Send, in test-suite datasets and on the command line.</summary>
    public enum TestPatientIdSource
    {
        /// <summary>DCST4 and ten random digits, which cannot be read as a personnummer. The default, see <see cref="TestPatientIds.Default"/>.</summary>
        Random,
        /// <summary>A number from Skatteverket's list of test personnummer, see <see cref="TestPersonnummerRegistry"/>.</summary>
        TestPersonnummer,
    }

    /// <summary>
    /// The random Patient ID. It is not a personnummer: it starts with letters, so no date or check digit can be read
    /// from it. A personnummer only ever comes from <see cref="TestPersonnummerRegistry"/>.
    /// </summary>
    public static class TestPatientIds
    {
        /// <summary>What is used when nothing else is chosen.</summary>
        public const TestPatientIdSource Default = TestPatientIdSource.Random;

        public const string Prefix = "DCST4";

        /// <summary>How many digits follow <see cref="Prefix"/>.</summary>
        public const int DigitCount = 10;

        /// <summary>DCST4 and ten digits, 15 characters, within the 64 of LO.</summary>
        public static string Random(Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            var digits = new char[DigitCount];
            for (var i = 0; i < DigitCount; i++) digits[i] = (char)('0' + random.Next(10));
            return Prefix + new string(digits);
        }

        /// <summary>True for an ID made by <see cref="Random(System.Random)"/>.</summary>
        public static bool IsRandomId(string id) =>
            id != null
            && id.Length == Prefix.Length + DigitCount
            && id.StartsWith(Prefix, StringComparison.Ordinal)
            && id.Skip(Prefix.Length).All(c => c >= '0' && c <= '9');

        /// <summary>A day on which a person is aged within the range (inclusive) on <paramref name="today"/>, each day as likely.</summary>
        public static DateTime BirthDate(Random random, DateTime today, int minAge, int maxAge)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (minAge < 0 || maxAge < minAge) throw new InvalidOperationException($"The age range {minAge}-{maxAge} is not valid.");

            // The oldest is born the day after the (maxAge + 1)th birthday, the youngest on the minAge-th birthday.
            var day = today.Date;
            var latest = day.AddYears(-minAge);
            var earliest = day.AddYears(-(maxAge + 1)).AddDays(1);
            var span = (int)(latest - earliest).TotalDays;
            return earliest.AddDays(random.Next(span + 1));
        }

        /// <summary>M or F, each as likely.</summary>
        public static string Sex(Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            return random.Next(2) == 0 ? "M" : "F";
        }

        /// <summary>Reads a setting or a command line value: Random or TestPersonnummer, and on the command line also personnummer. Anything else gives false.</summary>
        public static bool TryParse(string text, out TestPatientIdSource source)
        {
            var trimmed = text?.Trim();
            foreach (TestPatientIdSource value in Enum.GetValues(typeof(TestPatientIdSource)))
            {
                if (string.Equals(value.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    source = value;
                    return true;
                }
            }
            if (string.Equals("personnummer", trimmed, StringComparison.OrdinalIgnoreCase))
            {
                source = TestPatientIdSource.TestPersonnummer;
                return true;
            }
            source = Default;
            return false;
        }

        /// <summary>For a log line or a report.</summary>
        public static string Describe(TestPatientIdSource source) =>
            source == TestPatientIdSource.Random
                ? string.Format(CultureInfo.InvariantCulture, "random ({0} and {1} digits)", Prefix, DigitCount)
                : "Swedish test personnummer";
    }
}
