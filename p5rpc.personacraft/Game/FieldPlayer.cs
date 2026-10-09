using System.Diagnostics;
using System.Numerics;
using p5rpc.lib.interfaces;
using p5rpc.personacraft.Link;
using p5rpc.personacraft.World;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace p5rpc.personacraft.Game;

/// <summary>What the game thread saw this frame, for the logic thread. Immutable; swapped whole.</summary>
internal sealed record FieldSnapshot(
    long Qpc,
    FieldFrame Field,
    int PcHandle,
    int PcState,
    bool HasJoker,
    Vector3 Joker,          // feet, cm
    bool HasCamera,
    Vector3 CameraPos,      // cm
    float CameraYaw,        // Minecraft degrees, from P5R's camera
    float CameraPitch,
    bool Following);

/// <summary>What the logic thread wants the game thread to do with Joker and the camera. Immutable.</summary>
internal sealed record FollowCommand(
    bool Follow,
    Proto.GuestState Guest,
    float Yaw,
    float Pitch,
    bool HideJoker);

/// <summary>
/// Everything that touches P5R's field player, run on the game thread from a hook on
/// fldPCMoveUpdate (the field player's per-frame update; signature from rirurin/p5r-freecam).
///
/// The game's own update always runs first. With P5R's keyboard filtered out Joker stands idle in
/// it, so his talk/door prompts, state machine and triggers keep working; afterwards, while
/// Minecraft owns him, Joker is moved to Minecraft's player. Position is written through the same
/// native getter the flowscript FLD_MODEL_GET_*_TRANSLATE functions use (a pointer to the model's
/// translation), plus every copy of that position in the field player's work struct found when the
/// takeover starts. Camera, facing and visibility go through P5R's own flowscript functions, which
/// run immediately on this thread.
/// </summary>
internal sealed unsafe class FieldPlayer
{
    // fldPCMoveUpdate(task, delta). Its first instruction loads the player's work struct from task+0x48.
    private const string PcMoveUpdateSig = "40 53 48 83 EC 50 48 8B 59 ?? 0F 29 74 24 ?? 0F 28 F1";

    // The flowscript function FLD_MODEL_GET_X_TRANSLATE: walks the model table for a handle, then calls
    // the getter that returns a pointer to the model's translation (x, y, z floats).
    private const string ModelTranslateSig =
        "48 83 EC 28 33 C9 E8 ?? ?? ?? ?? 85 C0 78 ?? 48 8D 15 ?? ?? ?? ?? 4C 8D 05 ?? ?? ?? ?? 0F 1F 00 " +
        "48 8B 0A 48 85 C9 74 ?? 39 41 08 74 ?? 48 8B 89 C8 02 00 00 48 85 C9 75 ?? 48 83 C2 08 49 3B D0 7C ?? " +
        "48 8B 05 ?? ?? ?? ?? C7 80 D8 01 00 00 00 00 00 00 C6 40 5F 01 B8 01 00 00 00 48 83 C4 28 C3 " +
        "48 85 C9 74 ?? E8 ?? ?? ?? ?? F3 0F 10 00";

    private const int TaskWorkOffset = 0x48;
    private const int WorkStateOffset = 0x2C50;
    private const int WorkScanBytes = 0x14A80; // fldPCMoveUpdate itself touches the work struct up to +0x14A30
    private const int MaxPositionCopies = 8;
    private const int ModelHandleOffset = 0x8;
    private const int ModelNextOffset = 0x2C8;

    [Function(CallingConventions.Microsoft)]
    private delegate void PcMoveUpdate(nint task, float delta);

    private readonly IFlowCaller _flow;
    private readonly Settings _settings;
    private readonly long _qpcFrequency;
    private IHook<PcMoveUpdate>? _hook;

    private nint _modelTableStart, _modelTableEnd;
    private delegate* unmanaged<nint, float*> _getTranslate;

    private FieldFrame _field;
    private int _pcHandle = -1;
    private float* _jokerPos;
    private int _frame;
    private bool _camForwardMinusZ = true;
    private bool _calibrated;
    private Vector3 _cameraPos;
    private float _cameraYaw, _cameraPitch;
    private bool _hasCamera;

