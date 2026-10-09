using System.Globalization;
using System.Numerics;
using p5rpc.personacraft.Link;
using p5rpc.personacraft.World;
using PersonaCraft.Core;

// HostSim <field GFS> <major> <minor> <jokerX> <jokerY> <jokerZ> [seconds]
// Acts as P5R: creates the link, sends the field's collision, asks Minecraft to teleport to "Joker",
// then walks forward (W) for a while and prints where Minecraft's player is, in P5R centimetres.
var ci = CultureInfo.InvariantCulture;
var meshes = GfsCollision.Read(File.ReadAllBytes(args[0]));
var field = new FieldFrame(int.Parse(args[1]), int.Parse(args[2]));
var joker = new Vector3(float.Parse(args[3], ci), float.Parse(args[4], ci), float.Parse(args[5], ci));
int seconds = args.Length > 6 ? int.Parse(args[6]) : 60;
Console.WriteLine($"field {field}: {meshes.Count} meshes; Joker {joker}; ground below {CollisionExporter.GroundBelow(meshes, joker)}");

var link = new HostLink();
if (!Proto.SelfCheck() || !Rings.SelfTest() || !link.Create(Proto.MappingName)) return 1;
var collision = new CollisionExporter(link);
uint teleport = (uint)(HostLink.TickCount / 1000 % 1000000) * 64 + 2;
var start = DateTime.Now;
bool sent = false, teleported = false, walking = false;
long walkAt = 0, nextPrint = 0, nextEsc = 0;
uint pid = 0;
while ((DateTime.Now - start).TotalSeconds < seconds)
{
    link.Heartbeat();
    unsafe { link.DrainRender(8L << 20, static (_, _, _) => { }); }
    bool alive = link.GuestAlive;
    link.ReadGuestState(out var g);
    if (alive && link.GuestPid != pid) { pid = link.GuestPid; Console.WriteLine($"link up, Minecraft pid {pid}"); sent = false; }
    if (alive && !sent) { collision.Begin(field, meshes); sent = true; }
    collision.Pump();
    bool inWorld = (g.flags & 1) != 0;
    // A screen left open before the world loads (first-run onboarding) keeps Minecraft from entering it: Esc it.
    if (alive && !inWorld && (g.flags & 2) != 0 && Environment.TickCount64 >= nextEsc)
    {
        nextEsc = Environment.TickCount64 + 2000;
        link.PushInput(Proto.InputType.Key, 41, 1);
        link.PushInput(Proto.InputType.Key, 41, 0);
        Console.WriteLine("Esc sent (a Minecraft screen is open before the world)");
    }
    if (collision.Complete && inWorld && !teleported) { teleported = true; teleport++; Console.WriteLine($"teleport {teleport} requested"); }
    var (x, y, z) = field.ToMc(joker);
    var hs = new Proto.HostState
    {
        flags = (uint)Proto.HostFlags.InGame | (teleported ? 0u : (uint)Proto.HostFlags.Loading),
        worldId = field.WorldId, collisionEpoch = collision.Epoch, teleportSeq = teleport,
        posX = x, posY = y, posZ = z, yaw = 0, pitch = 10, viewportW = 1280, viewportH = 720, gameHour = 12,
    };
    link.WriteHostState(hs);
    long now = Environment.TickCount64;
    if (teleported && g.teleportAck == teleport && !walking && walkAt == 0) { walkAt = now + 3000; Console.WriteLine("teleport acknowledged; walking in 3 s"); }
    if (walkAt != 0 && now >= walkAt && !walking) { walking = true; link.PushInput(Proto.InputType.Key, 26, 1); Console.WriteLine("W down"); }
    if (walking && now >= walkAt + 4000) { link.PushInput(Proto.InputType.Key, 26, 0); walking = false; walkAt = long.MaxValue; Console.WriteLine("W up"); }
    if (now >= nextPrint && alive)
    {
        nextPrint = now + 500;
        var cm = field.ToGame(g.x, g.y, g.z);
        float? ground = CollisionExporter.GroundBelow(meshes, cm);
        Console.WriteLine($"MC flags 0x{g.flags:X} ack {g.teleportAck}/{teleport} -> P5R {cm.X:F0},{cm.Y:F1},{cm.Z:F0} cm; ground there {(ground?.ToString("F1", ci) ?? "-")}; collision {(collision.Complete ? "sent" : "sending")} backlog {link.CollisionBacklog}");
    }
    Thread.Sleep(16);
}
link.Close();
return 0;
