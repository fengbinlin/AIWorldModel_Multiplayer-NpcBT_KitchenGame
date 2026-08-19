using UnityEngine;
#if TCP2_NEW_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ToonyColorsPro
{
	namespace Demo
	{
		static class InputAbstraction
		{
#if TCP2_NEW_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
			// New Input System
			internal static bool KeyDown_Delete => Keyboard.current != null && Keyboard.current.deleteKey.wasPressedThisFrame;
			internal static bool KeyDown_Escape => Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
			internal static bool KeyDown_RightArrow => Keyboard.current != null && Keyboard.current.rightArrowKey.wasPressedThisFrame;
			internal static bool KeyDown_LeftArrow => Keyboard.current != null && Keyboard.current.leftArrowKey.wasPressedThisFrame;
			internal static bool KeyDown_Tab => Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
			internal static bool KeyDown_H => Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame;
			internal static bool KeyDown_R => Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;

			internal static bool KeyDown_1 => Keyboard.current != null && (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame);
			internal static bool KeyDown_2 => Keyboard.current != null && (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame);
			internal static bool KeyDown_3 => Keyboard.current != null && (Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame);
			internal static bool KeyDown_4 => Keyboard.current != null && (Keyboard.current.digit4Key.wasPressedThisFrame || Keyboard.current.numpad4Key.wasPressedThisFrame);
			internal static bool KeyDown_5 => Keyboard.current != null && (Keyboard.current.digit5Key.wasPressedThisFrame || Keyboard.current.numpad5Key.wasPressedThisFrame);
			internal static bool KeyDown_6 => Keyboard.current != null && (Keyboard.current.digit6Key.wasPressedThisFrame || Keyboard.current.numpad6Key.wasPressedThisFrame);

			internal static bool Key_LeftShift => Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
			internal static bool Key_RightShift => Keyboard.current != null && Keyboard.current.rightShiftKey.isPressed;

			internal static Vector2 Mouse_Position => Mouse.current != null ? Mouse.current.position.value : Vector2.zero;
			internal static float Mouse_ScrollWheel => Mouse.current != null ? Mouse.current.scroll.y.value : 0;
			internal static bool Mouse_LeftDown => Mouse.current != null && Mouse.current.leftButton.isPressed;
			internal static bool Mouse_RightDown => Mouse.current != null && Mouse.current.rightButton.isPressed;
			internal static bool Mouse_MiddleDown => Mouse.current != null && Mouse.current.middleButton.isPressed;
#else
			// Legacy Input System
			internal static bool KeyDown_Delete => Input.GetKeyDown(KeyCode.Delete);
			internal static bool KeyDown_Escape => Input.GetKeyDown(KeyCode.Escape);
			internal static bool KeyDown_RightArrow => Input.GetKeyDown(KeyCode.RightArrow);
			internal static bool KeyDown_LeftArrow => Input.GetKeyDown(KeyCode.LeftArrow);
			internal static bool KeyDown_Tab => Input.GetKeyDown(KeyCode.Tab);
			internal static bool KeyDown_H => Input.GetKeyDown(KeyCode.H);
			internal static bool KeyDown_R => Input.GetKeyDown(KeyCode.R);

			internal static bool KeyDown_1 => Input.GetKeyDown(KeyCode.Keypad1) || Input.GetKeyDown(KeyCode.Alpha1);
			internal static bool KeyDown_2 => Input.GetKeyDown(KeyCode.Keypad2) || Input.GetKeyDown(KeyCode.Alpha2);
			internal static bool KeyDown_3 => Input.GetKeyDown(KeyCode.Keypad3) || Input.GetKeyDown(KeyCode.Alpha3);
			internal static bool KeyDown_4 => Input.GetKeyDown(KeyCode.Keypad4) || Input.GetKeyDown(KeyCode.Alpha4);
			internal static bool KeyDown_5 => Input.GetKeyDown(KeyCode.Keypad5) || Input.GetKeyDown(KeyCode.Alpha5);
			internal static bool KeyDown_6 => Input.GetKeyDown(KeyCode.Keypad6) || Input.GetKeyDown(KeyCode.Alpha6);

			internal static bool Key_LeftShift => Input.GetKey(KeyCode.LeftShift);
			internal static bool Key_RightShift => Input.GetKey(KeyCode.RightShift);

			internal static Vector2 Mouse_Position => Input.mousePosition;
			internal static float Mouse_ScrollWheel => Input.GetAxis("Mouse ScrollWheel");
			internal static bool Mouse_LeftDown => Input.GetMouseButton(0);
			internal static bool Mouse_RightDown => Input.GetMouseButton(1);
			internal static bool Mouse_MiddleDown => Input.GetMouseButton(2);
#endif
		}
	}
}