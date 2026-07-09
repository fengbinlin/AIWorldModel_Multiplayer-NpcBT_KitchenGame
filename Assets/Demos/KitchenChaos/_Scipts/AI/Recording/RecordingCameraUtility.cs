using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    public static class RecordingCameraUtility
    {
        private static int _asyncWriteCount;

        public static int PendingAsyncWrites => Volatile.Read(ref _asyncWriteCount);

        public static RenderTexture CreateRenderTexture(int width, int height)
        {
            return new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        }

        /// <summary>
        /// Render + GPU readback on the main thread. Must be called from Unity main thread.
        /// </summary>
        public static Color32[] CapturePixels(Camera camera, RenderTexture rt)
        {
            if (camera == null || rt == null) return null;

            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;

            camera.targetTexture = rt;
            camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            camera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            Color32[] pixels = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            return pixels;
        }

        /// <summary>
        /// Encode on main thread, write file on a background thread.
        /// </summary>
        public static void SavePixelsAsync(Color32[] pixels, int width, int height, string path)
        {
            if (pixels == null || pixels.Length == 0) return;

            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            if (png == null || png.Length == 0) return;

            Interlocked.Increment(ref _asyncWriteCount);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, png);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RecordingCameraUtility] Async save failed: {e.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref _asyncWriteCount);
                }
            });
        }

        [Obsolete("Use CapturePixels + SavePixelsAsync to avoid blocking the main thread.")]
        public static byte[] CapturePng(Camera camera, RenderTexture rt)
        {
            var pixels = CapturePixels(camera, rt);
            if (pixels == null || rt == null) return null;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            return png;
        }
    }
}
