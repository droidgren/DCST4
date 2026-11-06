using System.Reflection;

namespace DCST4.ViewModels
{
    public class AboutViewModel
    {
        private static Assembly Assembly => Assembly.GetExecutingAssembly();

        public string Version => "Version " + (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Assembly.GetName().Version.ToString());
        public string Company => Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? string.Empty;
        public string Copyright => Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
    }
}