    private bool _following;
    private readonly object _followGate = new();
    private Task? _release; // a release queued from the logic thread; no new takeover until it ran
    private nint _work;
    private readonly List<int> _copies = new();
    private bool _jokerHidden;
    private float _lastJokerYaw = float.NaN;
    private bool _flowSlow;
    private bool _failed;
    private int _threadChecked; // 0 unknown, 1 main loop thread, -1 another thread
    private float _fovY = 45f;

    private volatile FieldSnapshot _snapshot = new(0, default, -1, -1, false, default, false, default, 0, 0, false);
    private volatile FollowCommand _command = new(false, default, 0, 0, false);

    public FieldPlayer(IFlowCaller flow, Settings settings, long qpcFrequency)
    {
        _flow = flow;
        _settings = settings;
        _qpcFrequency = qpcFrequency;
    }

    public FieldSnapshot Snapshot => _snapshot;

    /// <summary>The camera Minecraft drives this frame; null when P5R has its own.</summary>
    public volatile CameraView? View;
    public bool Ready => _hook != null && _getTranslate != null && !_failed;

    public void SetCommand(FollowCommand command) => _command = command;

    public void Hook(IStartupScanner scanner, IReloadedHooks hooks)
    {
        nint module = Process.GetCurrentProcess().MainModule!.BaseAddress;
        scanner.AddMainModuleScan(ModelTranslateSig, result =>
        {
            if (!result.Found)
            {
                Log.Error("field player: model translate function not found (game version?); Minecraft will not take over");
                return;
            }
            byte* fn = (byte*)(module + result.Offset);
            _modelTableStart = (nint)(fn + 0x16 + *(int*)(fn + 0x12));
            _modelTableEnd = (nint)(fn + 0x1D + *(int*)(fn + 0x19));
            _getTranslate = (delegate* unmanaged<nint, float*>)(fn + 0x6B + *(int*)(fn + 0x67));
            Log.Info($"field player: model table 0x{_modelTableStart:X}..0x{_modelTableEnd:X}, translate getter 0x{(nint)_getTranslate:X}");
        });
        scanner.AddMainModuleScan(PcMoveUpdateSig, result =>
        {
            if (!result.Found)
            {
                Log.Error("field player: fldPCMoveUpdate not found (game version?); Minecraft will not take over");
                return;
            }
            _hook = hooks.CreateHook<PcMoveUpdate>(OnPcMoveUpdate, module + result.Offset).Activate();
            Log.Info($"field player: hooked fldPCMoveUpdate at 0x{module + result.Offset:X}");
        });
    }

    private void OnPcMoveUpdate(nint task, float delta)
    {
        _hook!.OriginalFunction(task, delta);
        if (_failed)
            return;
        try
        {
            AfterUpdate(task);
        }
        catch (Exception e)
        {
            _failed = true;
            Log.Error($"field player: disabled after an error: {e}");
            _snapshot = _snapshot with { Following = false };
        }
    }

    private void AfterUpdate(nint task)
    {
        _frame++;
        if (!OnMainLoopThread())
            return;
        nint work = *(nint*)(task + TaskWorkOffset);
        int state = work != 0 ? *(ushort*)(work + WorkStateOffset) : -1;

        // Field and Joker's model handle change rarely; ask twice a second.
        if (_frame % 30 == 1 || !_field.IsValid)
            RefreshField();

        Vector3 joker = default;
        bool hasJoker = _jokerPos != null;
        if (hasJoker)
            joker = new Vector3(_jokerPos[0], _jokerPos[1], _jokerPos[2]);

        var command = _command;
        bool follow = command.Follow && hasJoker && _settings.TakeOver && !_flowSlow && _getTranslate != null;
        bool following;
        lock (_followGate)
        {
            if (follow && !_following && (_release?.IsCompleted ?? true))
                BeginFollow(work, joker);
            else if (!follow && _following)
                EndFollow();
            if (!_following)
                View = null;

            if (_following)
                joker = Apply(command, work);
            following = _following;
        }
        if (!following && _frame % 6 == 0)
            ReadCamera(joker);

        _snapshot = new FieldSnapshot(HostLink.Qpc(), _field, _pcHandle, state, hasJoker, joker,
            _hasCamera, _cameraPos, _cameraYaw, _cameraPitch, following);
    }

