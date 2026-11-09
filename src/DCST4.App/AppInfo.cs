using DCST4.Core;
using System.Reflection;

namespace DCST4
{
    public static class AppInfo
    {
        /// <summary>The program's name as shown in title bars and the Help menu.</summary>
        public const string DisplayName = "DICOM C-Store Tester 4 (DCST4)";

        public const string ProjectUrl = ProductInfo.ProjectUrl;

        /// <summary>GitHub's answer to "what is the newest release", which leaves out drafts and prereleases.</summary>
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/droidgren/DCST4/releases/latest";

        /// <summary>What Windows knows the program by. Must match the ID the installer puts on the Start menu shortcut.</summary>
        public const string AppUserModelId = "VGR.DCST4";

        public static string CurrentVersion
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString();
            }
        }
    }
}
