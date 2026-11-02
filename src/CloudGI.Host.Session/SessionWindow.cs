// ActiveX host/connection adapted from mattxslv/annex, MIT; see third-party/Annex.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;
using CloudGI.Host.Core.Streaming;
using Microsoft.Win32;

namespace CloudGI.Host.Session;

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsRdpExtendedSettings
{
    void put_Property([MarshalAs(UnmanagedType.BStr)] string name, ref object value);
    void get_Property([MarshalAs(UnmanagedType.BStr)] string name, out object value);
}

sealed class RdpBox : AxHost
{
    public RdpBox() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") { }
    public object Ocx { get { return GetOcx(); } }
}

static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool WTSEnableChildSessions(bool enable);
    [DllImport("wtsapi32.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool WTSIsChildSessionsEnabled(out bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool WTSGetChildSessionId(out uint id);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr server, uint id, bool wait);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    public static uint Child() { uint id; return WTSGetChildSessionId(out id) ? id : uint.MaxValue; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    public static string ProcessPath(int id)
    {
        // Module enumeration can return null while a process starts in another session.
        // Only request the limited query right, and treat not-yet-available paths as pending.
        using (var handle = OpenProcess(0x1000, false, id))
        {
            if (handle.IsInvalid) return null;
            var path = new StringBuilder(32768);
            uint size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) ? path.ToString() : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SessionInfo { public uint Id; public IntPtr Name; public int State; }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr entries, out uint count);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, uint id, int type, out IntPtr data, out uint size);

    public static bool LoggedOn(uint id)
    {
        IntPtr data; uint size;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, id, 5, out data, out size)) return false;
        try { if (size < 2 || string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(data))) return false; }
        finally { WTSFreeMemory(data); }
        if (!WTSQuerySessionInformationW(IntPtr.Zero, id, 8, out data, out size)) return false;
        try { return size >= 4 && Marshal.ReadInt32(data) == 0; } // WTSActive, with a logged-on user.
        finally { WTSFreeMemory(data); }
    }

    public static bool SessionExists(uint id)
    {
        // WTSGetChildSessionId can retain a reserved ID even when no logon session exists.
        // An assigned ID alone must not prevent creating the actual child desktop.
        IntPtr entries; uint count;
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out entries, out count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认已有 Windows 会话，已停止创建后台桌面。");
        try
        {
            int size = Marshal.SizeOf<SessionInfo>();
            for (uint i = 0; i < count; i++)
                if (Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(entries, checked((int)i * size))).Id == id) return true;
            return false;
        }
        finally { WTSFreeMemory(entries); }
    }
}

sealed class SessionWindow : Form
{
    readonly Process parent;
    readonly string hostPath, pipeName, directory;
    readonly ChildDisplaySettings display;
    readonly Timer timer = new Timer { Interval = 500 };
    readonly Stopwatch connecting = Stopwatch.StartNew();
    readonly Label status = new Label { Dock = DockStyle.Top, Height = 55, TextAlign = ContentAlignment.MiddleLeft };
    readonly RdpBox rdp = new RdpBox { Dock = DockStyle.Fill };
    readonly NotifyIcon tray;
    dynamic client;
    uint child;
    bool agentStarted, ending, enabledByUs;
    object oldSuppress;
    bool suppressChanged;
    string taskName;
    object registeredTask;
    long lastLaunchAttempt;
    string stage = "初始化";
    Form login;
    bool connectRequested;

