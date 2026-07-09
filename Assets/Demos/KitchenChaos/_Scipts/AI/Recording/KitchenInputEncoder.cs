using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Reverse-engineers PlayerInput-compatible WASD + E from AI movement and interaction events.
    /// Player move mapping: input (x, y) → world (x, 0, y) — W=+Z, S=-Z, A=-X, D=+X.
    /// </summary>
    public static class KitchenInputEncoder
    {
        private const float MoveThreshold = 0.15f;

        public static ChefKeyboardInput Encode(Vector3 worldVelocity, bool interactPressed)
        {
            var input = new ChefKeyboardInput { E = interactPressed };

            Vector3 flat = worldVelocity;
            flat.y = 0f;
            if (flat.sqrMagnitude < MoveThreshold * MoveThreshold)
                return input;

            Vector3 dir = flat.normalized;
            input.moveX = Mathf.Clamp(dir.x, -1f, 1f);
            input.moveZ = Mathf.Clamp(dir.z, -1f, 1f);

            input.W = dir.z > MoveThreshold;
            input.S = dir.z < -MoveThreshold;
            input.A = dir.x < -MoveThreshold;
            input.D = dir.x > MoveThreshold;
            return input;
        }

        public static ChefKeyboardInput EncodeFromDelta(Vector3 positionDelta, float deltaTime, bool interactPressed)
        {
            if (deltaTime <= 0f)
                return Encode(Vector3.zero, interactPressed);
            return Encode(positionDelta / deltaTime, interactPressed);
        }
    }
}
