using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Virnect.Lcc.Editor
{
    /// v1 FastAPI 백엔드 (/api/upload, /api/process, /api/mesh, /api/bake, /api/phototex ...) 에
    /// 접근하는 C# 클라이언트. LCC Importer 창의 각 탭이 사용.
    public static class LccV1Client
    {
        public static string BaseUrl => LccServerManager.BaseUrl;

        // ── POST /api/upload-path — 로컬 파일 경로로 업로드 → sid 반환 ────
        public static void UploadPath(string path, Action<string, string> onDone)  // (sid, error)
        {
            var payload = $"{{\"path\":\"{path.Replace("\\","/")}\"}}";
            var req = new UnityWebRequest(BaseUrl + "/api/upload-path", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 600; // 대용량 업로드
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onDone?.Invoke(null, $"HTTP {req.responseCode}: {req.downloadHandler.text}");
                        return;
                    }
                    var d = JsonUtility.FromJson<UploadResp>(req.downloadHandler.text);
                    onDone?.Invoke(d.session_id, null);
                }
                catch (Exception e) { onDone?.Invoke(null, e.Message); }
                finally { req.Dispose(); }
            };
        }

        [Serializable] public class UploadResp { public string session_id; public int point_count; }

        // ── POST /api/upload-lcc — LCC 디렉토리(또는 .lcc 파일)에서 LOD 추출 → sid ─
        // XGrids proxy mesh 가 작아 콜라이더 잘리는 문제 회피용 — data.bin 의 실제 splat 점을 사용.
        public static void UploadLccPath(string lccDirOrFile, int lod, int maxPoints,
                                         Action<string, string> onDone)
        {
            string path = lccDirOrFile.Replace("\\", "/");
            string payload = maxPoints > 0
                ? $"{{\"path\":\"{path}\",\"lod\":{lod},\"max_points\":{maxPoints}}}"
                : $"{{\"path\":\"{path}\",\"lod\":{lod}}}";
            var req = new UnityWebRequest(BaseUrl + "/api/upload-lcc", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 600;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onDone?.Invoke(null, $"HTTP {req.responseCode}: {req.downloadHandler.text}");
                        return;
                    }
                    var d = JsonUtility.FromJson<UploadResp>(req.downloadHandler.text);
                    onDone?.Invoke(d.session_id, null);
                }
                catch (Exception e) { onDone?.Invoke(null, e.Message); }
                finally { req.Dispose(); }
            };
        }

        // ── POST JSON body → 스트리밍 SSE 읽기 (Editor 는 비동기) ────────
        /// returns last parsed event dict (last "data:" line) via callback
        public static void PostJsonReadSse(string url, string jsonBody,
                                           Action<string> onFinalEventJson,
                                           Action<string> onError)
        {
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 3600;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onError?.Invoke($"HTTP {req.responseCode}: {req.downloadHandler.text}");
                        return;
                    }
                    // parse the last data: line
                    string last = null;
                    foreach (var line in req.downloadHandler.text.Split('\n'))
                    {
                        if (line.StartsWith("data: ")) last = line.Substring(6);
                    }
                    onFinalEventJson?.Invoke(last ?? req.downloadHandler.text);
                }
                catch (Exception e) { onError?.Invoke(e.Message); }
                finally { req.Dispose(); }
            };
        }

        // ── GET {mesh/mesh-fbx/mesh-glb}/{sid} → save to disk ─────────────
        public static void DownloadBinary(string url, string savePath,
                                          Action<long> onSaved, Action<string> onError)
        {
            var req = UnityWebRequest.Get(url);
            req.timeout = 600;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    { onError?.Invoke($"HTTP {req.responseCode}: {req.error}"); return; }
                    var bytes = req.downloadHandler.data;
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    File.WriteAllBytes(savePath, bytes);
                    onSaved?.Invoke(bytes.LongLength);
                }
                catch (Exception e) { onError?.Invoke(e.Message); }
                finally { req.Dispose(); }
            };
        }

        public static string SafeStem(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var sb = new StringBuilder();
            foreach (var c in stem) sb.Append(char.IsLetterOrDigit(c) || c=='_' || c=='-' || c=='.' ? c : '_');
            return sb.Length == 0 ? "mesh" : sb.ToString();
        }
    }
}
