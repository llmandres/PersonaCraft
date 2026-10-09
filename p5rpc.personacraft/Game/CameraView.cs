namespace p5rpc.personacraft.Game;

/// <summary>The camera this frame in Minecraft space, for drawing blocks: eye, look, P5R's vertical FOV (degrees).</summary>
internal sealed record CameraView(double X, double Y, double Z, float Yaw, float Pitch, float FovY);
