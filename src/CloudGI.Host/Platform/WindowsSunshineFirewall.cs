using System.Diagnostics;
using System.Security.Principal;

namespace CloudGI.Host.Platform;

internal static class WindowsSunshineFirewall
{
    private static string RuleName => "CloudGI Sunshine - " + WindowsIdentity.GetCurrent().User!.Value;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "Sunshine", "sunshine.exe");

    public static bool IsConfigured()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            bool sunshine = false, host = false;
            foreach (dynamic rule in policy.Rules)
            {
                if ((string)rule.Name == RuleName && (bool)rule.Enabled &&
                    string.Equals((string)rule.ApplicationName, Executable, StringComparison.OrdinalIgnoreCase)) sunshine = true;
                if ((string)rule.Name == RuleName + " Control" && (bool)rule.Enabled &&
                    string.Equals((string)rule.ApplicationName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) host = true;
            }
            return sunshine && host;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }

    // Runs only in the elevated helper invocation of this Host executable.
    public static void Configure()
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("Sunshine 文件不存在。");
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        rule.Name = RuleName;
        rule.Description = "CloudGI bundled Sunshine; inbound traffic from the local subnet only.";
        rule.ApplicationName = Executable;
        rule.Protocol = 256; // Any protocol; scoped to this exact executable and local subnet.
        rule.Direction = 1;
        rule.Action = 1;
        rule.Profiles = int.MaxValue;
        rule.RemoteAddresses = "LocalSubnet";
        rule.Enabled = true;
        // Rule name is private to CloudGI. Installed Sunshine rules remain untouched.
        policy.Rules.Remove(RuleName);
        policy.Rules.Add(rule);
        dynamic control = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        control.Name = RuleName + " Control";
        control.Description = "CloudGI automatic pairing; local subnet only.";
        control.ApplicationName = Environment.ProcessPath!;
        control.Protocol = 6;
        control.Direction = 1;
        control.Action = 1;
        control.Profiles = int.MaxValue;
        control.RemoteAddresses = "LocalSubnet";
        control.Enabled = true;
        policy.Rules.Remove(RuleName + " Control");
        policy.Rules.Add(control);
    }

    public static async Task EnableAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--configure-streaming-firewall")
        { UseShellExecute = true, Verb = "runas" }) ?? throw new IOException("无法打开 Windows 授权窗口。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !IsConfigured()) throw new IOException("未能配置局域网访问规则，请重试。");
    }
}
