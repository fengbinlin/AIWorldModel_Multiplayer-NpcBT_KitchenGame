using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Builds camera intrinsic / world-space extrinsic snapshots for recording JSON.
    /// Extrinsic uses camera localToWorld (global pose), not player-relative.
    /// </summary>
    public static class KitchenCameraInfoUtility
    {
        public const string DefaultGameName = "Kitchen Chaos";

        public const string DefaultTaskDescription =
            "Kitchen Chaos: multi-agent cooking. Players fetch ingredients, cut/cook at counters, " +
            "plate and deliver dishes before orders expire. The AI must assign and execute kitchen " +
            "tasks (fetch/process/plate/serve/trash) to complete waiting orders cooperatively.";

        public static CameraInfo Capture(Camera cam, int pixelWidth, int pixelHeight, string name = null)
        {
            var info = new CameraInfo
            {
                name = name ?? (cam != null ? cam.name : ""),
                @int = CaptureIntrinsic(cam, pixelWidth, pixelHeight),
                ext = CaptureExtrinsic(cam),
            };
            return info;
        }

        public static CameraIntrinsic CaptureIntrinsic(Camera cam, int pixelWidth, int pixelHeight)
        {
            int w = Mathf.Max(1, pixelWidth);
            int h = Mathf.Max(1, pixelHeight);
            float fovY = cam != null ? cam.fieldOfView : 60f;
            float fovYRad = fovY * Mathf.Deg2Rad;
            float fy = (h * 0.5f) / Mathf.Tan(fovYRad * 0.5f);
            float fx = fy; // square pixels

            return new CameraIntrinsic
            {
                fx = fx,
                fy = fy,
                cx = w * 0.5f,
                cy = h * 0.5f,
                width = w,
                height = h,
                fovY = fovY,
            };
        }

        public static CameraExtrinsic CaptureExtrinsic(Camera cam)
        {
            if (cam == null)
            {
                return new CameraExtrinsic
                {
                    matrix = IdentityMatrixRowMajor(),
                };
            }

            var t = cam.transform;
            var e = t.eulerAngles;
            return new CameraExtrinsic
            {
                posX = t.position.x,
                posY = t.position.y,
                posZ = t.position.z,
                rotX = e.x,
                rotY = e.y,
                rotZ = e.z,
                matrix = ToRowMajorArray(t.localToWorldMatrix),
            };
        }

        public static float[] ToRowMajorArray(Matrix4x4 m)
        {
            var a = new float[16];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                    a[r * 4 + c] = m[r, c];
            }
            return a;
        }

        public static float[] IdentityMatrixRowMajor()
        {
            return new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1,
            };
        }
    }
}
