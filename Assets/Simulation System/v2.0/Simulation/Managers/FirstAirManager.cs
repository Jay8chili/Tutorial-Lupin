using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class FirstAirZone
{
    public string areaName;
    public List<Collider> colliders = new List<Collider>();

    [Tooltip("UI panel popped in when this zone's first air is broken.")]
    public GameObject uiPanel;

    // ── runtime state (per zone) ────────────────────────────────────
    [NonSerialized] public bool isTriggered;
    [NonSerialized] public Coroutine resolveRoutine;
}

[Serializable]
public class FirstAirTriggeredEvent : UnityEvent<string, GameObject> { }

public class FirstAirManager : MonoBehaviour
{
    [Header("Zones")]
    public List<FirstAirZone> zones = new List<FirstAirZone>();

    [Header("Timing")]
    [Tooltip("How long a hand must stay inside a collider before a first air break triggers.")]
    public float contactThreshold = 1f;

    [Tooltip("Delay after a first air break is detected before the UI/error is actually shown. E.g. 3 = error pops up 3 seconds after detection.")]
    public float uiDelay = 0f;

    [Tooltip("How long the first air UI stays visible before auto-resolving.")]
    public float displayDuration = 3f;

    [Header("Events")]
    // Every zone is monitored simultaneously and independently — any number of zones can be
    // triggered at the same time. Each event carries the specific zone's panel so listeners
    // can track multiple concurrently-open panels.
    public FirstAirTriggeredEvent onFirstAirTriggered;
    public GameObjectEvent onFirstAirResolved;

    // ── private ──────────────────────────────────────────────────────
    private Dictionary<Collider, FirstAirZone> _colliderToZone = new Dictionary<Collider, FirstAirZone>();

    // ── lifecycle ────────────────────────────────────────────────────

    private void Awake()
    {
        BuildLookups();
    }

    private void OnEnable()
    {
        FirstAirTrigger.OnHandContactDetected += NotifyTriggerEnter;
    }

    private void OnDisable()
    {
        FirstAirTrigger.OnHandContactDetected -= NotifyTriggerEnter;
    }

    // ── public API ───────────────────────────────────────────────────

    /// <summary>Re-enables a zone's colliders. Zones are enabled by default — only needed after an explicit DisableZone.</summary>
    public void EnableZone(int index)
    {
        if (index < 0 || index >= zones.Count)
        {
            return;
        }

        FirstAirZone zone = zones[index];
        foreach (Collider col in zone.colliders)
        {
            if (col != null)
                col.enabled = true;
        }
    }

    /// <summary>
    /// Disables the zone at the given index — disables its colliders, and stops
    /// any in-progress first air resolve for that zone.
    /// </summary>
    public void DisableZone(int index)
    {
        if (index < 0 || index >= zones.Count)
        {
            return;
        }

        FirstAirZone zone = zones[index];

        foreach (Collider col in zone.colliders)
        {
            if (col != null)
                col.enabled = false;
        }

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

        foreach (FirstAirZone zone in zones)
        {
            foreach (Collider col in zone.colliders)
            {
                if (col != null)
                    _colliderToZone[col] = zone;
            }
        }
    }

    private void NotifyTriggerEnter(Collider collider)
    {
        if (!_colliderToZone.TryGetValue(collider, out FirstAirZone zone)) return;
        if (zone.isTriggered) return;

        zone.isTriggered = true;

        if (zone.resolveRoutine != null) StopCoroutine(zone.resolveRoutine);
        zone.resolveRoutine = StartCoroutine(TriggerSequence(zone));
    }

    private IEnumerator TriggerSequence(FirstAirZone zone)
    {
        if (uiDelay > 0f)
            yield return new WaitForSeconds(uiDelay);

        onFirstAirTriggered?.Invoke(zone.areaName, zone.uiPanel);

        yield return new WaitForSeconds(displayDuration);

        zone.isTriggered = false;
        zone.resolveRoutine = null;
        onFirstAirResolved?.Invoke(zone.uiPanel);
    }
}
