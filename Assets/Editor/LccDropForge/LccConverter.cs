using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LccDropForge
{
    internal static class LccConverter
    {
        public const string TempPlyFolder = "Temp/LccConverted";

        public static bool TryConvertToPly(string lccAssetPath, int lodLevel, out string plyAbsolutePath, out string error)
        {
            plyAbsolutePath = null;
            error = null;

            string lccAbs = Path.GetFullPath(lccAssetPath);
            if (!File.Exists(lccAbs))
            {
                error = $"LCC file not found: {lccAbs}";
                return false;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string tempDir = Path.Combine(projectRoot, TempPlyFolder);
            Directory.CreateDirectory(tempDir);

            string baseName = Path.GetFileNameWithoutExtension(lccAbs);
            plyAbsolutePath = Path.Combine(tempDir, $"{baseName}_lod{lodLevel}.ply");

            string cmdExe = Environment.GetEnvironmentVariable("COMSPEC");
            if (string.IsNullOrEmpty(cmdExe)) cmdExe = "cmd.exe";

            string cliArgs = $"/c splat-transform -O {lodLevel} \"{lccAbs}\" \"{plyAbsolutePath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = cmdExe,
                Arguments = cliArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectRoot,
            };

            Debug.Log($"[LccDropForge] Running: {cmdExe} {cliArgs}");

            try
            {
                using var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    error = $"splat-transform exited with code {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}";
                    return false;
                }

                if (!File.Exists(plyAbsolutePath))
                {
                    error = $"splat-transform ran but output PLY missing: {plyAbsolutePath}\nSTDOUT:\n{stdout}";
                    return false;
                }

                Debug.Log($"[LccDropForge] PLY ready: {plyAbsolutePath}\n{stdout}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to launch splat-transform. Is Node.js + @playcanvas/splat-transform installed and on PATH?\n{ex.Message}";
                return false;
            }
        }
    }
}
