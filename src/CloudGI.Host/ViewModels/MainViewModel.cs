using System.Reflection;
using System.Runtime.InteropServices;

namespace CloudGI.Host.ViewModels;

public sealed class MainViewModel
{
    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public string Platform =>
        $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    public string Runtime => RuntimeInformation.FrameworkDescription;
}