    /// <summary>
    /// Called from the logic thread when the field player stopped updating while Minecraft had Joker
    /// (a door, a loading screen, a menu, a battle). The release that normally runs inside the update
    /// is done here instead: P5R's transitions can wait on the camera, so it must not stay locked.
    /// The flowscript calls are queued to the game's main loop by p5rpc.lib, off this thread, with no
    /// lock held while they wait.
    /// </summary>
    public void ReleaseWhilePaused()
    {
        int handle;
        bool hidden;
        lock (_followGate)
        {
            if (!_following)
                return;
            _following = false;
            hidden = _jokerHidden;
            _jokerHidden = false;
            handle = _pcHandle;
            View = null;
        }
        _snapshot = _snapshot with { Following = false };
        _release = Task.Run(() =>
        {
            try
            {
                _flow.FLD_CAMERA_UNLOCK();
                if (hidden && handle >= 0)
                    _flow.FLD_MODEL_SET_VISIBLE(handle, 1, 0);
                Log.Info("field player: field player paused; released the camera and Joker to P5R");
            }
            catch (Exception e)
            {
                Log.Error($"field player: releasing while paused failed: {e.Message}");
            }
        });
    }

    /// <summary>
    /// p5rpc.lib runs flowscript calls immediately only on its main loop thread and queues them from
    /// any other, waiting for the main loop. Calling from a game thread that the main loop itself
    /// waits on would deadlock, so nothing is called unless this hook runs on that very thread.
    /// </summary>
    private bool OnMainLoopThread()
    {
        if (_threadChecked != 0)
            return _threadChecked > 0;
        var field = _flow.GetType().GetField("_mainThread", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
        {
            _threadChecked = -1;
            _failed = true;
            Log.Error("field player: cannot see p5rpc.lib's main loop thread (library changed?); takeover disabled");
            return false;
        }
        if (field.GetValue(_flow) is not Thread main)
            return false; // the main loop has not run yet; ask again next frame
        _threadChecked = main == Thread.CurrentThread ? 1 : -1;
        if (_threadChecked < 0)
        {
            _failed = true;
            Log.Error($"field player: fldPCMoveUpdate runs on thread {Environment.CurrentManagedThreadId}, not p5rpc.lib's main loop thread {main.ManagedThreadId}; takeover disabled to avoid a deadlock");
        }
        else
        {
            Log.Info("field player: fldPCMoveUpdate runs on the main loop thread; flowscript calls are immediate");
        }
        return _threadChecked > 0;
    }

    private void RefreshField()
    {
        var watch = Stopwatch.StartNew();
        var field = new FieldFrame(_flow.FLD_GET_MAJOR(), _flow.FLD_GET_MINOR());
        int handle = _flow.FLD_PC_GET_RESHND(0);
        if (watch.ElapsedMilliseconds > 20 && !_flowSlow)
        {
            // p5rpc.lib queues calls made off its main loop thread; per-frame calls would pile up.
            _flowSlow = true;
            Log.Error($"field player: flowscript calls from fldPCMoveUpdate took {watch.ElapsedMilliseconds} ms (not the main loop thread?); takeover disabled");
        }
        if (field != _field)
        {
            Log.Info($"field player: field {_field} -> {field}");
            _field = field;
            _calibrated = false;
        }
        if (handle != _pcHandle || _jokerPos == null)
        {
            _pcHandle = handle;
            _jokerPos = handle >= 0 ? FindTranslate(handle) : null;
            if (_settings.Verbose)
                Log.Info($"field player: Joker handle {handle}, translate {(nint)_jokerPos:X}");
        }
    }

    /// <summary>Same walk as FLD_MODEL_GET_X_TRANSLATE: buckets of linked models, matched by handle.</summary>
    private float* FindTranslate(int handle)
    {
        if (_getTranslate == null)
            return null;
        for (nint bucket = _modelTableStart; bucket < _modelTableEnd; bucket += 8)
        {
            for (nint model = *(nint*)bucket; model != 0; model = *(nint*)(model + ModelNextOffset))
            {
                if (*(int*)(model + ModelHandleOffset) == handle)
                    return _getTranslate(model);
            }
        }
        return null;
    }

    private void ReadCamera(Vector3 joker)
    {
        var pos = new Vector3(_flow.FLD_CAMERA_GET_X_POS(), _flow.FLD_CAMERA_GET_Y_POS(), _flow.FLD_CAMERA_GET_Z_POS());
        var rot = new Quaternion(_flow.FLD_CAMERA_GET_X_ROT(), _flow.FLD_CAMERA_GET_Y_ROT(), _flow.FLD_CAMERA_GET_Z_ROT(), _flow.FLD_CAMERA_GET_W_ROT());
        if (!float.IsFinite(rot.W) || rot.LengthSquared() < 0.5f)
        {
            _hasCamera = false;
            return;
        }
        rot = Quaternion.Normalize(rot);
        // P5R's third-person camera looks at Joker: whichever of the camera's -Z and +Z points at
        // him is its forward axis.
        var minusZ = Vector3.Transform(-Vector3.UnitZ, rot);
        var toJoker = joker + new Vector3(0, 100, 0) - pos;
        if (!_calibrated && toJoker.Length() > 50f)
        {
            float dot = Vector3.Dot(minusZ, Vector3.Normalize(toJoker));
            if (MathF.Abs(dot) > 0.5f)
            {
                _calibrated = true;
                _camForwardMinusZ = dot > 0;
                Log.Info($"field player: camera looks along its {(_camForwardMinusZ ? "-Z" : "+Z")} axis (dot {dot:F2}); camera {pos:F0}, rot {rot:F3}, Joker {joker:F0}");
            }
        }
        var forward = _camForwardMinusZ ? minusZ : -minusZ;
        (_cameraYaw, _cameraPitch) = Look.FromForward(forward);
        _cameraPos = pos;
        _hasCamera = true;
    }

    private void BeginFollow(nint work, Vector3 joker)
    {
        _following = true;
        _work = work;
        _copies.Clear();
        // Every other copy of Joker's position in the field player's work struct gets the same writes.
        // Only when the position is distinctive: near the origin, zeroed memory would match too.
        if (work != 0 && MathF.Abs(joker.X) + MathF.Abs(joker.Z) > 10f)
        {
            for (int off = 0; off + 12 <= WorkScanBytes; off += 4)
            {
                var p = (float*)(work + off);
                if (MathF.Abs(p[0] - joker.X) < 0.5f && MathF.Abs(p[1] - joker.Y) < 0.5f && MathF.Abs(p[2] - joker.Z) < 0.5f
                    && (nint)p != (nint)_jokerPos)
                    _copies.Add(off);
            }
            if (_copies.Count > MaxPositionCopies)
            {
                Log.Warn($"field player: {_copies.Count} matches for Joker's position in the work struct; writing none of them");
                _copies.Clear();
            }
        }
        _flow.FLD_CAMERA_LOCK();
        _lastJokerYaw = float.NaN;
        _fovY = _flow.FLD_CAMERA_GET_FOVY();
        Log.Info($"field player: following Minecraft (Joker {joker:F0} cm, state {(work != 0 ? *(ushort*)(work + WorkStateOffset) : -1)}, " +
                 $"y-rot {_flow.FLD_MODEL_GET_Y_ROTATE(_pcHandle):F1}, position copies in the work struct at [{string.Join(", ", _copies.Select(o => $"0x{o:X}"))}])");
    }

    private void EndFollow()
    {
        _following = false;
        _flow.FLD_CAMERA_UNLOCK();
        if (_jokerHidden && _pcHandle >= 0)
            _flow.FLD_MODEL_SET_VISIBLE(_pcHandle, 1, 0);
        _jokerHidden = false;
        Log.Info("field player: released Joker and the camera to P5R");
    }

    private Vector3 Apply(FollowCommand command, nint work)
    {
        var guest = command.Guest;
        var (feetMc, eyeHeight) = Interpolate(guest);
        var feet = _field.ToGame(feetMc.X, feetMc.Y, feetMc.Z);

        _jokerPos[0] = feet.X;
        _jokerPos[1] = feet.Y;
        _jokerPos[2] = feet.Z;
        if (work == _work && work != 0)
        {
            foreach (int off in _copies)
            {
                var p = (float*)(work + off);
                p[0] = feet.X;
                p[1] = feet.Y;
                p[2] = feet.Z;
            }
        }

        // Joker faces where the player looks. Model forward is +Z: rotating it by -yaw about Y
        // gives Minecraft's forward (-sin yaw, 0, cos yaw).
        float jokerYaw = Look.WrapDegrees(-command.Yaw);
        if (float.IsNaN(_lastJokerYaw) || MathF.Abs(Look.WrapDegrees(jokerYaw - _lastJokerYaw)) > 1f)
        {
            _flow.FLD_MODEL_SET_ROTATE(_pcHandle, 0f, jokerYaw, 0f, 0);
            _lastJokerYaw = jokerYaw;
        }

        // Joker is hidden in every camera mode: in F5 Minecraft's own player model is drawn instead.
        bool hide = command.HideJoker;
        if (hide != _jokerHidden)
        {
            _flow.FLD_MODEL_SET_VISIBLE(_pcHandle, hide ? 0 : 1, 0);
            _jokerHidden = hide;
        }

        // Camera: Minecraft's eye, or its F5 positions (1 behind, 2 in front looking back).
        var eye = feet + new Vector3(0, eyeHeight * (float)FieldFrame.CmPerBlock, 0);
        float yaw = command.Yaw, pitch = command.Pitch;
        var forward = Look.Forward(yaw, pitch);
        var position = eye;
        if (guest.cameraMode == 1)
        {
            position = eye - forward * guest.cameraDistance * (float)FieldFrame.CmPerBlock;
        }
        else if (guest.cameraMode == 2)
        {
            position = eye + forward * guest.cameraDistance * (float)FieldFrame.CmPerBlock;
            yaw += 180f;
            pitch = -pitch;
        }
        var rotation = CameraRotation(yaw, pitch);
        _flow.FLD_CAMERA_SET_POS(position.X, position.Y, position.Z);
        _flow.FLD_CAMERA_SET_ROT(rotation.X, rotation.Y, rotation.Z, rotation.W);
        _cameraPos = position;
        _cameraYaw = yaw;
        _cameraPitch = pitch;
        if (_frame % 30 == 0)
            _fovY = _flow.FLD_CAMERA_GET_FOVY();
        var (cx, cy, cz) = _field.ToMc(position);
        View = new CameraView(cx, cy, cz, yaw, pitch, _fovY is > 1f and < 170f ? _fovY : 45f,
            feetMc.X, feetMc.Y, feetMc.Z, guest.cameraMode);
        return feet;
    }

    /// <summary>
    /// Camera orientation for a Minecraft yaw/pitch: pitch about the camera's X, then yaw about world Y.
    /// For a camera looking down -Z that is Ry(180 - yaw) * Rx(-pitch); for +Z, Ry(-yaw) * Rx(pitch).
    /// </summary>
    private Quaternion CameraRotation(float yaw, float pitch)
    {
        float y = yaw * MathF.PI / 180f, p = pitch * MathF.PI / 180f;
        return _camForwardMinusZ
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI - y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -p)
            : Quaternion.CreateFromAxisAngle(Vector3.UnitY, -y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, p);
    }

