using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kitchen.UI
{
    /// <summary>
    /// Screen HUD + world facility chrome: visible only to the top-down main camera.
    /// AI follow / first-person cameras never see the UI layer.
    /// </summary>
    public static class KitchenUiVisibilitySetup
    {
        public const string UiLayerName = "UI";

        private static bool _hookedSceneLoaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ApplyAll();
            if (_hookedSceneLoaded) return;
            _hookedSceneLoaded = true;
            SceneManager.sceneLoaded += (_, __) => ApplyAll();
        }

        /// <summary>
        /// Bind HUD canvases to the main camera, put UI on the UI layer,
        /// and strip that layer from every non-main camera (AI follow cams).
        /// </summary>
        public static void ApplyAll()
        {
            var main = Camera.main;
            int uiLayer = LayerMask.NameToLayer(UiLayerName);
            if (main != null && uiLayer >= 0)
                main.cullingMask |= 1 << uiLayer;

            // AI / secondary cameras must never render the order HUD.
            ExcludeUiFromAllNonMainCameras(main);

            var canvases = Object.FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
                ApplyCanvas(canvases[i], main, uiLayer);
        }

        /// <summary>
        /// Apply to a single world-UI root (e.g. ProgressBar / WarningIcon / DeliveryResult).
        /// </summary>
        public static void ApplyTo(GameObject root)
        {
            if (root == null) return;
            var main = Camera.main;
            int uiLayer = LayerMask.NameToLayer(UiLayerName);
            var canvas = root.GetComponent<Canvas>() ?? root.GetComponentInParent<Canvas>();
            if (canvas != null)
                ApplyCanvas(canvas, main, uiLayer);
            else if (uiLayer >= 0)
                SetLayerRecursive(root, uiLayer);
        }

        /// <summary>
        /// Strip the UI layer from an AI / recording / follow camera.
        /// </summary>
        public static void ExcludeUiFromCamera(Camera camera)
        {
            if (camera == null) return;
            int uiLayer = LayerMask.NameToLayer(UiLayerName);
            if (uiLayer < 0) return;
            camera.cullingMask &= ~(1 << uiLayer);
        }

        /// <summary>
        /// Strip UI from every camera that is not the top-down main camera.
        /// </summary>
        public static void ExcludeUiFromAllNonMainCameras(Camera main = null)
        {
            main ??= Camera.main;
            var cams = Object.FindObjectsOfType<Camera>(true);
            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i];
                if (cam == null || cam == main) continue;
                // Never strip the tagged MainCamera even if Camera.main is briefly null/wrong.
                if (cam.CompareTag("MainCamera")) continue;
                ExcludeUiFromCamera(cam);
            }
        }

        private static void ApplyCanvas(Canvas canvas, Camera main, int uiLayer)
        {
            if (canvas == null) return;

            switch (canvas.renderMode)
            {
                case RenderMode.WorldSpace:
                    if (uiLayer >= 0)
                        SetLayerRecursive(canvas.gameObject, uiLayer);
                    if (main != null)
                        canvas.worldCamera = main;
                    break;

                case RenderMode.ScreenSpaceOverlay:
                    // Overlay composites onto every camera view — convert so AI cams can cull UI.
                    if (main == null)
                        break;

                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = main;
                    canvas.planeDistance = SafePlaneDistance(main);
                    if (uiLayer >= 0)
                        SetLayerRecursive(canvas.gameObject, uiLayer);
                    break;

                case RenderMode.ScreenSpaceCamera:
                    if (main != null)
                    {
                        canvas.worldCamera = main;
                        canvas.planeDistance = SafePlaneDistance(main);
                    }
                    if (uiLayer >= 0)
                        SetLayerRecursive(canvas.gameObject, uiLayer);
                    break;
            }
        }

        private static float SafePlaneDistance(Camera cam)
        {
            if (cam == null) return 1f;
            float near = Mathf.Max(cam.nearClipPlane, 0.01f);
            float far = cam.farClipPlane;
            if (far <= near + 0.1f)
                return near + 0.05f;
            return Mathf.Clamp(1f, near + 0.05f, far - 0.05f);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i).gameObject, layer);
        }
    }
}
