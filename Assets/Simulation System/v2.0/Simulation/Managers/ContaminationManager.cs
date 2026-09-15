using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class ContaminationZone
{
    public string areaName;
    public List<Collider> colliders = new List<Collider>();

    [Tooltip("Single trigger collider defining the boundary of this zone for speed tracking.")]
    public Collider speedTrackingArea;

    [Tooltip("Max hand speed (m/s) allowed inside this zone.")]
    public float speedThreshold = 2f;

    [Tooltip("UI panel popped in when this zone's contamination is triggered.")]
    public GameObject uiPanel;

    [Tooltip("UI panel popped in when a hand moves too fast inside this zone's speed tracking area.")]
    public GameObject handSpeedUIPanel;

    // ── runtime state (per zone) ────────────────────────────────────
    [NonSerialized] public bool isTriggered;
    [NonSerialized] public Coroutine resolveRoutine;
}

[Serializable]
public class ContaminationTriggeredEvent : UnityEvent<string, GameObject> { }

[Serializable]
public class GameObjectEvent : UnityEvent<GameObject> { }

public class ContaminationManager : MonoBehaviour
{
    [Header("Zones")]
    public List<ContaminationZone> zones = new List<ContaminationZone>();

    [Header("Timing")]
    [Tooltip("How long a hand must stay inside a collider before contamination triggers.")]
    public float contactThreshold = 1f;

    [Tooltip("Delay after contamination is detected before the UI/error is actually shown. E.g. 3 = error pops up 3 seconds after detection.")]
    public float uiDelay = 0f;

    [Tooltip("How long the contamination UI stays visible before auto-resolving.")]
    public float displayDuration = 3f;

    [Header("[ HAND SPEED ]")]
    [Tooltip("Reference to the VRHandSpeedTracker that fires speed violation detected/resolved events. " +
             "ContaminationManager owns this subscription so it can route each violation to the correct zone's handSpeedUIPanel.")]
    public VRHandSpeedTracker vrHandSpeedTracker;

    [Header("Events")]
    // Every zone is monitored simultaneously and independently — any number of zones can be
    // triggered (contamination or speed) at the same time. Each event carries the specific
    // zone's panel so listeners can track multiple concurrently-open panels.
    public ContaminationTriggeredEvent onContaminationTriggered;
    public GameObjectEvent onContaminationResolved;

    public GameObjectEvent onSpeedViolationTriggered;
    public GameObjectEvent onSpeedViolationResolved;

    // ── private ──────────────────────────────────────────────────────
    private Dictionary<Collider, ContaminationZone> _colliderToZone = new Dictionary<Collider, ContaminationZone>();
    private Dictionary<SpeedTrackingArea, ContaminationZone> _areaToZone = new Dictionary<SpeedTrackingArea, ContaminationZone>();

    // ── lifecycle ────────────────────────────────────────────────────

    private void Awake()
    {
        BuildLookups();
        EnableAllSpeedTrackingAreas();
    }

    private void OnEnable()
    {
        ContaminationTrigger.OnHandContactDetected += NotifyTriggerEnter;

        if (vrHandSpeedTracker != null)
        {
            vrHandSpeedTracker.OnSpeedViolationDetected.AddListener(NotifySpeedViolationDetected);
            vrHandSpeedTracker.OnSpeedViolationResolved.AddListener(NotifySpeedViolationResolved);
        }
    }

    private void OnDisable()
    {
        ContaminationTrigger.OnHandContactDetected -= NotifyTriggerEnter;

        if (vrHandSpeedTracker != null)
        {
            vrHandSpeedTracker.OnSpeedViolationDetected.RemoveListener(NotifySpeedViolationDetected);
            vrHandSpeedTracker.OnSpeedViolationResolved.RemoveListener(NotifySpeedViolationResolved);
        }
    }

    // ── public API ───────────────────────────────────────────────────

    /// <summary>Re-enables a zone's speed tracking area. Zones are enabled by default — only needed after an explicit DisableZone.</summary>
    public void EnableZone(int index)
    {
        if (index < 0 || index >= zones.Count)
        {
            return;
        }

        ContaminationZone zone = zones[index];
        if (zone.speedTrackingArea != null)
            zone.speedTrackingArea.enabled = true;
    }

    /// <summary>
    /// Disables the zone at the given index — disables its speed tracking area, and stops
    /// any in-progress contamination resolve for that zone.
    /// </summary>
    public void DisableZone(int index)
    {
        if (index < 0 || index >= zones.Count)
        {
            return;
        }

        ContaminationZone zone = zones[index];

        // Disable speed tracking area for this zone
        if (zone.speedTrackingArea != null)
            zone.speedTrackingArea.enabled = false;

        // Stop hand speed tracking immediately via event — hands may still be inside the zone
        SpeedTrackingArea.DisableTracking();

        // Stop any in-progress resolve coroutine for this zone
        if (zone.resolveRoutine != null)
        {
            StopCoroutine(zone.resolveRoutine);
            zone.resolveRoutine = null;
        }

        zone.isTriggered = false;
    }

    // ── private ──────────────────────────────────────────────────────

    private void BuildLookups()
    {
        _colliderToZone.Clear();
        _areaToZone.Clear();

        foreach (ContaminationZone zone in zones)
        {
            foreach (Collider col in zone.colliders)
            {
                if (col != null)
                    _colliderToZone[col] = zone;
            }

            if (zone.speedTrackingArea != null)
            {
                SpeedTrackingArea area = zone.speedTrackingArea.GetComponent<SpeedTrackingArea>();
                if (area != null)
                    _areaToZone[area] = zone;
            }
        }
    }

    private void EnableAllSpeedTrackingAreas()
    {
        foreach (ContaminationZone zone in zones)
        {
            if (zone.speedTrackingArea == null) continue;
            zone.speedTrackingArea.enabled = true;
        }
    }

    private void NotifyTriggerEnter(Collider collider)
    {
        if (!_colliderToZone.TryGetValue(collider, out ContaminationZone zone)) return;
        if (zone.isTriggered) return;

        zone.isTriggered = true;

        if (zone.resolveRoutine != null) StopCoroutine(zone.resolveRoutine);
        zone.resolveRoutine = StartCoroutine(TriggerSequence(zone));
    }

    private IEnumerator TriggerSequence(ContaminationZone zone)
    {
        if (uiDelay > 0f)
            yield return new WaitForSeconds(uiDelay);

        onContaminationTriggered?.Invoke(zone.areaName, zone.uiPanel);

        yield return new WaitForSeconds(displayDuration);

        zone.isTriggered = false;
        zone.resolveRoutine = null;
        onContaminationResolved?.Invoke(zone.uiPanel);
    }

    private void NotifySpeedViolationDetected(SpeedTrackingArea area)
    {
        _areaToZone.TryGetValue(area, out ContaminationZone zone);
        onSpeedViolationTriggered?.Invoke(zone != null ? zone.handSpeedUIPanel : null);
    }

    private void NotifySpeedViolationResolved(SpeedTrackingArea area)
    {
        _areaToZone.TryGetValue(area, out ContaminationZone zone);
        onSpeedViolationResolved?.Invoke(zone != null ? zone.handSpeedUIPanel : null);
    }
}
