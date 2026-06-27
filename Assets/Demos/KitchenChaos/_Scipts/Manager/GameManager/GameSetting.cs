using System;
using UnityEngine;

namespace Kitchen
{
    [Serializable]
    public class GameSetting
    {
        [Tooltip("Game duration in seconds. Set to 0 or negative for unlimited time.")]
        public float gameDurationSetting = 60f;
        public int readyCountDown = 3;
        public int maxPlayerCount = 4;
    }
}