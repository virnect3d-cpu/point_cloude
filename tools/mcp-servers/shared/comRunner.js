import { spawn } from "node:child_process";

/**
 * Execute ExtendScript (JSX) inside a running Adobe app (Photoshop/Illustrator)
 * via its COM automation object on Windows. Uses PowerShell as the COM bridge
 * and base64 to avoid quoting/escaping hell.
 *
 * @param {"Photoshop.Application"|"Illustrator.Application"} progId
 * @param {string} jsxSource  Raw ExtendScript source to run
 * @returns {Promise<string>} Whatever the script returns (coerced to string)
 */
export async function runJsx(progId, jsxSource) {
  const b64 = Buffer.from(jsxSource, "utf8").toString("base64");

  const psScript = `
$ErrorActionPreference = 'Stop'
try {
  $script = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${b64}'))
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('${progId}')
  $result = $app.DoJavaScript($script)
  if ($null -eq $result) { Write-Output '' } else { Write-Output $result }
} catch [System.Runtime.InteropServices.COMException] {
  Write-Error "COM_ERROR: $($_.Exception.Message). Is the host application running?"
  exit 2
} catch {
  Write-Error "JSX_ERROR: $($_.Exception.Message)"
  exit 3
}
`;

  return new Promise((resolve, reject) => {
    const child = spawn(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", psScript],
      { windowsHide: true }
    );
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d.toString("utf8")));
    child.stderr.on("data", (d) => (stderr += d.toString("utf8")));
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) resolve(stdout.replace(/\r?\n$/, ""));
      else reject(new Error(stderr.trim() || `PowerShell exited with code ${code}`));
    });
  });
}

/** Convert a Windows path to JSX-friendly forward-slash form. */
export function toJsxPath(p) {
  return String(p).replace(/\\/g, "/");
}

/** Wrap a JSX body so it returns a JSON string — lets us get structured data back. */
export function jsonWrap(body) {
  return `(function(){ try { ${body} } catch(e) { return JSON.stringify({ok:false, error: String(e), line: e.line}); } })();`;
}
