using System;
using Kitchen;
using Nico.Components;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Kitchen
{
    public class KitchenObj : NetworkBehaviour
    {
        public KitchenObjEnum objEnum;

        protected ICanHoldKitchenObj holder;

        [field: SerializeField] public TransformFollower follower { get; private set; }

        private Rigidbody _rigidbody;
        private NetworkTransform _networkTransform;

        public bool IsFree { get; private set; }

        private void Awake()
        {
            follower = GetComponent<TransformFollower>();
            _rigidbody = GetComponent<Rigidbody>();
            _networkTransform = GetComponent<NetworkTransform>();

            // Ensure physics components exist. Must be called before NetworkObject.Spawn().
            // If already present (added in Awake), Spawn will pick them up.
            EnsurePhysicsComponents();

            // Rigidbody starts disabled — only enabled when free on server
            if (_rigidbody != null)
            {
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
            }
        }

        /// <summary>
        /// Ensures Rigidbody and NetworkTransform exist on this GameObject.
        /// Must be called BEFORE NetworkObject.Spawn() so NGO registers the NetworkTransform.
        /// Safe to call multiple times — only adds if missing.
        /// </summary>
        public void EnsurePhysicsComponents()
        {
            if (_rigidbody == null)
            {
                _rigidbody = gameObject.AddComponent<Rigidbody>();
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
                _rigidbody.drag = 2f;
                _rigidbody.angularDrag = 1f;
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }

            if (_networkTransform == null)
            {
                // Server-authoritative NetworkTransform (default, NOT ClientNetworkTransform)
                _networkTransform = gameObject.AddComponent<NetworkTransform>();
            }
        }

        /// <summary>
        /// Transition to free (on-ground) state.
        /// On server: enables physics. On clients: clears holder, stays kinematic.
        /// </summary>
        public void SetFree(Vector3 dropPosition, Vector3 dropDirection, float dropForce)
        {
            IsFree = true;
            holder = null;
            follower.SetFollowTarget(null);

            if (IsServer)
            {
                transform.position = dropPosition;
                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = false;
                    _rigidbody.useGravity = true;
                    _rigidbody.AddForce(dropDirection.normalized * dropForce, ForceMode.Impulse);
                }
            }
            else
            {
                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = true;
                    _rigidbody.useGravity = false;
                }
            }
        }

        /// <summary>
        /// Transition to held state. Disables physics, sets holder relationship.
        /// </summary>
        public void SetHeld(ICanHoldKitchenObj newHolder)
        {
            IsFree = false;

            if (_rigidbody != null)
            {
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
                _rigidbody.velocity = Vector3.zero;
            }

            SetHolder(newHolder);
        }

        public void SetHolder(ICanHoldKitchenObj iholder)
        {
            follower.SetFollowTarget(iholder.GetHoldTransform());
            holder = iholder;
        }

        public ICanHoldKitchenObj GetHolder()
        {
            return holder;
        }

        public void ClearHolder()
        {
            holder.ClearKitchenObj();
            holder = null;
        }
    }
}