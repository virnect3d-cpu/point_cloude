using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Virnect.Lcc.Editor
{
    /// 패키지에 함께 들어있는 Server~ 폴더의 server.py 를 subprocess 로 띄우고,
    /// pip install, 헬스 체크까지 담당. 실행.bat 의 Python 부분을 에디터 내부로 내재화.
    public static class LccServerManager
    {
        public const string PythonPref  = "Virnect.Lcc.PythonPath";
        public const string PortPref    = "Virnect.Lcc.ServerPort";
        public const string PidPref     = "Virnect.Lcc.ServerPid";
        public const string V1RootPref  = "Virnect.Lcc.V1Root";
        public const string DefaultPython = "python";
        public const int    DefaultPort   = 8001;

        // v1 PointCloudOptimizer_v3.0_260422 설치 폴더 (실행.bat 가 있는 위치의 상위)
        public static string DefaultV1Root =>
            @"C:\Users\jeongsomin\Desktop\PointCloudOptimizer_v3.0_260422\PointCloudOptimizer_v3.0_260422\PointCloudOptimizer";
        public static string V1Root
        {
            get => EditorPrefs.GetString(V1RootPref, DefaultV1Root);
            set => EditorPrefs.SetString(V1RootPref, value);
        }
        public static string V1Venv =>
            Path.Combine(V1Root, ".venv", "Scripts", "python.exe");

        public static string V1EffectivePython
            => File.Exists(V1Venv) ? V1Venv : PythonPath;

        public static string PythonPath
        {
            get => EditorPrefs.GetString(PythonPref, DefaultPython);
            set => EditorPrefs.SetString(PythonPref, value);
        }
        public static int Port
        {
            get => EditorPrefs.GetInt(PortPref, DefaultPort);
            set => EditorPrefs.SetInt(PortPref, value);
        }
        public static string BaseUrl => $"http://127.0.0.1:{Port}";

        // Package-relative location of Server~
        //   Development: Packages/com.virnect.lcc/Server~
        //   UPM git:     Library/PackageCache/com.virnect.lcc@<hash>/Server~
        public static string ServerFolder()
        {
            // Resolve via Package Manager API
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.virnect.lcc/package.json");
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                return Path.Combine(info.resolvedPath, "Server~");
            // Fallback search
            var local = Path.Combine(Application.dataPath, "..", "Packages", "com.virnect.lcc", "Server~");
            if (Directory.Exists(local)) return Path.GetFullPath(local);
            return "";
        }

        public static string ServerPy() => Path.Combine(ServerFolder(), "server.py");
        public static string Requirements() => Path.Combine(ServerFolder(), "requirements.txt");

        public static bool ServerFilesExist()
            => File.Exists(ServerPy()) && File.Exists(Requirements());

        // ── Async health check via UnityWebRequest ────────────────────────
        public static void HealthCheckAsync(Action<bool, string> callback)
        {
            var req = UnityWebRequest.Get(BaseUrl + "/api/health");
            req.timeout = 2;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
                callback?.Invoke(ok, ok ? req.downloadHandler.text : req.error);
                req.Dispose();
            };
        }

        // ── Install pip dependencies (blocking — small install) ───────────
        public static int InstallDependencies(Action<string> onLine)
        {
            if (!ServerFilesExist()) { onLine?.Invoke("[error] Server files not found."); return -1; }
            var args = $"-m pip install -r \"{Requirements()}\" --disable-pip-version-check";
            return _RunProcess(PythonPath, args, ServerFolder(), onLine);
        }

        static int _RunProcess(string exe, string args, string cwd, Action<string> onLine)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = cwd,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
                p.ErrorDataReceived  += (s, e) => { if (e.Data != null) onLine?.Invoke("[stderr] " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit(1000 * 60 * 10); // 10 min max
                return p.ExitCode;
            }
            catch (Exception e)
            {
                onLine?.Invoke("[exception] " + e.Message);
                return -1;
            }
        }

        // ── Start server (v1 full backend — 모든 5 페이지 기능 포함) ─────
        public static bool StartServer(out string error)
        {
            error = null;
            if (IsRunning()) { error = "already running (pid " + EditorPrefs.GetInt(PidPref, 0) + ")"; return false; }

            string v1Root = V1Root;
            string v1Py   = V1EffectivePython;
            string appPath = Path.Combine(v1Root, "backend");
            if (!Directory.Exists(appPath))
            {
                error = "v1 backend not found at " + appPath + "\n'Server' 탭의 'v1 루트 경로' 를 확인하세요.";
                return false;
            }
            if (!File.Exists(v1Py))
            {
                error = "Python not found: " + v1Py;
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo(v1Py,
                    $"-m uvicorn backend.app:app --host 127.0.0.1 --port {Port} --log-level warning")
                {
                    WorkingDirectory = v1Root,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                var proc = Process.Start(psi);
                if (proc == null) { error = "Process.Start returned null"; return false; }
                EditorPrefs.SetInt(PidPref, proc.Id);
                Debug.Log($"[LCC] v1 server started, pid={proc.Id}, port={Port}, root={v1Root}");
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        public static bool IsRunning()
        {
            int pid = EditorPrefs.GetInt(PidPref, 0);
            if (pid <= 0) return false;
            try { Process.GetProcessById(pid); return true; }
            catch { EditorPrefs.DeleteKey(PidPref); return false; }
        }

        public static bool StopServer(out string error)
        {
            error = null;
            int pid = EditorPrefs.GetInt(PidPref, 0);
            if (pid <= 0) { error = "not running"; return false; }
            try
            {
                var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(3000);
                EditorPrefs.DeleteKey(PidPref);
                Debug.Log($"[LCC] server stopped, pid={pid}");
                return true;
            }
            catch (Exception e) { error = e.Message; EditorPrefs.DeleteKey(PidPref); return false; }
        }

        public static void OpenBrowser() => Application.OpenURL(BaseUrl + "/docs");

        // ── v1 실행파일 원클릭 기동 (PointCloudOptimizer_v3.0_260422) ──────
        public const string V1PathPref = "Virnect.Lcc.V1BatPath";
        public static string DefaultV1Bat =>
            @"C:\Users\jeongsomin\Desktop\PointCloudOptimizer_v3.0_260422\PointCloudOptimizer_v3.0_260422\실행.bat";

        public static string V1BatPath
        {
            get => EditorPrefs.GetString(V1PathPref, DefaultV1Bat);
            set => EditorPrefs.SetString(V1PathPref, value);
        }

        public static bool LaunchV1App(out string error)
        {
            error = null;
            string bat = V1BatPath;
            if (!File.Exists(bat)) { error = "실행.bat not found: " + bat; return false; }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c start \"v1 app\" \"{bat}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(bat),
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
    }
}
