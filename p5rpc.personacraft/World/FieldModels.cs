using System.Collections.Concurrent;
using CriFsV2Lib;
using CriFsV2Lib.Definitions;
using CriFsV2Lib.Definitions.Structs;
using PersonaCraft.Core;

namespace p5rpc.personacraft.World;

/// <summary>
/// Loads a field's collision straight from the game's BASE.CPK (read-only): the "atari" meshes in
/// MODEL/FIELD_TEX/Fxxx_yyy_0.GFS. Indexing the CPK and parsing a model happen on a background thread;
/// <see cref="TryGet"/> never blocks the game.
/// </summary>
internal sealed class FieldModels : IDisposable
{
    private readonly string _cpkPath;
    private readonly ConcurrentDictionary<FieldFrame, List<CollisionMesh>?> _loaded = new();
    private readonly ConcurrentDictionary<FieldFrame, bool> _requested = new();
    private readonly BlockingCollection<FieldFrame> _queue = new();
    private readonly Thread _thread;
    private FileStream? _stream;
    private ICpkReader? _reader;
    private Dictionary<string, CpkFile>? _index;

    public FieldModels(string gameDirectory)
    {
        _cpkPath = Path.Combine(gameDirectory, "CPK", "BASE.CPK");
        _thread = new Thread(Run) { IsBackground = true, Name = "PersonaCraft field models", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The field's collision once loaded (null if the field has none), or false while it is still loading.</summary>
    public bool TryGet(FieldFrame field, out List<CollisionMesh>? meshes)
    {
        if (_loaded.TryGetValue(field, out meshes))
            return true;
        if (_requested.TryAdd(field, true))
            _queue.Add(field);
        return false;
    }

    private void Run()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _stream = new FileStream(_cpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = CriFsLib.Instance.CreateCpkReader(_stream, false, CriFsLib.Instance.GetKnownDecryptionFunction(KnownDecryptionFunction.P5R));
            _index = new Dictionary<string, CpkFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _reader.GetFiles())
            {
                if (file.Directory != null && file.Directory.EndsWith("FIELD_TEX", StringComparison.OrdinalIgnoreCase))
                    _index[file.FileName] = file;
            }
            Log.Info($"field models: indexed {_index.Count} field models in BASE.CPK in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            Log.Error($"field models: cannot read {_cpkPath}: {e.Message}. No collision, so Minecraft will not take over.");
            return;
        }

        foreach (var field in _queue.GetConsumingEnumerable())
        {
            _loaded[field] = Load(field);
        }
    }

    private List<CollisionMesh>? Load(FieldFrame field)
    {
        string name = $"F{field.Major:D3}_{field.Minor:D3}_0.GFS";
        if (!_index!.TryGetValue(name, out var file))
        {
            Log.Warn($"field models: {name} not in BASE.CPK; field {field} has no collision");
            return null;
        }
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var data = _reader!.ExtractFile(file);
            var meshes = GfsCollision.Read(data.Span.ToArray());
            int triangles = meshes.Sum(m => m.Indices.Length / 3);
            Log.Info($"field models: {name}: {meshes.Count} collision meshes, {triangles} triangles ({watch.ElapsedMilliseconds} ms)");
            return meshes.Count > 0 ? meshes : null;
        }
        catch (Exception e)
        {
            Log.Error($"field models: {name}: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _stream?.Dispose();
    }
}
