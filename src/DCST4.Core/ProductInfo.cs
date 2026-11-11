using System.Reflection;

namespace DCST4.Core
{
    public static class ProductInfo
    {
        public const string ProjectUrl = "https://github.com/droidgren/DCST4";

        /// <summary>The name of the program as a reader who has never seen it should meet it, for a report.</summary>
        public const string FullName = "DICOM C-Store Tester 4 (DCST4)";

        /// <summary>The version of this build. Read from Core so that every front-end reports the same value.</summary>
        public static string Version { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var assembly = typeof(ProductInfo).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString();
        }
    }
}
