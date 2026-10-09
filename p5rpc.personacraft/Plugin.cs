using p5rpc.inputhook.interfaces;
using p5rpc.lib.interfaces;
using p5rpc.personacraft.Game;
using p5rpc.personacraft.Input;
using p5rpc.personacraft.Link;
using p5rpc.personacraft.Player;
using p5rpc.personacraft.Render;
using p5rpc.personacraft.World;
using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using static p5rpc.lib.interfaces.Sequence;

namespace p5rpc.personacraft;

/// <summary>
/// PersonaCraft's host plugin: the P5R end of SkyCraft's Minecraft link (see PeakCraft's
/// docs/PORTING-GUIDE.md for the design). A logic thread runs the link, collision, ownership and
/// input; the game thread moves Joker and the camera (<see cref="FieldPlayer"/>); the render thread
/// draws the overlay (<see cref="Overlay"/>).
/// </summary>
internal sealed unsafe class Plugin
{
    private const int TickMs = 8;
    private const float MaxGroundGapCm = 40f;
    private const double PausedMs = 250; // as Ownership.StaleUpdateMs

    private readonly Settings _settings;
    private readonly ISequencer _sequencer;
    private readonly HostLink _link = new();
    private readonly FieldModels _models;
    private readonly CollisionExporter _collision;
    private readonly Ownership _ownership = new();
    private readonly InputBridge _input;
    private readonly FieldPlayer _player;
    private readonly Overlay _overlay;
    private readonly HashSet<FieldFrame> _unsupported = new();
    private Thread? _thread;
    private volatile bool _running = true;

    private Proto.GuestState _guest;
    private uint _guestPid;
    private bool _guestAlive;
    private Owner _lastOwner = Owner.P5ROwns;
    private long _nextSummary;
    private long _nextEsc;
    private uint _hostSeqFrames;
    private SequenceType _lastSequence = SequenceType.NONE;

    public Plugin(Settings settings, IP5RLib lib, IInputHook inputHook, IReloadedHooks hooks, IStartupScanner scanner)
    {
        _settings = settings;
        _sequencer = lib.Sequencer;
        _models = new FieldModels(Path.GetDirectoryName(Environment.ProcessPath)!);
        _collision = new CollisionExporter(_link);
        _input = new InputBridge(_link, settings);
        _player = new FieldPlayer(lib.FlowCaller, settings, _link.QpcFrequency);
        _overlay = new Overlay(_link, _player);

        if (!Proto.SelfCheck() || !Rings.SelfTest())
            throw new InvalidOperationException("protocol self-check failed; PersonaCraft stays off");
        if (!_link.Create(Proto.MappingName))
            throw new InvalidOperationException("shared memory could not be created; PersonaCraft stays off");

        _player.Hook(scanner, hooks);
        _input.HookP5R(inputHook);
        if (settings.Overlay)
            _overlay.Install(hooks);
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PersonaCraft link" };
        _thread.Start();
        Log.Info("link thread started; waiting for Minecraft (start it first and leave it on the title screen)");
    }

