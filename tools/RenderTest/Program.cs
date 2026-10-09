using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using p5rpc.personacraft.Game;
using p5rpc.personacraft.Link;
using p5rpc.personacraft.Render;
using p5rpc.personacraft.World;
using PersonaCraft.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// RenderTest <field GFS> <major> <minor> <jokerX> <jokerY> <jokerZ> <out.png>
// Plays P5R's side of the link, makes Minecraft place a few blocks, then draws Minecraft's blocks
// and HUD with the mod's own renderer into a PNG.
var ci = CultureInfo.InvariantCulture;
var meshes = GfsCollision.Read(File.ReadAllBytes(args[0]));
var field = new FieldFrame(int.Parse(args[1]), int.Parse(args[2]));
var joker = new Vector3(float.Parse(args[3], ci), float.Parse(args[4], ci), float.Parse(args[5], ci));
string output = args[6];
const int W = 1280, H = 720;
int avatarMessages = 0;

D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out ID3D11Device? baseDevice, out ID3D11DeviceContext? context).CheckError();
var device = baseDevice!.QueryInterface<ID3D11Device1>();
var target = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, W, H, 1, 1, BindFlags.RenderTarget));
var rtv = device.CreateRenderTargetView(target);
var blocks = new Blocks(device);

var link = new HostLink();
if (!Proto.SelfCheck() || !Rings.SelfTest() || !link.Create(Proto.MappingName))
    return 1;
var collision = new CollisionExporter(link);
uint teleport = (uint)(HostLink.TickCount / 1000 % 1000000) * 64 + 2;
bool teleported = false;
float pitch = 10f;
long ackAt = 0, nextEsc = 0;
int step = 0;
var overlay = new byte[W * H * 4];
bool overlayOk = false;
uint pid = 0;
long start = Environment.TickCount64;
while (Environment.TickCount64 - start < 120000)
{
    link.Heartbeat();
    Drain();
    bool alive = link.GuestAlive;
    link.ReadGuestState(out var g);
    if (alive && link.GuestPid != pid)
    {
        pid = link.GuestPid;
        collision.Begin(field, meshes);
        Console.WriteLine($"link up, Minecraft pid {pid}");
    }
    collision.Pump();
    if (collision.Complete && (g.flags & 1) != 0 && !teleported)
    {
        teleported = true;
        teleport++;
    }
    if (alive && (g.flags & 1) == 0 && (g.flags & 2) != 0 && Environment.TickCount64 > nextEsc)
    {
        nextEsc = Environment.TickCount64 + 2000;
        link.PushInput(Proto.InputType.Key, 41, 1);
        link.PushInput(Proto.InputType.Key, 41, 0);
    }
    var (x, y, z) = field.ToMc(joker);
    link.WriteHostState(new Proto.HostState
    {
        flags = 1u | (teleported ? 0u : 4u),
        worldId = field.WorldId,
        collisionEpoch = collision.Epoch,
        teleportSeq = teleport,
        posX = x, posY = y, posZ = z,
        yaw = 0, pitch = pitch,
        viewportW = W, viewportH = H, gameHour = 12,
    });
    long now = Environment.TickCount64;
    if (teleported && g.teleportAck == teleport && ackAt == 0)
    {
        ackAt = now;
        Console.WriteLine("teleported");
    }
    // Script: look down-forward and right-click a few times (place blocks), raising the look each time.
    if (ackAt != 0)
    {
        long t = now - ackAt;
        if (step == 0 && t > 2000)
        {
            pitch = 50f;
            step++;
            link.PushInput(Proto.InputType.Key, 34, 1); // hotbar slot 5: oak planks
            link.PushInput(Proto.InputType.Key, 34, 0);
        }
        if (step >= 1 && step <= 6 && t > 2000 + step * 700)
        {
            link.PushInput(Proto.InputType.MouseButton, 3, 1);
            link.PushInput(Proto.InputType.MouseButton, 3, 0);
            pitch = 50f - step * 5f;
            step++;
        }
        if (step == 7 && t > 9000)
        {
            pitch = 20f;
            step++;
            link.PushInput(Proto.InputType.Key, 62, 1); // F5: third person behind the player
            link.PushInput(Proto.InputType.Key, 62, 0);
        }
        if (step == 8 && t > 12000)
            break;
    }
    Thread.Sleep(16);
}
Drain();
link.ReadGuestState(out var gs);
Console.WriteLine($"guest eye {gs.eyeX:F2},{gs.eyeY:F2},{gs.eyeZ:F2} yaw {gs.yaw:F1} pitch {gs.pitch:F1}; block sections {blocks.Count}; HUD frame {(overlayOk ? "yes" : "no")}");

// Like the mod over P5R's picture: background, then blocks, then the HUD. A second image from 5
// blocks behind and 2 above, looking down, shows the shape of what was built.
// The mod's camera in F5 (mode 1): behind the eye by Minecraft's camera distance.
var look = Look.Forward(gs.yaw, gs.pitch);
double distance = gs.cameraMode == 1 ? gs.cameraDistance : 0;
Console.WriteLine($"camera mode {gs.cameraMode}, distance {gs.cameraDistance:F2}");
Render(new CameraView(gs.eyeX - look.X * distance, gs.eyeY - look.Y * distance, gs.eyeZ - look.Z * distance, gs.yaw, gs.pitch, 70f,
    gs.x, gs.y, gs.z, gs.cameraMode), output, true);
var back = Look.Forward(gs.yaw, 35f);
Render(new CameraView(gs.eyeX - back.X * 5, gs.eyeY + 2.5, gs.eyeZ - back.Z * 5, gs.yaw, 35f, 70f, gs.x, gs.y, gs.z, 1), output.Replace(".png", "_back.png"), false);
Console.WriteLine($"wrote {output}");