    public SessionWindow(Process parent, string hostPath, string pipeName, string directory, ChildDisplaySettings display)
    {
        this.parent = parent; this.hostPath = hostPath; this.pipeName = pipeName; this.directory = directory; this.display = display;
        Text = "CloudGI · Annex 后台桌面（实验）"; ClientSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(rdp); Controls.Add(status);
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示后台桌面", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("结束子会话", null, (s, e) => Close());
        tray = new NotifyIcon { Text = "CloudGI 后台桌面", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        Shown += (s, e) => Connect();
        timer.Tick += (s, e) => Poll();
        FormClosing += (s, e) => Cleanup();
    }

    void Connect()
    {
        try
        {
            timer.Start();
            // Never attach to a child desktop created by BetterGI, Annex, or another Host.
            uint existing = Native.Child();
            if (existing != uint.MaxValue && existing != 0 && Native.SessionExists(existing))
                throw new InvalidOperationException("Windows 已存在子会话（ID " + existing + "）。请先正常关闭原有桌面分身，再重试。");
            stage = "启用 Windows 子会话";
            bool enabled;
            if (!Native.WTSIsChildSessionsEnabled(out enabled)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!enabled)
            {
                if (!Native.WTSEnableChildSessions(true)) throw new Win32Exception(Marshal.GetLastWin32Error());
                enabledByUs = true;
            }
            // Annex keeps RDP rendering while minimized. Restore the original setting at exit.
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client"))
            {
                oldSuppress = key.GetValue("RemoteDesktop_SuppressWhenMinimized");
                key.SetValue("RemoteDesktop_SuppressWhenMinimized", 2, RegistryValueKind.DWord);
                suppressChanged = true;
            }
            stage = "初始化 RDP ActiveX 控件";
            rdp.CreateControl(); client = rdp.Ocx;
            using (var identity = WindowsIdentity.GetCurrent()) client.UserName = identity.Name;
            client.Server = "localhost";
            stage = "设置后台分辨率";
            client.DesktopWidth = display.Width; client.DesktopHeight = display.Height;
            Optional(() => client.ColorDepth = 32);
            Optional(() => client.AdvancedSettings2.SmartSizing = true);
            Optional(() => client.AdvancedSettings7.EnableCredSspSupport = true);
            Optional(() => client.AdvancedSettings6.RedirectClipboard = false);
            stage = "设置 ConnectToChildSession";
            var extended = (IMsRdpExtendedSettings)rdp.Ocx;
            object childMode = true; extended.put_Property("ConnectToChildSession", ref childMode);
            stage = "设置 RDP 缩放";
            Optional(() => new RdpScaleSettings(name => { object value; extended.get_Property(name, out value); return value; },
                (name, value) => extended.put_Property(name, ref value)).Apply(display.Scale));
            status.Text = "正在建立独立桌面…如出现凭据窗口，请输入 Windows 账户密码（不是 PIN）。";
            stage = "Windows 子会话登录";
            using (var credentials = new Form { Text = "CloudGI · 后台桌面登录", ClientSize = new Size(480, 205),
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false, MinimizeBox = false })
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    credentials.Controls.Add(new Label { Text = "Windows 账户：" + identity.Name, Left = 20, Top = 18, Width = 440, Height = 26 });
                credentials.Controls.Add(new Label { Text = "请输入账户密码（不是 PIN）。仅用于本次子会话，不保存。", Left = 20, Top = 50, Width = 440, Height = 35 });
                var password = new TextBox { Left = 20, Top = 92, Width = 440, UseSystemPasswordChar = true };
                var ok = new Button { Text = "连接", Left = 275, Top = 145, Width = 88, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 373, Top = 145, Width = 88, DialogResult = DialogResult.Cancel };
                credentials.Controls.AddRange(new Control[] { password, ok, cancel });
                credentials.AcceptButton = ok; credentials.CancelButton = cancel;
                credentials.Shown += (s, e) => password.Focus();
                login = credentials;
                var result = credentials.ShowDialog(this);
                login = null;
                if (result != DialogResult.OK)
                    throw new OperationCanceledException("已取消 Windows 后台桌面登录。");
                client.AdvancedSettings2.ClearTextPassword = password.Text;
                password.Clear();
            }
            stage = "连接本机 RDP";
            connectRequested = true; client.Connect(); connecting.Restart();
        }
        catch (Exception ex) { Fail(ex); }
    }

