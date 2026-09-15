using SimulationSystem.V0._1.Utility.Miscellanous;
using SimulationSystem.V02.Extensions;
using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit;

namespace SimulationSystem.V02.Utility
{
    public class TeleportManager : MonoBehaviour
    {
        public static TeleportManager Instance { get; private set; }

        [SerializeField] private ScreenFade OVR;
        [SerializeField] private AudioSource promptAudioSrc;

        [Header("Object Teleport")]
        [Tooltip("Offset applied in front of the player when teleporting an object. " +
                 "X = right, Y = up, Z = forward.")]
        public Vector3 objectTeleportOffset = new Vector3(0f, 0f, 1f);

        [Header("Teleport Message")]
        [SerializeField] private string teleportMessage = "Adjusting your position...";
        [Tooltip("How long the message stays on screen before the reveal")]
        [SerializeField] private float messageHoldTime = 1f;

        // Fired when the player teleport coroutine fully completes (fade in done).
        // SimulationState subscribes to this to delay starting interactions.
        public static Action TeleportStarted;
        public static Action TeleportCompleted;

        #region Teleport Movement Lock Fix (Hadhil)
        // ─────────────────────────────────────────────────────────────────
        // FIX (Hadhil): teleport movement bypass
        // ISSUE: while the player teleports, transform.position is snapped
        //        directly and only the CharacterController is disabled
        //        (Cc.enabled = false). XR Interaction Toolkit's locomotion
        //        providers (e.g. DynamicMoveProvider) still read stick input
        //        during that window, and when the CharacterController is
        //        disabled they fall back to translating the transform
        //        directly instead of calling Cc.Move(). That drift combines
        //        with the teleport's own position set, so the player can end
        //        up in a random spot instead of exactly at the teleport target.
        // FIX: cache every LocomotionProvider under the rig and disable them
        //      alongside the CharacterController for the duration of the
        //      teleport, then re-enable both together. This stops locomotion
        //      input from moving the player at all while we're mid-teleport,
        //      instead of only blocking the collision path.
        // WHY NO INSPECTOR FIELD: providers are found at runtime via
        //      GetComponentsInChildren<LocomotionProvider>(true) instead of
        //      being dragged into the Inspector. XR Interaction Toolkit
        //      updates can add/rename/replace locomotion providers on the rig
        //      (e.g. DynamicMoveProvider), and a manually-wired reference
        //      would silently go stale (MissingReferenceException or just
        //      not covering the new provider) after a package update. Auto
        //      discovery means this keeps working with zero manual rewiring.
        // FOLLOW-UP ISSUE (Hadhil): first version filtered nothing and used
        //      GetComponentsInChildren<LocomotionProvider>(true) as-is. That
        //      also caught GravityProvider, an XRIT LocomotionProvider that
        //      is NOT player-input driven -- it just applies falling/ground
        //      snapping every frame. Disabling it during teleport and then
        //      re-enabling it made it reset its grounded state fresh at the
        //      teleport target; if the target's pivot sat even slightly above
        //      the floor (CharacterController center/skinWidth offset,
        //      imprecise teleport point), GravityProvider treated the player
        //      as ungrounded and applied a visible fall from that height right
        //      after teleporting. GravityProvider was never part of the
        //      original random-position bug (that was input-driven movement),
        //      so it is now excluded from the disable/enable list and stays
        //      running normally throughout the teleport.
        // ─────────────────────────────────────────────────────────────────
        private LocomotionProvider[] moveProviders;
        #endregion
        private CharacterController Cc;
        private bool isFadeAudio = true;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                return;
            }
            Instance = this;
        }

        void Start()
        {
            Cc = GetComponent<CharacterController>();
            // Discover locomotion providers on the rig at runtime -- see FIX note above.
            moveProviders = GetComponentsInChildren<LocomotionProvider>(true)
                .Where(mp => mp is not UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity.GravityProvider)
                .ToArray();
        }

        public void UpdatePlayerPos(Transform newPos)
        {
            StartCoroutine(SyncPlayerPosition(newPos));
        }

        IEnumerator SyncPlayerPosition(Transform pos)
        {
            TeleportStarted?.Invoke();

            // ── Fade out (sphere fills bottom → top) ────────────────────
            FadeAudio(true);

            bool fadeOutDone = false;
            OVR.OnFadeOutComplete += FlagDone;
            OVR.FadeOut();

            // Wait until the sphere is fully filled
            yield return new WaitUntil(() => fadeOutDone);

            Cc.enabled = false;
            // Suspend locomotion input so it can't move the player while we teleport (see FIX note above).
            foreach (var mp in moveProviders) mp.enabled = false;
            // ── Move the player while the screen is fully black ─────────
            transform.position = pos.position;
            transform.rotation = pos.rotation;

            // Small extra frame to let physics / tracking settle
            yield return null;

            // ── Show transition message ─────────────────────────────────
            OVR.ShowMessage(teleportMessage);
            yield return new WaitForSeconds(messageHoldTime);
            OVR.HideMessage();

            // Brief pause so the text finishes fading out before the sphere drains
            yield return new WaitForSeconds(0.3f);

            // ── Fade in (sphere drains top → bottom) ────────────────────
            bool fadeInDone = false;
            OVR.OnFadeInComplete += FlagRevealDone;
            OVR.FadeIn();

            yield return new WaitUntil(() => fadeInDone);

            Cc.enabled = true;
            // Restore locomotion input now that the teleport is complete.
            foreach (var mp in moveProviders) mp.enabled = true;

            FadeAudio();

            TeleportCompleted?.Invoke();

            // ── Local helpers to avoid allocations from lambdas ─────────
            void FlagDone()
            {
                fadeOutDone = true;
                OVR.OnFadeOutComplete -= FlagDone;
            }
            void FlagRevealDone()
            {
                fadeInDone = true;
                OVR.OnFadeInComplete -= FlagRevealDone;
            }
        }

        /// <summary>
        /// Teleports the given object to a position in front of the player,
        /// applying objectTeleportOffset in the player's local space.
        /// </summary>
        public void TeleportObject(GameObject obj)
        {
            if (obj == null)
            {
                return;
            }

            Vector3 targetPosition = transform.position
                                   + transform.right * objectTeleportOffset.x
                                   + transform.up * objectTeleportOffset.y
                                   + transform.forward * objectTeleportOffset.z;

            obj.transform.position = targetPosition;
        }

        void FadeAudio(bool fadeout = false)
        {
            if (isFadeAudio)
            {
                if (fadeout)
                {
                    promptAudioSrc.mute = true;
                }
                else
                {
                    promptAudioSrc.time = 0;
                    promptAudioSrc.mute = false;
                    if (SimulationManager.Instance.simulationMode == SimulationMode.Guided)
                    {
                        promptAudioSrc.Play();
                    }
                }
            }
        }
    }
}