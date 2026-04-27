using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LccDropForge
{
    internal static class GaussianAssetBuilder
    {
        public const string DefaultOutputFolder = "Assets/GaussianAssets";
        public const string DefaultQualityName = "Medium";

        public static GaussianSplatAsset BuildFromPly(string plyAbsolutePath, string assetName, out string error)
        {
            error = null;

            Type creatorType = typeof(GaussianSplatAssetCreator);
            ScriptableObject creator = null;
            try
            {
                creator = ScriptableObject.CreateInstance(creatorType);

                Type qualityType = creatorType.GetNestedType("DataQuality", BindingFlags.NonPublic);
                object qualityValue = Enum.Parse(qualityType, DefaultQualityName);

                SetPrivate(creator, "m_InputFile", plyAbsolutePath);
                SetPrivate(creator, "m_OutputFolder", DefaultOutputFolder);
                SetPrivate(creator, "m_ImportCameras", false);
                SetPrivate(creator, "m_Quality", qualityValue);

                Directory.CreateDirectory(DefaultOutputFolder);

                MethodInfo apply = creatorType.GetMethod("ApplyQualityLevel", BindingFlags.NonPublic | BindingFlags.Instance);
                apply?.Invoke(creator, null);

                MethodInfo create = creatorType.GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance);
                if (create == null)
                {
                    error = "Reflection: GaussianSplatAssetCreator.CreateAsset not found (Aras-P API change?)";
                    return null;
                }

                create.Invoke(creator, null);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                string baseName = Path.GetFileNameWithoutExtension(plyAbsolutePath);
                string assetPath = $"{DefaultOutputFolder}/{baseName}.asset";
                var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);

                if (asset == null)
                {
                    error = $"Aras-P asset creation ran but .asset not found at {assetPath}";
                    return null;
                }

                if (!string.IsNullOrEmpty(assetName) && asset.name != assetName)
                {
                    asset.name = assetName;
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                }
                return asset;
            }
            catch (TargetInvocationException tie)
            {
                error = $"CreateAsset threw: {tie.InnerException?.Message ?? tie.Message}";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (creator != null) UnityEngine.Object.DestroyImmediate(creator);
            }
        }

        public static GameObject SpawnInScene(GaussianSplatAsset asset)
        {
            var go = new GameObject($"GS_{asset.name}");
            var renderer = go.AddComponent<GaussianSplatRenderer>();
            renderer.m_Asset = asset;
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
            return go;
        }

        static void SetPrivate(object obj, string fieldName, object value)
        {
            FieldInfo f = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException($"Field {fieldName} not found on {obj.GetType().Name}");
            f.SetValue(obj, value);
        }
    }
}