    void Optional(Action action)
    {
        // Annex treats older/unsupported mstscax display options as best-effort.
        try { action(); }
        catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            try { File.AppendAllText(Path.Combine(directory, "viewer-options.log"), ex.GetType().Name + ": " + ex.Message + Environment.NewLine); }
            catch (IOException) { }
        }
    }

    void Poll()
    {
        if (ending) return;
        try
        {
            if (parent.HasExited)
            {
                if (login != null) login.DialogResult = DialogResult.Cancel;
                BeginInvoke(new Action(Close)); return;
            }
            if (!connectRequested || client == null) return;
            int state = (int)client.Connected;
            uint actual = Native.Child();
            if (state == 1 && !agentStarted && actual != 0 && actual != uint.MaxValue &&
                Native.SessionExists(actual) && Native.LoggedOn(actual))
            {
                using (var current = Process.GetCurrentProcess())
                    if (actual == current.SessionId || actual == Native.WTSGetActiveConsoleSessionId())
                        throw new InvalidOperationException("Windows 返回了非子会话，已拒绝启动串流。");
                child = actual;
                stage = "在子会话 " + child + " 中启动 Host 代理";
                // RunEx may return a task object before any action actually starts.
                // Confirm the Host process in this exact child; otherwise keep retrying.
                if (ChildHostIsRunning())
                {
                    agentStarted = true;
                    status.Text = "后台会话 " + child + " · " + display.Width + "×" + display.Height +
                        " · 关闭此窗口会结束后台桌面；最小化可继续串流。";
                    return;
                }
                if (connecting.Elapsed > TimeSpan.FromMinutes(3))
                    throw new TimeoutException("后台桌面已登录，但子 Host 未能启动。请查看本次任务启动日志。");
                if (connecting.ElapsedMilliseconds - lastLaunchAttempt < 2000) return;
                lastLaunchAttempt = connecting.ElapsedMilliseconds;
                if (!StartAgent()) return;
                status.Text = "后台桌面已登录，正在等待子 Host 进程启动…";
                return;
            }
            if (agentStarted && (actual != child || state == 0))
                throw new IOException("后台桌面的 RDP 连接已断开；已停止此实验会话，请在 Host 中重新启动。");
            if (!agentStarted && connecting.Elapsed > TimeSpan.FromMinutes(3))
                throw new TimeoutException("子会话连接超时。请检查 Windows 版本、本机 RDP 和账户凭据。");
        }
        catch (Exception ex) { Fail(ex); }
    }

    bool ChildHostIsRunning()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(hostPath)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == child && !process.HasExited &&
                        string.Equals(Native.ProcessPath(process.Id), hostPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
            }
        }
        return false;
    }

    bool StartAgent()
    {
        if (Native.Child() != child || !Native.SessionExists(child) || !Native.LoggedOn(child))
            return false;
        // A one-shot task, under the already-approved administrator account, targets only this session.
        // No saved password, logon trigger, SYSTEM account, or persistent auto-start task is used.
        string account;
        using (var identity = WindowsIdentity.GetCurrent()) account = identity.Name;
        if (registeredTask == null)
        {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder(@"\");
            dynamic definition = scheduler.NewTask(0);
            taskName = "CloudGI-Annex-" + Guid.NewGuid().ToString("N");
            definition.Principal.UserId = account;
            definition.Principal.LogonType = 3; definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.MultipleInstances = 2; // Ignore duplicate runs while an action is live.
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            dynamic action = definition.Actions.Create(0);
            action.Path = hostPath; action.WorkingDirectory = Path.GetDirectoryName(hostPath);
            action.Arguments = "--child-session-agent " + pipeName + " " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + taskName;
            registeredTask = folder.RegisterTaskDefinition(taskName, definition, 2, account, null, 3, null);
        }
        try
        {
            dynamic registered = registeredTask;
            string sid;
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            // Force VT_NULL for the VARIANT parameter; avoid an untyped null in the COM binder.
            // All variants target the same owned child and the same Windows user.
            Exception last = null;
            foreach (int variant in new[] { 0, 1, 2 })
            {
                try
                {
                    object running;
                    if (variant == 0) running = registered.RunEx(DBNull.Value, 4, checked((int)child), account);
                    else if (variant == 1) running = registered.RunEx(DBNull.Value, 12, checked((int)child), sid);
                    else running = registered.RunEx(DBNull.Value, 4, checked((int)child), string.Empty);
                    if (running == null) throw new IOException("任务计划程序没有返回子会话运行实例。");
                    File.AppendAllText(Path.Combine(directory, "viewer-task.log"), DateTime.Now.ToString("s") + " RunEx succeeded; child=" + child + "; variant=" + variant + Environment.NewLine);
                    return true;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070057))
                {
                    last = ex;
                    File.AppendAllText(Path.Combine(directory, "viewer-task.log"), DateTime.Now.ToString("s") + " RunEx rejected; child=" + child + "; variant=" + variant + "; HRESULT=" + ex.HResult.ToString("X") + Environment.NewLine);
                }
            }
            throw new InvalidOperationException("任务计划程序拒绝在已登录的子会话 " + child + " 中启动代理。", last);
        }
        catch (InvalidOperationException ex) when (ex.InnerException != null && ex.InnerException.HResult == unchecked((int)0x80070057) && connecting.Elapsed < TimeSpan.FromSeconds(60))
        {
            status.Text = "Windows 正在准备后台登录令牌，稍后自动重试…";
            return false;
        }
        // The task stays until the agent acknowledges it or viewer cleanup deletes it.
    }

    void Fail(Exception ex)
    {
        try { File.WriteAllText(Path.Combine(directory, "viewer-error.txt"), stage + Environment.NewLine + ex.ToString()); } catch (IOException) { }
        status.Text = "后台会话失败：" + ex.Message;
        // Keep the error visible, but release the owned session/settings immediately.
        Cleanup();
    }

    void Cleanup()
    {
        if (ending) return; ending = true; timer.Stop(); tray.Visible = false;
        if (login != null) login.Close();
        try
        {
            if (child != 0)
                new ChildSessionController(Native.Child, id => {
                    if (!Native.WTSLogoffSession(IntPtr.Zero, id, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }).EndConfirmedSession(child, (uint)Process.GetCurrentProcess().SessionId, Native.WTSGetActiveConsoleSessionId());
        }
        catch (Exception ex) { status.Text = "子会话清理失败：" + ex.Message; }
        try { if (client != null && (int)client.Connected != 0) client.Disconnect(); } catch (COMException) { }
        if (taskName != null)
        {
            try { dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); scheduler.Connect(); scheduler.GetFolder(@"\").DeleteTask(taskName, 0); }
            catch (COMException) { }
        }
        if (suppressChanged)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client"))
            {
                if (oldSuppress == null) key.DeleteValue("RemoteDesktop_SuppressWhenMinimized", false);
                else key.SetValue("RemoteDesktop_SuppressWhenMinimized", oldSuppress, RegistryValueKind.DWord);
            }
        }
        if (enabledByUs) Native.WTSEnableChildSessions(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Cleanup(); timer.Dispose(); tray.Dispose(); parent.Dispose(); }
        base.Dispose(disposing);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length != 8) throw new ArgumentException("此程序由 CloudGI Host 管理，不能单独运行。");
            var parent = Process.GetProcessById(int.Parse(args[0]));
            string host = Path.GetFullPath(args[2]);
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != long.Parse(args[1]) ||
                !string.Equals(parent.MainModule.FileName, host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Host 进程身份无效。");
            using (var window = new SessionWindow(parent, host, args[3], args[4],
                new ChildDisplaySettings(int.Parse(args[5]), int.Parse(args[6]), int.Parse(args[7])).Validate()))
                Application.Run(window);
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CloudGI 后台会话", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
