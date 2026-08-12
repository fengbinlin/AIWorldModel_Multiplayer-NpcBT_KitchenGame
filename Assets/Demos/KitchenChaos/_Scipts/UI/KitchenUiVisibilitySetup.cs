using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kitchen.UI
{
    /// <summary>
    /// Ensures all UI (screen HUD + world-space facility/delivery chrome) is only
    /// visible to the main top-down camera. AI first-person cameras exclude the UI layer.
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
        /// Bind every canvas in the scene to the main camera and put world UI on the UI layer.
        /// </summary>
        public static void ApplyAll()
        {
            var main = Camera.main;
            int uiLayer = LayerMask.NameToLayer(UiLayerName);
            if (main != null && uiLayer >= 0)
                main.cullingMask |= 1 << uiLayer;

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
        /// Strip the UI layer from an AI / recording camera so it never sees HUD chrome.
        /// </summary>
        public static void ExcludeUiFromCamera(Camera camera)
        {
            if (camera == null) return;
            int uiLayer = LayerMask.NameToLayer(UiLayerName);
            if (uiLayer < 0) return;
            camera.cullingMask &= ~(1 << uiLayer);
        }

        private static void ApplyCanvas(Canvas canvas, Camera main, int uiLayer)
        {
            if (canvas == null) return;

            switch (canvas.renderMode)
            {
                case RenderMode.WorldSpace:
                    // Facility progress / warning / delivery result — layer UI so AI cams cull them.
                    if (uiLayer >= 0)
                        SetLayerRecursive(canvas.gameObject, uiLayer);
                    if (main != null)
                        canvas.worldCamera = main;
                    break;

                case RenderMode.ScreenSpaceOverlay:
                    // Overlay draws on every display; bind to main camera so only that cam composites it.
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = main;
                    if (canvas.planeDistance < 0.1f)
                        canvas.planeDistance = 1f;
                    if (uiLayer >= 0)
                        SetLayerRecursive(canvas.gameObject, uiLayer);
                    break;

                case RenderMode.ScreenSpaceCamera:
                    if (main != null)
                        canvas.worldCamera = main;
                    break;
            }
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
