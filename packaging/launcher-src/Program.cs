using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace YoukedingLauncher
{
    /// <summary>
    /// 邮课叮桌宠 · Windows 免安装绿色包启动器。
    ///
    /// 设计目标（"傻瓜包"核心）：
    ///   1. 编译为 WinExe（GUI 子系统）→ 双击不弹黑色控制台窗口；
    ///   2. 静默拉起内嵌的 Node 后端（runtime/node/node.exe），等待端口就绪；
    ///   3. 再拉起桌宠本体（app/BigBlueFish.exe）；
    ///   4. 后端已运行则复用，不重复启动。
    ///
    /// 所有路径以 exe 自身所在目录（绿色包根目录）为基准，随意拷贝/移动都能用。
    /// 任何异常都静默处理，绝不弹错误对话框（傻瓜包体验）。
    /// </summary>
    internal static class Program
    {
        private const int BridgePort = 23336;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // 单文件发布时 Assembly.Location 为空；AppContext.BaseDirectory
                // 在普通发布与单文件发布中都稳定指向启动器所在目录。
                string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string nodeExe = Path.Combine(root, "runtime", "node", "node.exe");
                string serverDir = Path.Combine(root, "server");
                string serverEntry = Path.Combine(serverDir, "studyserver.js");
                string appExe = Path.Combine(root, "app", "BigBlueFish.exe");

                // 1) 后端未监听则拉起（隐藏窗口）
                if (!IsPortOpen(BridgePort))
                {
                    if (File.Exists(nodeExe) && File.Exists(serverEntry))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = nodeExe,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = serverDir
                        };
                        psi.ArgumentList.Add(serverEntry);
                        Process.Start(psi);
                    }
                }

                // 2) 等待后端端口就绪（最多 15 秒），让桌宠首次点击学习中心不报连接失败
                for (int i = 0; i < 30 && !IsPortOpen(BridgePort); i++)
                    System.Threading.Thread.Sleep(500);

                // 3) 拉起桌宠本体（GUI 程序，无需隐藏）
                if (File.Exists(appExe))
                {
                    var petPsi = new ProcessStartInfo
                    {
                        FileName = appExe,
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(root, "app")
                    };
                    // 让桌宠在绿色包根目录读 bridge-config.json（CompanionBridgeConfig 用 CurrentDirectory）
                    petPsi.WorkingDirectory = root;
                    Process.Start(petPsi);
                    return 0;
                }

                return 4; // 桌宠本体缺失
            }
            catch
            {
                return 1; // 静默失败，不弹窗
            }
        }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using var tcp = new TcpClient();
                var result = tcp.BeginConnect("127.0.0.1", port, null, null);
                bool ok = result.AsyncWaitHandle.WaitOne(300);
                if (ok) tcp.EndConnect(result);
                return ok;
            }
            catch { return false; }
        }
    }
}