    // Like Minecraft's renderer: blend the last two 20 Hz physics ticks on our own clock, so the two
    // games' frames don't beat against each other. Falls back to the reported position when stale.
    // Doubles throughout: Minecraft coordinates here reach ~131k blocks.
    private ((double X, double Y, double Z) Feet, float Eye) Interpolate(in Proto.GuestState guest)
    {
        (double X, double Y, double Z) feet = (guest.x, guest.y, guest.z);
        float eye = guest.eyeHeight > 0.1f ? guest.eyeHeight : 1.62f;
        if (guest.tickQpc != 0 && guest.tickMs > 1f)
        {
            double ageMs = (HostLink.Qpc() - guest.tickQpc) * 1000.0 / _qpcFrequency;
            if (ageMs >= 0 && ageMs < 250)
            {
                double t = Math.Clamp(ageMs / guest.tickMs, 0.0, 1.0);
                double bx = guest.prevX + (guest.curX - guest.prevX) * t;
                double by = guest.prevY + (guest.curY - guest.prevY) * t;
                double bz = guest.prevZ + (guest.curZ - guest.prevZ) * t;
                double dx = bx - guest.x, dy = by - guest.y, dz = bz - guest.z;
                if (dx * dx + dy * dy + dz * dz < 4.0)
                {
                    feet = (bx, by, bz);
                    eye = guest.tickEyeO + (guest.tickEye - guest.tickEyeO) * (float)t;
                }
            }
        }
        return (feet, eye);
    }
}
