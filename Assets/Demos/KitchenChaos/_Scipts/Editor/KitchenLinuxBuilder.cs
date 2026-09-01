#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kitchen.EditorTools
{
    public static class KitchenLinuxBuilder
    {
        private const string ScenePath = "Assets/NPC_PGC.unity";
        private const string BuildPathArgument = "--buildPath";
        private const string DefaultBuildPath = "Builds/Linux/KitchenGame.x86_64";

        private static readonly string[] RequiredShaderGuids =
        {
            // Toony Colors Pro 2 Hybrid Outline; referenced by 46 scene materials.
            "df5bb027d94a6c44bb32b3c31ec1303f",
            // lilToon multi outline; referenced by 41 scene materials.
            "51b2dee0ab07bd84d8147601ff89e511",
            // TextMesh Pro mobile SDF; used by the HUD and order text.
            "fe393ace9b354375a9cb14cdbbc28be4",
        };

        public static void Build()
        {
            ValidateRequiredShaders();

            string outputPath = GetArgument(BuildPathArgument) ?? DefaultBuildPath;
            outputPath = Path.GetFullPath(outputPath);

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            Debug.Log($"[KitchenLinuxBuilder] Building {ScenePath} to {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.None,
            });

            BuildSummary summary = report.summary;
            Debug.Log(
                $"[KitchenLinuxBuilder] Result={summary.result}, " +
                $"size={summary.totalSize}, warnings={summary.totalWarnings}, errors={summary.totalErrors}");

            if (summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Linux build failed: {summary.result}");
        }

        private static void ValidateRequiredShaders()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            foreach (string guid in RequiredShaderGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath) && projectRoot != null &&
                    File.Exists(Path.Combine(projectRoot, assetPath)))
                    continue;

                throw new InvalidOperationException(
                    $"Required shader asset {guid} is missing. A Linux build would render " +
                    "materials magenta. Use a full checkout, or include Assets/lilToon, " +
                    "Assets/ImportedResources/JMO Assets/Toony Colors Pro, and " +
                    "Assets/Plugins/TextMesh Pro/Shaders in the sparse checkout.");
            }
        }

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal) && i + 1 < args.Length)
                    return args[i + 1];

                string prefix = name + "=";
                if (args[i].StartsWith(prefix, StringComparison.Ordinal))
                    return args[i].Substring(prefix.Length);
            }

            return null;
        }
    }
}
#endif
