"""Multi-LCC merge demo: load ScanData + ScanData_B (offset +90 X) → place both."""
import json, urllib.request

SID = "f96066e5ba2640db8e5fc60207f04e1b"
MCP = "http://127.0.0.1:8080/mcp"


def call_code(code: str, timeout=240) -> dict:
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


# Ensure both .lcc assets are imported
REFRESH = r"""
UnityEditor.AssetDatabase.Refresh();
return "ok";
"""

SETUP = r"""
// Clean
foreach (var n in new[]{"__LccSplatTest","__LccMerge_A","__LccMerge_B","__LccMergeRoot"}) {
  var g = UnityEngine.GameObject.Find(n);
  if (g != null) UnityEngine.Object.DestroyImmediate(g);
}
var oldPts = UnityEngine.GameObject.Find("__LccTest"); if (oldPts != null) oldPts.SetActive(false);

var camGO = UnityEngine.GameObject.Find("Main Camera");
var cam = camGO.GetComponent<UnityEngine.Camera>();
cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
cam.backgroundColor = new UnityEngine.Color(0.05f,0.05f,0.08f,1f);

var sceneA = UnityEditor.AssetDatabase.LoadAssetAtPath<Virnect.Lcc.LccScene>("Assets/ScanData/ShinWon_1st_Cutter.lcc");
var sceneB = UnityEditor.AssetDatabase.LoadAssetAtPath<Virnect.Lcc.LccScene>("Assets/ScanData_B/ShinWon_1st_Cutter.lcc");
if (sceneA == null || sceneB == null) return "scenes null: A=" + (sceneA!=null) + " B=" + (sceneB!=null);

// Use the merger to get planned world positions
var list = new System.Collections.Generic.List<Virnect.Lcc.LccScene>();
list.Add(sceneA); list.Add(sceneB);
var plan = Virnect.Lcc.LccSceneMerger.Plan(list);

// Root GameObject
var root = new UnityEngine.GameObject("__LccMergeRoot");

string log = "";
int idx = 0;
foreach (var placed in plan)
{
    var go = new UnityEngine.GameObject("__LccMerge_" + (idx==0?"A":"B"));
    go.transform.SetParent(root.transform, false);
    // double3 → Vector3 (EPSG 좌표는 m 단위라 여기서는 그대로 적용)
    go.transform.position = new UnityEngine.Vector3(
        (float)placed.worldOffset.x, (float)placed.worldOffset.y, (float)placed.worldOffset.z);
    go.transform.rotation = new UnityEngine.Quaternion(
        placed.rotation.value.x, placed.rotation.value.y, placed.rotation.value.z, placed.rotation.value.w);
    go.transform.localScale = new UnityEngine.Vector3(placed.scale.x, placed.scale.y, placed.scale.z);

    var r = go.AddComponent<Virnect.Lcc.LccSplatRenderer>();
    r.scene = placed.scene;
    r.lodLevel = 4;
    r.scaleMultiplier = 2.0f;
    r.opacityBoost = 0.3f;
    r.enabled = false; r.enabled = true;
    log += string.Format("  {0}: pos=({1:F1},{2:F1},{3:F1})\n",
        placed.scene.name, go.transform.position.x, go.transform.position.y, go.transform.position.z);
    idx++;
}

// Frame camera to combined bounds
var rA = root.transform.GetChild(0).GetComponent<Virnect.Lcc.LccSplatRenderer>();
var rB = root.transform.GetChild(1).GetComponent<Virnect.Lcc.LccSplatRenderer>();
var bA = rA.GetWorldBounds(); bA.center += root.transform.GetChild(0).position;
var bB = rB.GetWorldBounds(); bB.center += root.transform.GetChild(1).position;
var combined = bA; combined.Encapsulate(bB);
var c = combined.center;
var s = UnityEngine.Mathf.Max(combined.size.x, UnityEngine.Mathf.Max(combined.size.y, combined.size.z));
cam.transform.position = c + new UnityEngine.Vector3(s*0.55f, s*0.55f, -s*0.9f);
cam.transform.LookAt(c);
cam.nearClipPlane = 0.1f;
cam.farClipPlane = s * 5f;

return "merged 2 scenes:\n" + log + " combined_size=" + s.ToString("F1");
"""


RENDER = r"""
var cam = UnityEngine.Camera.main;
if (cam == null) { var g = UnityEngine.GameObject.Find("Main Camera"); if (g!=null) cam = g.GetComponent<UnityEngine.Camera>(); }
int W = 1600, H = 900;
var rt = new UnityEngine.RenderTexture(W, H, 24, UnityEngine.RenderTextureFormat.ARGB32);
cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
UnityEngine.RenderTexture.active = rt;
var tex = new UnityEngine.Texture2D(W, H, UnityEngine.TextureFormat.RGB24, false);
tex.ReadPixels(new UnityEngine.Rect(0,0,W,H), 0, 0); tex.Apply();
UnityEngine.RenderTexture.active = null;
byte[] png = tex.EncodeToPNG();
string outPath = System.IO.Path.Combine(
    System.Environment.GetEnvironmentVariable("USERPROFILE"),
    "Desktop", "PointCloudOptimizer_v2", "__screenshot_merge_demo.png");
System.IO.File.WriteAllBytes(outPath, png);
UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
return "saved " + png.Length + " B";
"""

if __name__ == "__main__":
    print("[refresh]", call_code(REFRESH).get("data", {}).get("result", "?"))
    print("[setup]")
    print(call_code(SETUP, timeout=240).get("data", {}).get("result", "?"))
    print("[render]", call_code(RENDER).get("data", {}).get("result", "?"))
