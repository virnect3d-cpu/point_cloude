"""Render merge scene from multiple angles."""
import json, urllib.request

def call(code, timeout=120):
    req = urllib.request.Request('http://127.0.0.1:8080/mcp',
        data=json.dumps({'jsonrpc':'2.0','id':1,'method':'tools/call',
                         'params':{'name':'execute_code','arguments':{'action':'execute','code':code}}}).encode(),
        headers={'Content-Type':'application/json','Accept':'application/json, text/event-stream',
                 'mcp-session-id':'f96066e5ba2640db8e5fc60207f04e1b'})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        for line in r.read().decode().split('\n'):
            if line.startswith('data: '):
                d = json.loads(line[6:])
                if 'result' in d:
                    return d['result'].get('structuredContent',{})

TEMPLATE = '''
var a = UnityEngine.GameObject.Find("__LccMerge_A");
var b = UnityEngine.GameObject.Find("__LccMerge_B");
var mrA = a.GetComponent<UnityEngine.MeshRenderer>();
var mrB = b.GetComponent<UnityEngine.MeshRenderer>();
var bnd = mrA.bounds; bnd.Encapsulate(mrB.bounds);
var c = bnd.center;
var s = UnityEngine.Mathf.Max(bnd.size.x, UnityEngine.Mathf.Max(bnd.size.y, bnd.size.z));
var camGO = UnityEngine.GameObject.Find("Main Camera");
var cam = camGO.GetComponent<UnityEngine.Camera>();
var dir = DIR_EXPR;
cam.transform.position = c + dir * s * ZOOM_EXPR;
cam.transform.LookAt(c);
cam.nearClipPlane = 0.1f; cam.farClipPlane = s * 5f;

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
    "Desktop", "PointCloudOptimizer_v2", "OUT_NAME");
System.IO.File.WriteAllBytes(outPath, png);
UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
return "c=" + c + " s=" + s + " cam=" + cam.transform.position + " saved=" + png.Length + " B";
'''

VIEWS = [
    ("merge_top",   "new UnityEngine.Vector3(0.0f, 1.0f, -0.1f).normalized", "0.8f"),
    ("merge_iso",   "new UnityEngine.Vector3(0.5f, 0.5f, -0.8f).normalized", "1.2f"),
    ("merge_front", "new UnityEngine.Vector3(0.1f, 0.3f, -1.0f).normalized", "1.3f"),
]

for out, dir_expr, zoom in VIEWS:
    code = (TEMPLATE
            .replace("DIR_EXPR", dir_expr)
            .replace("ZOOM_EXPR", zoom)
            .replace("OUT_NAME", f"__screenshot_{out}.png"))
    r = call(code, timeout=120)
    print(f"[{out}]", r.get('data', {}).get('result', '?'))
