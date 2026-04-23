"""Render LOD N from multiple camera angles — front/side/top."""
import json, sys, urllib.request

SID = "f96066e5ba2640db8e5fc60207f04e1b"
MCP = "http://127.0.0.1:8080/mcp"


def exec_code(code: str, timeout=120) -> dict:
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


def setup_lod(lod: int) -> str:
    code = f'''
var go = UnityEngine.GameObject.Find("__LccTest");
if (go == null) return "no __LccTest";
var r = go.GetComponent<Virnect.Lcc.LccPointCloudRenderer>();
r.lodLevel = {lod};
r.enabled = false; r.enabled = true;
var mf = go.GetComponent<UnityEngine.MeshFilter>();
return "lod={lod} verts=" + (mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0);
'''
    return exec_code(code).get("data", {}).get("result", "?")


def render_view(view: str, out_name: str) -> str:
    # view: "front", "iso", "top", "side"
    views = {
        "iso":   "var dir = new UnityEngine.Vector3(1.0f, 0.6f, -1.0f).normalized;",
        "front": "var dir = new UnityEngine.Vector3(0.0f, 0.2f, -1.0f).normalized;",
        "side":  "var dir = new UnityEngine.Vector3(1.0f, 0.2f,  0.0f).normalized;",
        "top":   "var dir = new UnityEngine.Vector3(0.0f, 1.0f, -0.1f).normalized;",
    }
    code = rf'''
var camGO = UnityEngine.GameObject.Find("Main Camera");
var cam = camGO.GetComponent<UnityEngine.Camera>();
var go = UnityEngine.GameObject.Find("__LccTest");
var r = go.GetComponent<Virnect.Lcc.LccPointCloudRenderer>();
var b = r.GetWorldBounds();
var c = b.center;
var s = UnityEngine.Mathf.Max(b.size.x, UnityEngine.Mathf.Max(b.size.y, b.size.z));
{views[view]}
cam.transform.position = c + dir * s * 1.0f;
cam.transform.LookAt(c);
cam.nearClipPlane = 0.1f;
cam.farClipPlane = s * 5f;

int W = 1280, H = 720;
var rt = new UnityEngine.RenderTexture(W, H, 24, UnityEngine.RenderTextureFormat.ARGB32);
cam.targetTexture = rt;
cam.Render();
cam.targetTexture = null;
UnityEngine.RenderTexture.active = rt;
var tex = new UnityEngine.Texture2D(W, H, UnityEngine.TextureFormat.RGB24, false);
tex.ReadPixels(new UnityEngine.Rect(0, 0, W, H), 0, 0);
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
'''
    return exec_code(code).get("data", {}).get("result", "?")


if __name__ == "__main__":
    lod = int(sys.argv[1]) if len(sys.argv) > 1 else 2
    prefix = sys.argv[2] if len(sys.argv) > 2 else f"__screenshot_A_LOD{lod}"
    print("[setup]", setup_lod(lod))
    for v in ("iso", "front", "side", "top"):
        print(f"[{v}]", render_view(v, f"{prefix}_{v}.png"))
