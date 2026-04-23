"""v2.2 splat billboard demo — swap out LccPointCloudRenderer for LccSplatRenderer."""
import json, sys, urllib.request

SID = "f96066e5ba2640db8e5fc60207f04e1b"
MCP = "http://127.0.0.1:8080/mcp"


def call_code(code: str, timeout=180) -> dict:
    req = urllib.request.Request(MCP,
        data=json.dumps({"jsonrpc":"2.0","id":1,"method":"tools/call",
                         "params":{"name":"execute_code",
                                   "arguments":{"action":"execute","code":code}}}).encode(),
        headers={"Content-Type":"application/json",
                 "Accept":"application/json, text/event-stream",
                 "mcp-session-id": SID})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        for line in r.read().decode().split("\n"):
            if line.startswith("data: "):
                d = json.loads(line[6:])
                if "result" in d:
                    return d["result"].get("structuredContent", {})
    return {}


SETUP = r"""
var old = UnityEngine.GameObject.Find("__LccSplatTest");
if (old != null) UnityEngine.Object.DestroyImmediate(old);
var oldPts = UnityEngine.GameObject.Find("__LccTest");
if (oldPts != null) oldPts.SetActive(false);

var camGO = UnityEngine.GameObject.Find("Main Camera");
if (camGO == null) { camGO = new UnityEngine.GameObject("Main Camera", typeof(UnityEngine.Camera)); camGO.tag = "MainCamera"; }
var cam = camGO.GetComponent<UnityEngine.Camera>();
cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
cam.backgroundColor = new UnityEngine.Color(0.05f, 0.05f, 0.08f, 1f);

var scene = UnityEditor.AssetDatabase.LoadAssetAtPath<Virnect.Lcc.LccScene>("Assets/ScanData/ShinWon_1st_Cutter.lcc");
if (scene == null) return "scene null";

var go = new UnityEngine.GameObject("__LccSplatTest");
var r = go.AddComponent<Virnect.Lcc.LccSplatRenderer>();
r.scene = scene;
r.lodLevel = 4;
r.scaleMultiplier = 2.0f;
r.opacityBoost = 0.3f;
r.enabled = false; r.enabled = true;

var b = r.GetWorldBounds();
var c = b.center;
var s = UnityEngine.Mathf.Max(b.size.x, UnityEngine.Mathf.Max(b.size.y, b.size.z));
cam.transform.position = c + new UnityEngine.Vector3(s*0.9f, s*0.6f, -s*0.9f);
cam.transform.LookAt(c);
cam.nearClipPlane = 0.1f;
cam.farClipPlane = s * 5f;

var mf = go.GetComponent<UnityEngine.MeshFilter>();
int vcount = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.vertexCount : 0;
int tcount = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.triangles.Length / 3 : 0;
return string.Format("splat renderer setup — verts={0:N0} tris={1:N0} bounds_size={2:F1}", vcount, tcount, s);
"""


def render_angle(view: str, out_name: str) -> str:
    views = {
        "iso":   "new UnityEngine.Vector3( 1.0f, 0.6f,-1.0f).normalized",
        "front": "new UnityEngine.Vector3( 0.0f, 0.2f,-1.0f).normalized",
        "top":   "new UnityEngine.Vector3( 0.0f, 1.0f,-0.1f).normalized",
        "close": "new UnityEngine.Vector3( 0.3f, 0.2f,-1.0f).normalized",
    }
    zoom = "0.45f" if view == "close" else "1.0f"
    code = rf"""
var camGO = UnityEngine.GameObject.Find("Main Camera");
var cam = camGO.GetComponent<UnityEngine.Camera>();
var go = UnityEngine.GameObject.Find("__LccSplatTest");
var r = go.GetComponent<Virnect.Lcc.LccSplatRenderer>();
var b = r.GetWorldBounds();
var c = b.center;
var s = UnityEngine.Mathf.Max(b.size.x, UnityEngine.Mathf.Max(b.size.y, b.size.z));
var dir = {views[view]};
cam.transform.position = c + dir * s * {zoom};
cam.transform.LookAt(c);
cam.nearClipPlane = 0.05f;
cam.farClipPlane = s * 5f;

int W = 1280, H = 720;
var rt = new UnityEngine.RenderTexture(W, H, 24, UnityEngine.RenderTextureFormat.ARGB32);
cam.targetTexture = rt;
cam.Render();
cam.targetTexture = null;
UnityEngine.RenderTexture.active = rt;
var tex = new UnityEngine.Texture2D(W, H, UnityEngine.TextureFormat.RGB24, false);
tex.ReadPixels(new UnityEngine.Rect(0,0,W,H), 0, 0);
tex.Apply();
UnityEngine.RenderTexture.active = null;
byte[] png = tex.EncodeToPNG();
string outPath = System.IO.Path.Combine(
    System.Environment.GetEnvironmentVariable("USERPROFILE"),
    "Desktop", "PointCloudOptimizer_v2", "{out_name}");
System.IO.File.WriteAllBytes(outPath, png);
UnityEngine.Object.DestroyImmediate(tex);
rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
return "saved " + png.Length + " B -> {out_name}";
"""
    return call_code(code).get("data", {}).get("result", "?")


if __name__ == "__main__":
    print("[setup]", call_code(SETUP).get("data", {}).get("result", "?"))
    for v in ("iso", "front", "top", "close"):
        print(f"[{v}]", render_angle(v, f"__screenshot_v22_splats_{v}.png"))
