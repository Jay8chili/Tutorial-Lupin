using System;
using System.Collections;
using UnityEngine;


public class Timer
{
    private readonly float _totalTime;
    private readonly MonoBehaviour _host;
    private float _timer;
    private bool _isRunning;
    private Coroutine _routine;

    public event Action OnTimerStart;
    public event Action<float> OnTimerRunning;
    public event Action OnTimerEnd;
    public DelayedEvent delayedEvent;

    /// <summary>
    /// <paramref name="host"/> is the MonoBehaviour this timer's coroutine runs on. A Unity
    /// coroutine (yield return null) is used instead of async/Task.Yield() specifically because
    /// coroutines resume at a fixed, guaranteed point in Unity's frame order — after that frame's
    /// FixedUpdate/physics trigger callbacks, never before. async void + Task.Yield() has no such
    /// guarantee: its continuation can occasionally resume ahead of a same-frame OnTriggerExit,
    /// which let a completing timer and a StopTimer() call from a trigger-exit interleave in the
    /// wrong order — leaving _isRunning stuck true and the timer permanently unable to restart.
    /// </summary>
    public Timer(float totalTime, MonoBehaviour host)
    {
        _totalTime = totalTime;
        _host = host;
    }

    public void StartTimer()
    {
        if (_isRunning) return;

        if (_host == null)
        {
            return;
        }

        _isRunning = true;
        OnTimerStart?.Invoke();

        _routine = _host.StartCoroutine(RunTimer());
    }

    private IEnumerator RunTimer()
    {
        while (_isRunning && _timer <= _totalTime)
        {
            yield return null;
            UpdateTimer();
        }

        if (!_isRunning)
        {
            // Stopped externally (StopTimer already cleared _isRunning/_routine) — nothing more to do.
            yield break;
        }

        // Reached completion naturally. Clear _routine before calling StopTimer so it doesn't
        // try to StopCoroutine the very routine that's currently executing it.
        _routine = null;
        StopTimer(true);
        OnTimerEnd?.Invoke();
    }

    public void StopTimer(bool shouldReset)
    {
        _isRunning = false;

        if (_routine != null && _host != null)
        {
            _host.StopCoroutine(_routine);
            _routine = null;
        }

        if (shouldReset)
        {
            _timer = 0;
        }
    }

    private void UpdateTimer()
    {
        _timer += Time.deltaTime;

        if (_timer <= _totalTime)
        {
            OnTimerRunning?.Invoke(Mathf.InverseLerp(0, _totalTime, _timer));
        }
    }
}
