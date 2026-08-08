using UnityEngine;



namespace Kitchen

{

    public class StoveCounterVisual : MonoBehaviour

    {

        private ICookingFacility _cooking;

        private GameObject _stoveGameOnObject;

        private GameObject _particleObject;



        private void Awake()

        {

            _cooking = GetComponentInParent<ICookingFacility>();

            var onVisual = transform.Find("StoveOnVisual");

            var particles = transform.Find("SizzlingParticles");

            _stoveGameOnObject = onVisual != null ? onVisual.gameObject : null;

            _particleObject = particles != null ? particles.gameObject : null;

        }



        private void OnEnable()

        {

            if (_cooking == null)

                _cooking = GetComponentInParent<ICookingFacility>();

            if (_cooking == null) return;

            _cooking.OnStartCooking += OnStartCooking;

            _cooking.OnStopCooking += OnStopCooking;

        }



        private void OnDisable()

        {

            if (_cooking == null) return;

            _cooking.OnStartCooking -= OnStartCooking;

            _cooking.OnStopCooking -= OnStopCooking;

        }



        private void OnStopCooking()

        {

            if (_stoveGameOnObject != null) _stoveGameOnObject.SetActive(false);

            if (_particleObject != null) _particleObject.SetActive(false);

        }



        private void OnStartCooking()

        {

            if (_stoveGameOnObject != null) _stoveGameOnObject.SetActive(true);

            if (_particleObject != null) _particleObject.SetActive(true);

        }

    }

}


