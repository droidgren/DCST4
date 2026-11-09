using System;
using System.Globalization;

namespace DCST4.Core.TestData
{
    /// <summary>How test-suite datasets name the patient. The Patient ID comes from TestPersonnummerRegistry in both. Generate &amp; Send does not use this.</summary>
    public enum TestPatientNaming
    {
        /// <summary>An invented Swedish name, as Generate &amp; Send uses.</summary>
        RegistrySwedishName,
        /// <summary>DCST4^TEST^&lt;random number&gt;, which is plainly not a person.</summary>
        MarkedTestName,
    }

    public static class TestPatientNames
    {
        public const string MarkedPrefix = "DCST4^TEST^";

        /// <summary>The six letters that show whether a character set is handled correctly.</summary>
        public const string SwedishLetters = "åäöÅÄÖ";

        private const int MarkedNumberMin = 10000000;
        private const int MarkedNumberMax = 100000000;

        /// <summary>DCST4^TEST^ and eight digits.</summary>
        public static string Marked(Random random) =>
            MarkedPrefix + random.Next(MarkedNumberMin, MarkedNumberMax).ToString(CultureInfo.InvariantCulture);

        /// <summary>Reads a setting or a command line value. Anything else gives false.</summary>
        public static bool TryParse(string text, out TestPatientNaming naming)
        {
            foreach (TestPatientNaming value in Enum.GetValues(typeof(TestPatientNaming)))
            {
                if (string.Equals(value.ToString(), text?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    naming = value;
                    return true;
                }
            }
            naming = TestPatientNaming.RegistrySwedishName;
            return false;
        }
    }
}