    public void Stop()
    {
        _running = false;
        _link.Close();
        _models.Dispose();
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.Error($"link tick failed: {e}");
                Thread.Sleep(1000);
            }
            Thread.Sleep(TickMs);
        }
    }

    private void Tick()
    {
        WindowHook.TryInstall();
        _link.Heartbeat();

        bool alive = _link.GuestAlive;
        if (alive != _guestAlive)
        {
            _guestAlive = alive;
            Log.Info(alive ? $"link up (Minecraft pid {_link.GuestPid})" : "link down: Minecraft is gone or not responding");
            if (!alive)
            {
                _link.ResetOverlay();
                _overlay.ClearBlocks();
            }
        }
        if (alive && _link.GuestPid != _guestPid)
        {
            // A new Minecraft: everything sent before must be sent again.
            _guestPid = _link.GuestPid;
            _collision.Clear($"new Minecraft, pid {_guestPid}");
            _link.ResetOverlay();
            _overlay.ClearBlocks();
        }
        if (alive && _link.ReadGuestState(out var guest))
            _guest = guest;
        while (_link.PopEvent(out var e))
        {
            if (e.type == Proto.EvPlayerDied)
                Log.Info("Minecraft's player died");
        }
        // Drained every frame from the first one: a full render ring stalls Minecraft's heartbeat.
        // The overlay drains it on the render thread (and draws the blocks); until it runs, drop them here.
        if (!_overlay.DrainsRender)
        {
            lock (_overlay.RenderRingLock)
                _link.DrainRender(8L << 20, static (_, _, _) => { });
        }
        // A screen left open before Minecraft's world loads (its first-run onboarding) keeps it on the
        // title screen with its window hidden: close it.
        if (alive && (_guest.flags & (uint)Proto.GuestFlags.InWorld) == 0 && (_guest.flags & (uint)Proto.GuestFlags.ScreenOpen) != 0
            && Environment.TickCount64 >= _nextEsc)
        {
            _nextEsc = Environment.TickCount64 + 2000;
            _link.PushInput(Proto.InputType.Key, Scancodes.Escape, 1);
            _link.PushInput(Proto.InputType.Key, Scancodes.Escape, 0);
            Log.Info("Minecraft has a screen open before its world; sent Esc");
        }

        var sequence = Sequences.Current(_sequencer);
        if (sequence != _lastSequence)
        {
            if (_settings.Verbose)
                Log.Info($"sequence {_lastSequence} -> {sequence}");
            _lastSequence = sequence;
        }
        bool inField = sequence == SequenceType.FIELD;
        var snap = _player.Snapshot;
        double ageMs = (HostLink.Qpc() - snap.Qpc) * 1000.0 / _link.QpcFrequency;

        // The field player stopped updating (door, loading, menu, battle) while Minecraft had Joker:
        // give P5R its camera back now, not when the update resumes.
        if (snap.Following && ageMs > PausedMs)
        {
            _player.ReleaseWhilePaused();
            snap = _player.Snapshot;
        }

        UpdateCollision(inField, snap);
        _collision.Pump();
        bool collisionReady = _collision.Complete && _collision.Field == snap.Field;

        _ownership.Update(alive, _settings.TakeOver && _player.Ready, inField, collisionReady, snap, ageMs, _guest);
        var owner = _ownership.State;
        if (owner == Owner.Handoff && _lastOwner != Owner.Handoff)
            _input.SetLook(snap.CameraYaw, snap.CameraPitch); // keep looking where P5R's camera did
        _lastOwner = owner;

        _input.Update(_ownership, _guest, _overlay.Width, _overlay.Height);
        _player.SetCommand(new FollowCommand(_ownership.BodyFollows, _guest, _input.Yaw, _input.Pitch, _settings.HideJokerInFirstPerson));

        _overlay.Visible = _settings.Overlay && alive && _ownership.Overlay;
        _overlay.ShowCursor = _input.ScreenOpen;
        (_overlay.CursorX, _overlay.CursorY) = _input.Cursor;

        PublishHostState(alive, inField, snap, owner);
        Summary(snap, sequence, ageMs);
    }

    /// <summary>Loads and sends the current field's collision, once its alignment with Joker checks out.</summary>
    private void UpdateCollision(bool inField, FieldSnapshot snap)
    {
        if (!inField || !snap.Field.IsValid || !snap.HasJoker || !_guestAlive)
            return;
        if (_collision.Field == snap.Field || _unsupported.Contains(snap.Field))
            return;
        if (!_models.TryGet(snap.Field, out var meshes))
            return; // loading on the worker thread
        if (meshes == null)
        {
            _unsupported.Add(snap.Field);
            Log.Warn($"field {snap.Field}: no collision, Minecraft stays parked here");
            return;
        }
        float? ground = CollisionExporter.GroundBelow(meshes, snap.Joker);
        if (ground is not { } g || MathF.Abs(snap.Joker.Y - g) > MaxGroundGapCm)
        {
            _unsupported.Add(snap.Field);
            Log.Warn($"field {snap.Field}: collision does not line up with Joker (Joker at {snap.Joker:F0} cm, ground below {(ground?.ToString("F0") ?? "none")}); Minecraft stays parked here");
            return;
        }
        Log.Info($"field {snap.Field}: collision lines up with Joker (gap {snap.Joker.Y - g:F1} cm)");
        _collision.Begin(snap.Field, meshes);
    }

    private void PublishHostState(bool alive, bool inField, FieldSnapshot snap, Owner owner)
    {
        var state = new Proto.HostState
        {
            worldId = snap.Field.WorldId,
            collisionEpoch = _collision.Epoch,
            teleportSeq = _ownership.TeleportSeq,
            viewportW = (uint)Math.Max(_overlay.Width, 0),
            viewportH = (uint)Math.Max(_overlay.Height, 0),
            gameHour = 12f,
        };
        if (inField && snap.HasJoker)
            state.flags |= (uint)Proto.HostFlags.InGame;
        // Minecraft parks its player (and drops held keys) whenever it does not drive Joker.
        if (owner is not (Owner.Handoff or Owner.MinecraftOwns))
            state.flags |= (uint)Proto.HostFlags.Loading;
        if (snap.HasJoker && snap.Field.IsValid)
        {
            var (x, y, z) = snap.Field.ToMc(snap.Joker);
            state.posX = x;
            state.posY = y;
            state.posZ = z;
        }
        bool minecraftLook = owner == Owner.MinecraftOwns;
        state.yaw = minecraftLook ? _input.Yaw : snap.CameraYaw;
        state.pitch = minecraftLook ? _input.Pitch : snap.CameraPitch;
        _link.WriteHostState(state);
        _hostSeqFrames++;
    }

    private void Summary(FieldSnapshot snap, SequenceType sequence, double ageMs)
    {
        long now = Environment.TickCount64;
        if (!_settings.Verbose || now < _nextSummary)
            return;
        _nextSummary = now + 5000;
        Log.Info($"summary: owner {_ownership.State}, link {(_guestAlive ? "up" : "down")}, seq {sequence}, field {snap.Field}, " +
                 $"Joker {(snap.HasJoker ? snap.Joker.ToString("F0") : "-")} state {snap.PcState} (free {_ownership.FreeState}), " +
                 $"update age {ageMs:F0} ms, following {snap.Following}, collision {_collision.Field}{(_collision.Complete ? " ready" : "")}, " +
                 $"MC {_guest.x:F2},{_guest.y:F2},{_guest.z:F2} ack {_guest.teleportAck}/{_ownership.TeleportSeq}, look {_input.Yaw:F0}/{_input.Pitch:F0}, " +
                 $"viewport {_overlay.Width}x{_overlay.Height}, block sections {_overlay.BlockSections}");
    }
}
