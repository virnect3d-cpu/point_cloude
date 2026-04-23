"""Ask Unity (via MCP HTTP) to render the main camera into a PNG file and print the path."""
import json
import sys
import urllib.request

SID = "f96066e5ba2640db8e5fc60207f04e1b"
MCP = "http://127.0.0.1:8080/mcp"


def call(method, params):
    req = urllib.request.Request(
        MCP,
        data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(),
        headers={"Content-Type": "application/json",
                 "Accept": "application/json, text/event-stream",
                 "mcp-session-id": SID})
    with urllib.request.urlopen(req, timeout=120) as r:
        for line in r.read().decode().split("\n"):
            if line.startswith("data: "):
                d = json.loads(line[6:])
                if "result" in d:
                    return d["result"]
    return None


CODE = r"""
var cam = UnityEngine.Camera.main;
if (cam == null) { var go = UnityEngine.GameObject.Find("Main Camera"); if (go != null) cam = go.GetComponent<UnityEngine.Camera>(); }
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
rt.Release();
UnityEngine.Object.DestroyImmediate(rt);
return "saved " + png.Length + " bytes to " + outPath;
"""

if __name__ == "__main__":
    # call initialize? already done upstream; reuse session
    out = call("tools/call", {"name": "execute_code",
                              "arguments": {"action": "execute", "code": CODE}})
    print(json.dumps(out.get("structuredContent", out), indent=2)[:800] if out else "no response")
