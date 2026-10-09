using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using p5rpc.lib.interfaces;
using Reloaded.Mod.Interfaces;
using static p5rpc.lib.interfaces.Sequence;

namespace p5rpc.personacraft;

/// <summary>
/// Read-only probe: once a second, while the game is in the field, asks the game (through its own
/// flowscript getters) where Joker and the camera are, and appends that to a CSV.
///
/// It runs on its own thread. p5rpc.lib queues each getter onto the game's main loop and runs one per
/// frame, so a sample costs the game a handful of tiny function calls spread over ~8 frames. Nothing
/// here writes to game memory, scripts or saves.
/// </summary>
internal sealed class Probe
{
    private const int SampleIntervalMs = 1000;
    private const int PollIntervalMs = 50;
    private const int VkF8 = 0x77;
    private const string Tag = "[PersonaCraft]";

    private readonly IFlowCaller _flow;
    private readonly ISequencer _sequencer;
    private readonly ILogger _logger;
    private readonly string _csvPath;
    private readonly int _pid = Environment.ProcessId;
    private readonly Thread _thread;

    private volatile bool _running = true;
    private bool _enabled = true;
    private bool _f8WasDown;
    private string _lastField = "";
    private bool _reportedError;
    private bool _wasReady;
    private int _samples;

    public Probe(IP5RLib lib, ILogger logger, string csvPath)
    {
        _flow = lib.FlowCaller;
        _sequencer = lib.Sequencer;
        _logger = logger;
        _csvPath = csvPath;
        _thread = new Thread(Run) { IsBackground = true, Name = "PersonaCraft probe", Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        _sequencer.SequenceChanged += info => Log($"sequence {info.LastSequence} -> {info.CurrentSequence}");
        _thread.Start();
    }

    public void Stop() => _running = false;

    private void Run()
    {
        try
        {
            using var csv = new StreamWriter(new FileStream(_csvPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            if (csv.BaseStream.Length == 0)
                csv.WriteLine("time,field,pc_handle,pc_x,pc_y,pc_z,cam_x,cam_y,cam_z");
            Log($"probe on: samples once a second in the field, F8 toggles. Writing {_csvPath}");

            long nextSample = 0;
            while (_running)
            {
                Thread.Sleep(PollIntervalMs);
                PollToggleKey();
                if (!_enabled || Environment.TickCount64 < nextSample)
                    continue;
                nextSample = Environment.TickCount64 + SampleIntervalMs;
                if (!_flow.Ready())
                    continue;
                if (!_wasReady)
                {
                    _wasReady = true;
                    Log($"game functions ready, current sequence {_sequencer.GetSequenceInfo().CurrentSequence}");
                }
                if (!InField())
                    continue;

                try
                {
                    if (_samples == 0)
                        Log("in the field, taking the first sample");
                    var watch = Stopwatch.StartNew();
                    string? line = Sample();
                    if (line != null)
                    {
                        csv.WriteLine(line);
                        if (++_samples == 1 || _samples % 30 == 0)
                            Log($"sample {_samples} ({watch.ElapsedMilliseconds} ms): {line}");
                    }
                }
                catch (Exception e) when (!_reportedError)
                {
                    _reportedError = true;
                    Log($"sample failed (reported once): {e.Message}");
                }
                catch
                {
                    // Already reported once; keep going quietly.
                }
            }
        }
        catch (Exception e)
        {
            Log($"probe stopped: {e}");
        }
    }

    /// <summary>One sample, or null when the field changed underneath it (loading, battle, event start).</summary>
    private string? Sample()
    {
        string field = $"{_flow.FLD_GET_MAJOR():D3}_{_flow.FLD_GET_MINOR():D3}";
        if (field != _lastField)
        {
            _lastField = field;
            Log($"field {field}");
        }

        int pc = _flow.FLD_PC_GET_RESHND(0);
        if (pc < 0 || !InField())
            return null;

        float px = _flow.FLD_MODEL_GET_X_TRANSLATE(pc);
        float py = _flow.FLD_MODEL_GET_Y_TRANSLATE(pc);
        float pz = _flow.FLD_MODEL_GET_Z_TRANSLATE(pc);
        float cx = _flow.FLD_CAMERA_GET_X_POS();
        float cy = _flow.FLD_CAMERA_GET_Y_POS();
        float cz = _flow.FLD_CAMERA_GET_Z_POS();

        // A sample that straddles a sequence change can mix two scenes; drop it.
        if (!InField())
            return null;

        var ci = CultureInfo.InvariantCulture;
        return string.Join(',',
            DateTime.Now.ToString("HH:mm:ss", ci), field, pc.ToString(ci),
            px.ToString("F2", ci), py.ToString("F2", ci), pz.ToString("F2", ci),
            cx.ToString("F2", ci), cy.ToString("F2", ci), cz.ToString("F2", ci));
    }

    private bool InField() => _sequencer.GetSequenceInfo().CurrentSequence == SequenceType.FIELD;

    /// <summary>F8 flips sampling on and off, only while P5R is the focused window.</summary>
    private void PollToggleKey()
    {
        bool down = (GetAsyncKeyState(VkF8) & 0x8000) != 0 && IsGameFocused();
        if (down && !_f8WasDown)
        {
            _enabled = !_enabled;
            Log(_enabled ? "probe resumed (F8)" : "probe paused (F8)");
        }
        _f8WasDown = down;
    }

    private bool IsGameFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == _pid;
    }

    private void Log(string message) => _logger.WriteLine($"{Tag} {message}");

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
