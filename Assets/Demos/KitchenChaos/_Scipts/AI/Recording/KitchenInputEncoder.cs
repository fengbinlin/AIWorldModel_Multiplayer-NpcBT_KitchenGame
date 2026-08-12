using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Reverse-engineers first-person controls from AI motion / view change:
    /// WASD relative to facing yaw, mouse XY as look deltas (yaw/pitch degrees).
    /// </summary>
    public static class KitchenInputEncoder
    {
        private const float MoveThreshold = 0.15f;

        /// <param name="worldVelocity">Horizontal world-space velocity (m/s).</param>
        /// <param name="viewYawDegrees">First-person yaw used as move basis.</param>
        /// <param name="maxSpeed">Used to scale analog move axes into [-1, 1].</param>
        /// <param name="mouseDeltaX">Yaw delta this frame (degrees, + = turn right).</param>
        /// <param name="mouseDeltaY">Pitch delta this frame (degrees). Sign follows Unity local pitch
        /// change: more positive = look further down; more negative = look further up.</param>
        public static ChefKeyboardInput EncodeFirstPerson(
            Vector3 worldVelocity,
            float viewYawDegrees,
            float maxSpeed,
            float mouseDeltaX,
            float mouseDeltaY,
            bool interactPressed)
        {
            var input = new ChefKeyboardInput
            {
                E = interactPressed,
                mouseX = mouseDeltaX,
                mouseY = mouseDeltaY,
            };

            Vector3 flat = worldVelocity;
            flat.y = 0f;

            var yawRot = Quaternion.Euler(0f, viewYawDegrees, 0f);
            Vector3 forward = yawRot * Vector3.forward;
            Vector3 right = yawRot * Vector3.right;

            float localForward = Vector3.Dot(flat, forward);
            float localRight = Vector3.Dot(flat, right);

            float speedRef = Mathf.Max(maxSpeed, MoveThreshold);
            input.moveZ = Mathf.Clamp(localForward / speedRef, -1f, 1f);
            input.moveX = Mathf.Clamp(localRight / speedRef, -1f, 1f);

            input.W = localForward > MoveThreshold;
            input.S = localForward < -MoveThreshold;
            input.A = localRight < -MoveThreshold;
            input.D = localRight > MoveThreshold;
            return input;
        }

        public static ChefKeyboardInput EncodeFirstPersonFromDelta(
            Vector3 positionDelta,
            float deltaTime,
            float viewYawDegrees,
            float maxSpeed,
            float mouseDeltaX,
            float mouseDeltaY,
            bool interactPressed)
        {
            Vector3 velocity = deltaTime > 0f ? positionDelta / deltaTime : Vector3.zero;
            return EncodeFirstPerson(
                velocity, viewYawDegrees, maxSpeed, mouseDeltaX, mouseDeltaY, interactPressed);
        }

        /// <summary>Convert Unity euler X (0..360) to signed pitch degrees (-180..180).</summary>
        public static float NormalizePitch(float eulerX)
        {
            return Mathf.DeltaAngle(0f, eulerX);
        }
    }
}
