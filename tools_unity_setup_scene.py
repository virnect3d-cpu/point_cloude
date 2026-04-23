"""Build a test scene with LccPointCloudRenderer, frame camera, render to PNG."""
import json
import sys
import urllib.request

SID = "f96066e5ba2640db8e5fc60207f04e1b"
MCP = "http://127.0.0.1:8080/mcp"


def exec_code(code: str, timeout: int = 120) -> dict:
    req = urllib.request.Request(
        MCP,
        data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                         "params": {"name": "execute_code",
                                    "arguments": {"action": "execute", "code": code}}}).encode(),
        headers={"Content-Type": "application/json",
                 "Accept": "application/json, text/event-stream",
                 "mcp-session-id": SID})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        for line in r.read().decode().split("\n"):
            if line.startswith("data: "):
                d = json.loads(line[6:])
                if "result" in d:
                    sc = d["result"].get("structuredContent", {})
                    return sc
    return {}


SETUP = r"""
// Clean slate
var old = UnityEngine.GameObject.Find("__LccTest");
if (old != null) UnityEngine.Object.DestroyImmediate(old);

// Camera
var camGO = UnityEngine.GameObject.Find("Main Camera");
if (camGO == null) { camGO = new UnityEngine.GameObject("Main Camera", typeof(UnityEngine.Camera)); }
var cam = camGO.GetComponent<UnityEngine.Camera>();
cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
cam.backgroundColor = new UnityEngine.Color(0.05f, 0.05f, 0.08f, 1f);
cam.tag = "MainCamera";

// LCC Scene asset
var scene = UnityEditor.AssetDatabase.LoadAssetAtPath<Virnect.Lcc.LccScene>("Assets/ScanData/ShinWon_1st_Cutter.lcc");
if (scene == null) return "scene null";

// Renderer GameObject (needs MeshFilter + MeshRenderer via RequireComponent)
var go = new UnityEngine.GameObject("__LccTest");
var r = go.AddComponent<Virnect.Lcc.LccPointCloudRenderer>();
r.scene = scene;
r.lodLevel = 4;
// Toggle to trigger OnEnable after scene set
r.enabled = false; r.enabled = true;

// Frame camera
var b = r.GetWorldBounds();
var c = b.center;
var s = UnityEngine.Mathf.Max(b.size.x, UnityEngine.Mathf.Max(b.size.y, b.size.z));
cam.transform.position = c + new UnityEngine.Vector3(s*0.9f, s*0.6f, -s*0.9f);
cam.transform.LookAt(c);
cam.nearClipPlane = 0.1f;
cam.farClipPlane = s * 5f;

var mf = go.GetComponent<UnityEngine.MeshFilter>();
int vcount = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.vertexCount : 0;
return string.Format("setup OK. camera=({0:F1},{1:F1},{2:F1}) target={3} size={4:F1} mesh_verts={5}",
    cam.transform.position.x, cam.transform.position.y, cam.transform.position.z, c, s, vcount);
"""

RENDER = r"""
var cam = UnityEngine.Camera.main;
if (cam == null) { var go = UnityEngine.GameObject.Find("Main Camera"); if (go!=null) cam = go.GetComponent<UnityEngine.Camera>(); }
if (cam == null) return "no camera";

int W = 1280, H = 720;
var rt = new UnityEngine.RenderTexture(W, H, 24, UnityEngine.RenderTextureFormat.ARGB32);
var prev = cam.targetTexture;
cam.targetTexture = rt;
cam.Render();
cam.targetTexture = prev;

var active = UnityEngine.RenderTexture.active;
UnityEngine.RenderTexture.active = rt;
var tex = new UnityEngine.Texture2D(W, H, UnityEngine.TextureFormat.RGB24, false);
tex.ReadPixels(new UnityEngine.Rect(0, 0, W, H), 0, 0);
tex.Apply();
UnityEngine.RenderTexture.active = active;

byte[] png = tex.EncodeToPNG();
string outPath = System.IO.Path.Combine(
    System.Environment.GetEnvironmentVariable("USERPROFILE"),
    "Desktop", "PointCloudOptimizer_v2", "__screenshot_A.png");
System.IO.File.WriteAllBytes(outPath, png);
UnityEngine.Object.DestroyImmediate(tex);
rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
return "saved " + png.Length + " bytes to " + outPath;
"""

if __name__ == "__main__":
    print("=== SETUP ===")
    print(exec_code(SETUP).get("data", {}).get("result", "?"))
    print("=== RENDER ===")
    print(exec_code(RENDER).get("data", {}).get("result", "?"))