void Render(CameraView camera, string path, bool hud)
{
    context!.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0.15f, 0.15f, 0.2f, 1f));
    blocks.Draw(context, rtv, W, H, camera);
    using var staging = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, W, H, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    context.CopyResource(staging, target);
    var mapped = context.Map(staging, 0, MapMode.Read);
    var pixels = new byte[W * H * 4];
    unsafe
    {
        for (int row = 0; row < H; row++)
            new ReadOnlySpan<byte>((byte*)mapped.DataPointer + row * mapped.RowPitch, W * 4).CopyTo(pixels.AsSpan(row * W * 4));
    }
    context.Unmap(staging, 0);
    for (int i = 0; i < pixels.Length; i += 4)
    {
        int a = hud && overlayOk ? overlay[i + 3] : 0;
        for (int c = 0; c < 3; c++)
            pixels[i + c] = (byte)((overlay[i + c] * a + pixels[i + c] * (255 - a)) / 255);
        pixels[i + 3] = 255;
    }
    WritePng(path, pixels, W, H);
}
link.Close();
return 0;

void Drain()
{
    unsafe
    {
        link.DrainRender(16L << 20, (t, p, n) =>
        {
            if (t == Proto.RenAvatar)
                AnalyseAvatar(p, n);
            blocks.Handle(context!, t, p, n);
        });
        if (link.AcquireOverlayFrame())
        {
            var hdr = link.FrontHeader;
            if (hdr->width == W && hdr->height == H)
            {
                bool bottomUp = (hdr->flags & 1) != 0;
                for (int row = 0; row < H; row++)
                    new ReadOnlySpan<byte>(link.FrontPixels + (long)(bottomUp ? H - 1 - row : row) * W * 4, W * 4).CopyTo(overlay.AsSpan(row * W * 4));
                overlayOk = true;
            }
        }
    }
}

// Where does the skin's face (head front: u 8-16, v 8-16 of a 64x64 skin) sit relative to the head?
// Prints the face quads' mean position next to the whole head's, for the skin batch (texture 1).
unsafe void AnalyseAvatar(byte* payload, uint bytes)
{
    if (bytes < 8) return;
    var hdr = *(Proto.RenAvatarHdr*)payload;
    var batches = (Proto.RenBatch*)(payload + 8);
    var v = (Proto.RenVertex*)(batches + hdr.batchCount);
    if (++avatarMessages % 60 == 1)
    {
        for (int b = 0; b < hdr.batchCount; b++)
        {
            float lo = float.MaxValue, hi = float.MinValue, zl = float.MaxValue, zh = float.MinValue;
            for (uint i = batches[b].first; i < batches[b].first + batches[b].count; i++) { lo = Math.Min(lo, v[i].y); hi = Math.Max(hi, v[i].y); zl = Math.Min(zl, v[i].z); zh = Math.Max(zh, v[i].z); }
            Console.WriteLine($"avatar batch {b}: texture {batches[b].texture} flags {batches[b].flags} vertices {batches[b].count} y {lo:F2}..{hi:F2} z {zl:F2}..{zh:F2}");
        }
    }
    for (int b = 0; b < hdr.batchCount; b++)
    {
        if (batches[b].texture != 1) continue;
        Vector3 face = default, all = default; int nf = 0, na = 0;
        static bool In(float x, float lo, float hi) => x >= lo - 1e-4f && x <= hi + 1e-4f;
        for (uint i = batches[b].first; i + 2 < batches[b].first + batches[b].count; i += 3)
        {
            bool isFace = true, isHead = true;
            for (uint k = 0; k < 3; k++)
            {
                var q = v[i + k];
                isFace &= In(q.u, 8f / 64, 16f / 64) && In(q.v, 8f / 64, 16f / 64);
                isHead &= q.y > 1.45f;
            }
            if (!isHead) continue;
            if (avatarMessages % 60 == 1 && i - batches[b].first < 36 * 3)
                Console.WriteLine($"  head tri {i}: " + string.Join(" | ", Enumerable.Range(0, 3).Select(k => $"({v[i + (uint)k].x:F2},{v[i + (uint)k].y:F2},{v[i + (uint)k].z:F2}) uv {v[i + (uint)k].u:F4},{v[i + (uint)k].v:F4}")));
            for (uint k = 0; k < 3; k++)
            {
                var q = v[i + k];
                all += new Vector3(q.x, q.y, q.z); na++;
                if (isFace) { face += new Vector3(q.x, q.y, q.z); nf++; }
            }
        }
        if (nf > 0 && na > 0)
            Console.WriteLine($"avatar skin: head centre {all / na:F3}, face {face / nf:F3} ({nf} face vertices); face points {(face / nf - all / na).Z:+0.000;-0.000} in Z, {(face / nf - all / na).X:+0.000;-0.000} in X");
    }
}

static void WritePng(string path, byte[] rgba, int w, int h)
{
    using var fs = File.Create(path);
    fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    void Chunk(string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        fs.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(typed));
        fs.Write(crc);
    }
    var ihdr = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
    BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
    ihdr[8] = 8;
    ihdr[9] = 6;
    Chunk("IHDR", ihdr);
    using var raw = new MemoryStream();
    using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
    {
        for (int y = 0; y < h; y++)
        {
            z.WriteByte(0);
            z.Write(rgba, y * w * 4, w * 4);
        }
    }
    Chunk("IDAT", raw.ToArray());
    Chunk("IEND", []);
}
