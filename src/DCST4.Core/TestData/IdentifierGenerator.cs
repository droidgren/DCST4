using FellowOakDicom;
using System;
using System.Globalization;

namespace DCST4.Core.TestData
{
    /// <summary>Random identifiers in the form and length DICOM allows.</summary>
    public static class IdentifierGenerator
    {
        /// <summary>SH, 14 characters: DC + yyMMdd + 6 digits.</summary>
        public static string AccessionNumber(Random random, DateTime date) =>
            "DC" + date.ToString("yyMMdd", CultureInfo.InvariantCulture) + Digits(random, 6);

        /// <summary>SH, 8 digits.</summary>
        public static string StudyId(Random random) => Digits(random, 8);

        public static string Uid() => DicomUID.Generate().UID;

        private static string Digits(Random random, int count)
        {
            var chars = new char[count];
            for (var i = 0; i < count; i++) chars[i] = (char)('0' + random.Next(10));
            return new string(chars);
        }
    }
}
