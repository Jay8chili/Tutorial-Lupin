Grab Pose Studio v1.1.2
Simulation System SDK — 8Chili Technologies

Purpose
-------
Single-developer VR workflow for recording controller grab poses directly
inside Play Mode. Replaces the two-developer Grab Pose Recorder workflow.

Quick Start
-----------
1. Open your simulation scene.
2. Connect Quest Link and confirm controllers are active.
3. Tools → Grab Pose Studio → Launch Studio.
   - If GrabPoseStudio.unity is missing from Build Settings, a dialog will
     appear. Click "Add It For Me" — the scene is added automatically.
4. Unity enters Play Mode and the Studio loads automatically.
5. The first grabbable appears at the Recording Point.
6. Pose your hand/controller and press the record button.
7. Navigate with B (Next) and Y (Previous) to move between objects.
8. Once every grab has both Left+Right recorded (this session or already on
   disk), a red "Exit Play Mode" bar appears on the status panel — touch it
   with your hand/controller to exit. Assets persist to Assets/RecordedPoses/.

Controls
--------
X  →  Record Right pose
A  →  Record Left pose
B  →  Next object
Y  →  Previous object
Touch the "Exit Play Mode" panel button → exit Play Mode (appears only once
  the current grab set is fully recorded)

Notes
-----
- Assets/RecordedPoses/ is created automatically on first recording.
- Recorded assets are compatible with the existing Grab Pose Recorder
  assignment and bake workflow.
- Enabling/disabling a GrabInteraction at runtime updates the Studio's
  navigation list automatically (0.5s poll) — no relaunch needed.
- The platform has a teleport-back safety net: walking past its edge snaps
  the rig back to centre instead of letting you wander off.
- Hand tracking recording is not supported yet.
- Automatic pose assignment and baking are not supported yet.

Documentation
-------------
See: Documentation/Grab Pose Studio v1.0.pdf