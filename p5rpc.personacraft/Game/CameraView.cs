namespace p5rpc.personacraft.Game;

/// <summary>
/// The camera this frame in Minecraft space, for drawing blocks: eye, look, P5R's vertical FOV
/// (degrees). Also the player's interpolated feet and Minecraft's camera mode (0 first person, 1/2 F5),
/// for drawing the player's model in third person.
/// </summary>
internal sealed record CameraView(double X, double Y, double Z, float Yaw, float Pitch, float FovY,
    double FeetX = 0, double FeetY = 0, double FeetZ = 0, uint CameraMode = 0);
