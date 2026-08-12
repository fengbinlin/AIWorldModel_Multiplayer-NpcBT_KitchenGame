// Toony Colors Pro+Mobile 2
// (c) 2014-2026 Jean Moreno

using UnityEngine;
using UnityEngine.EventSystems;
#if TCP2_NEW_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace ToonyColorsPro
{
	namespace Demo
	{
		public class TCP2_Demo_Camera : MonoBehaviour
		{
			//--------------------------------------------------------------------------------------------------
			// PUBLIC INSPECTOR PROPERTIES

			public Transform Pivot;
			public Vector3 pivotOffset;
			[Header("Orbit")]
			public float OrbitStrg = 3f;
			public float OrbitClamp = 50f;
			[Header("Panning")]
			public float PanStrgMin = 0.1f;
			public float PanStrgMax = 0.5f;
			[Header("Zooming")]
			public float ZoomStrg = 40f;
			public float ZoomClamp = 30f;
			public float ZoomDistMin = 1f;
			public float ZoomDistMax = 2f;
			[Header("Misc")]
			public float Decceleration = 8f;
			public RectTransform ignoreMouseRect;
			public EventSystem uiEventSystem;
			Rect ignoreMouseActualRect;

			//--------------------------------------------------------------------------------------------------
			// PRIVATE PROPERTIES

			Vector2 mouseDelta;
			Vector2 lastMousePos;
			Vector3 orbitAcceleration;
			Vector3 panAcceleration;
			Vector3 moveAcceleration;
			float zoomAcceleration;
			float zoomDistance;
			const float XMax = 60;
			const float XMin = 300;
			Vector3 mResetCamPos, mResetPivotPos, mResetCamRot, mResetPivotRot;

			//--------------------------------------------------------------------------------------------------
			// UNITY EVENTS

			void Awake()
			{
				mResetCamPos = transform.position;
				mResetCamRot = transform.eulerAngles;
				mResetPivotPos = Pivot.position;
				mResetPivotRot = Pivot.eulerAngles;

				if (uiEventSystem != null)
				{
#if TCP2_NEW_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
					uiEventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
#else
					uiEventSystem.gameObject.AddComponent<StandaloneInputModule>();
#endif
				}
			}

			void OnEnable()
			{
				lastMousePos = InputAbstraction.Mouse_Position;

				Vector2 size = Vector2.Scale(ignoreMouseRect.rect.size, ignoreMouseRect.lossyScale);
				Rect rect = new Rect(ignoreMouseRect.position.x, ignoreMouseRect.position.y, size.x, size.y);
				rect.x -= (ignoreMouseRect.pivot.x * size.x);
				rect.y -= ((-ignoreMouseRect.pivot.y) * size.y);

				ignoreMouseActualRect = rect;
			}

			void FixedUpdate()
			{
				var mousePos = InputAbstraction.Mouse_Position;
				mouseDelta = mousePos - lastMousePos;
				mouseDelta.x = Mathf.Clamp(mouseDelta.x, -150f, 150f);
				mouseDelta.y = Mathf.Clamp(mouseDelta.y, -150f, 150f);
				lastMousePos = mousePos;
			}

			void Update()
			{
				// mouseDelta = InputAbstraction.Mouse_Position - mouseDelta;

				var ignoreMouse = ignoreMouseRect != null && ignoreMouseActualRect.Contains(InputAbstraction.Mouse_Position);

				//Left Button held
				if (!ignoreMouse && InputAbstraction.Mouse_LeftDown)
				{
					orbitAcceleration.x += Mathf.Clamp(mouseDelta.x * OrbitStrg, -OrbitClamp, OrbitClamp);
					orbitAcceleration.y += Mathf.Clamp(-mouseDelta.y * OrbitStrg, -OrbitClamp, OrbitClamp);
				}
				//Middle/Right Button held
				else if (!ignoreMouse && (InputAbstraction.Mouse_MiddleDown || InputAbstraction.Mouse_RightDown))
				{
					var str = Mathf.Lerp(PanStrgMin, PanStrgMax, Mathf.Clamp01((zoomDistance - ZoomDistMin) / (ZoomDistMax - ZoomDistMin)));
					panAcceleration.x = -mouseDelta.x * str;
					panAcceleration.y = -mouseDelta.y * str;
				}

				//Keyboard support
				//orbitAcceleration.x += Input.GetKey(KeyCode.LeftArrow) ? 15 : (Input.GetKey(KeyCode.RightArrow) ? -15 : 0);
				//orbitAcceleration.y += Input.GetKey(KeyCode.UpArrow) ? 15 : (Input.GetKey(KeyCode.DownArrow) ? -15 : 0);

				if (InputAbstraction.KeyDown_R)
				{
					ResetView();
				}

				//X Angle Clamping
				var angle = transform.localEulerAngles;
				if (angle.x < 180 && angle.x >= XMax && orbitAcceleration.y > 0) orbitAcceleration.y = 0;
				if (angle.x > 180 && angle.x <= XMin && orbitAcceleration.y < 0) orbitAcceleration.y = 0;

				Vector3 pivotPlusOffset = Pivot.position + pivotOffset;

				//Rotate
				transform.RotateAround(pivotPlusOffset, transform.right, orbitAcceleration.y * Time.deltaTime);
				transform.RotateAround(pivotPlusOffset, Vector3.up, orbitAcceleration.x * Time.deltaTime);

				//Pan
				pivotOffset += transform.TransformDirection(panAcceleration) * Time.deltaTime;
				//Pivot.Translate(panAcceleration * Time.deltaTime, transform);
				transform.Translate(panAcceleration * Time.deltaTime, transform);

				//Zoom
				var scrollWheel = InputAbstraction.Mouse_ScrollWheel;
				scrollWheel = scrollWheel > 0 ? 0.1f : (scrollWheel < 0 ? -0.1f : 0);

				zoomAcceleration += scrollWheel * ZoomStrg;
				zoomAcceleration = Mathf.Clamp(zoomAcceleration, -ZoomClamp, ZoomClamp);
				zoomDistance = Vector3.Distance(transform.position, pivotPlusOffset);
				if ((zoomDistance >= ZoomDistMin && zoomAcceleration > 0) || (zoomDistance <= ZoomDistMax && zoomAcceleration < 0))
				{
					transform.Translate(Vector3.forward * (zoomAcceleration * Time.deltaTime), Space.Self);
				}

				//Decelerate
				orbitAcceleration = Vector3.Lerp(orbitAcceleration, Vector3.zero, Decceleration * Time.deltaTime);
				panAcceleration = Vector3.Lerp(panAcceleration, Vector3.zero, Decceleration * Time.deltaTime);
				zoomAcceleration = Mathf.Lerp(zoomAcceleration, 0, Decceleration * Time.deltaTime);
				moveAcceleration = Vector3.Lerp(moveAcceleration, Vector3.zero, Decceleration * Time.deltaTime);

				// mouseDelta = InputAbstraction.Mouse_Position;
			}

			//--------------------------------------------------------------------------------------------------
			// MISC

			void ResetView()
			{
				moveAcceleration = Vector3.zero;
				orbitAcceleration = Vector3.zero;
				panAcceleration = Vector3.zero;
				pivotOffset = Vector3.zero;
				zoomAcceleration = 0f;

				transform.position = mResetCamPos;
				transform.eulerAngles = mResetCamRot;
				Pivot.position = mResetPivotPos;
				Pivot.eulerAngles = mResetPivotRot;
			}
		}
	}
}