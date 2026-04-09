using System;
using Unity.Netcode;
using UnityEngine;

namespace CoreAI.ExampleGame.SymbiosisMode
{
    [RequireComponent(typeof(NetworkObject))]
    public class SymbiosisGhostPlayer : NetworkBehaviour
    {
        [Header("Game Settings (SO)")]
        [SerializeField] private Settings.SymbiosisGameSettings gameSettings;

        public float Health = 100f;
        public float MaxHealth = 100f;
        public float MoveSpeed = 5f;

        public event Action<float> OnChangePercentHealth;

        private void Start()
        {
            if (gameSettings != null)
            {
                MaxHealth = gameSettings.GhostMaxHealth;
                Health = MaxHealth;
                MoveSpeed = gameSettings.GhostMoveSpeed;
            }

            Renderer ren = GetComponentInChildren<Renderer>();
            if (ren != null)
            {
                ren.material.color = Color.blue; // Prototype Ghost Color
            }
        }

        public override void OnNetworkSpawn()
        {
            // Health sync handled by NetworkVariable auto-sync
        }

        private static void ApplyLitColor(Renderer r, Color c)
        {
            if (r == null) return;
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh);
            mat.SetColor(sh.name.Contains("Universal") ? "_BaseColor" : "_Color", c);
            r.sharedMaterial = mat;
        }

        void Update()
        {
            if (!IsOwner) return;

            // Combine New Input System and UI joystick
            float h = 0f;
            float v = 0f;
            bool attackPressed = false;

#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) h -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;

                if (kb.spaceKey.wasPressedThisFrame) attackPressed = true;
            }
            
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame) attackPressed = true;
            }
#endif

            Vector2 mobileInput = UI.MobileJoystick.InputVector;
            
            float finalH = Mathf.Clamp(h + mobileInput.x, -1f, 1f);
            float finalV = Mathf.Clamp(v + mobileInput.y, -1f, 1f);
            Vector3 move = new Vector3(finalH, 0, finalV);
            
            if (move != Vector3.zero)
            {
                transform.position += move * MoveSpeed * Time.deltaTime;
            }

            // Attack logic: Check keyboard, mouse, and mobile button
            if (attackPressed || UI.MobileAttackButton.WasJustPressed)
            {
                RequestAttackServerRpc();
            }
        }

        [ServerRpc]
        private void RequestAttackServerRpc()
        {
            // Simple AoE Attack
            float attackRadius = 3f;
            int attackDamage = 5;

            Collider[] hits = Physics.OverlapSphere(transform.position, attackRadius);
            foreach (var hit in hits)
            {
                var enemy = hit.GetComponent<CoreAI.ExampleGame.ArenaCombat.Infrastructure.ArenaEnemyBrain>();
                if (enemy != null)
                {
                    enemy.TakeDamage(attackDamage);
                }
            }
            
            // Replicate visuals
            AttackEffectsClientRpc();
        }

        [ClientRpc]
        private void AttackEffectsClientRpc()
        {
            Debug.Log($"[Client] Ghost {OwnerClientId} performed AoE attack!");
            // In a real project, we would spawn an explosion particle here
        }

        public void HealFromSkeleton(float amount)
        {
            if (!IsServer) return;
            
            ApplyHealthSync(Health + amount);
            Debug.Log($"[Server] Ghost Player {OwnerClientId} healed by {amount}! Current HP: {Health}");
            
            // Replicate visual/sound to clients
            HealGhostClientRpc(amount);
        }

        [ClientRpc]
        private void HealGhostClientRpc(float amount)
        {
            if (IsServer) return; // Server already applied
            ApplyHealthSync(Health + amount);
            Debug.Log($"[Client] Ghost {OwnerClientId} visualized heal of {amount}.");
        }

        private void ApplyHealthSync(float newHealth)
        {
            Health = Mathf.Clamp(newHealth, 0, MaxHealth);
            OnChangePercentHealth?.Invoke(Health / MaxHealth);
        }
    }
}
