using System.Security.Principal;

namespace CloudGI.Host.Platform;

internal static class WindowsHostSession
{
    // Local names are scoped to the Windows logon session. Keep this marker alive for
    // the managed child Host, so a second normal launch cannot consume the parent mode.
    private static string ChildMarker => @"Local\CloudGI.Host.Child." + WindowsIdentity.GetCurrent().User!.Value;

    public static bool IsManagedChildSession
    {
        get
        {
            if (!Mutex.TryOpenExisting(ChildMarker, out var marker)) return false;
            marker.Dispose();
            return true;
        }
    }

    public static Mutex ClaimChildSession()
    {
        var marker = new Mutex(false, ChildMarker, out var created);
        if (created) return marker;
        marker.Dispose();
        throw new InvalidOperationException("当前后台桌面中已经有一个受主 Host 管理的子 Host。");
    }
}
