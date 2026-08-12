using System;
using UnityEngine;

namespace Kitchen
{
    internal enum LookAtUpdateEnum
    {
        LateUpdate,
        Update,
        FixedUpdate
    }

    internal enum LookMode
    {
        LookAt,
        LookAtInverted,
        CameraForward,
        CameraForwardInverted,
    }

    /// <summary>
    /// Billboard toward the main top-down camera.
    /// World Space Canvas draws on the -Z face, so "facing the camera" means
    /// pointing transform.forward away from the camera (LookAtInverted /
    /// CameraForwardInverted).
    /// </summary>
    public class LookAtCamera : MonoBehaviour
    {
        [SerializeField] internal LookMode lookMode;

        private bool _isWorldCanvas;

        private void Awake()
        {
            // World-space billboards (progress / warning / plate icons) → UI layer + main cam only.
            UI.KitchenUiVisibilitySetup.ApplyTo(gameObject);

            _isWorldCanvas = GetComponent<Canvas>() != null;
            if (_isWorldCanvas)
            {
                // Prefabs often used mesh-oriented modes; flip to canvas-correct ones.
                if (lookMode == LookMode.CameraForward)
                    lookMode = LookMode.CameraForwardInverted;
                else if (lookMode == LookMode.LookAt)
                    lookMode = LookMode.LookAtInverted;
            }
        }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;

            switch (lookMode)
            {
                case LookMode.LookAt:
                {
                    // +Z toward camera (meshes).
                    Vector3 toCam = cam.transform.position - transform.position;
                    if (toCam.sqrMagnitude < 1e-6f) return;
                    transform.rotation = Quaternion.LookRotation(toCam.normalized, cam.transform.up);
                    break;
                }
                case LookMode.LookAtInverted:
                {
                    // -Z toward camera (World Space Canvas front).
                    Vector3 fromCam = transform.position - cam.transform.position;
                    if (fromCam.sqrMagnitude < 1e-6f) return;
                    transform.rotation = Quaternion.LookRotation(fromCam.normalized, cam.transform.up);
                    break;
                }
                case LookMode.CameraForward:
                    transform.rotation = cam.transform.rotation;
                    break;
                case LookMode.CameraForwardInverted:
                    // Parallel to view plane; canvas front faces the camera.
                    transform.rotation = cam.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
