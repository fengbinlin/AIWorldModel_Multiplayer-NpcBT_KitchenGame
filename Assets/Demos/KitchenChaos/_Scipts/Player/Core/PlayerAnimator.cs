using System;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen.Player
{
    public class PlayerAnimator : NetworkBehaviour
    {
        private Player _player;
        private Animator _animator;

        private int _walking;
        

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _player = GetComponentInParent<Player>();
            _animator = GetComponent<Animator>();
            if (_player == null || _player.data == null || _animator == null)
                return;

            _walking = Animator.StringToHash(_player.data.animWalking);
            if (_player.MoveController == null)
                return;

            _player.MoveController.OnStartMove += _OnStartMove;
            _player.MoveController.OnStopMove += _OnStopMove;
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (_player?.MoveController == null) return;
            _player.MoveController.OnStartMove -= _OnStartMove;
            _player.MoveController.OnStopMove -= _OnStopMove;
        }
        private void _OnStopMove()
        {
            _animator.SetBool(_walking, false);
        }

        private void _OnStartMove()
        {
            _animator.SetBool(_walking, true);
        }
    }
}