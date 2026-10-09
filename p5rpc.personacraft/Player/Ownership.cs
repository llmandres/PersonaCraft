using p5rpc.personacraft.Game;
using p5rpc.personacraft.Link;

namespace p5rpc.personacraft.Player;

/// <summary>Who owns Joker. Every module reads this one value.</summary>
internal enum Owner
{
    /// <summary>No link, not in a field, or no collision for this field: P5R runs as if the mod were not there.</summary>
    P5ROwns,

    /// <summary>Minecraft has been asked to teleport to Joker and has not confirmed. P5R's keyboard is already off.</summary>
    Handoff,

    /// <summary>Minecraft physics drives Joker; P5R's camera follows Minecraft's eye.</summary>
    MinecraftOwns,

    /// <summary>
    /// P5R took Joker inside a field: talking, a door, a menu, an event, a battle starting. P5R gets
    /// the keyboard, the body and the camera; Minecraft parks its player until Joker is free again.
    /// </summary>
    P5RBusy,
}

/// <summary>
/// The ownership state machine, adapted from PeakCraft's.
///
/// | State          | P5R keyboard | Joker moved by | Camera    | Overlay |
/// | P5ROwns        | yes          | P5R            | P5R       | no      |
/// | Handoff        | no           | P5R (idle)     | P5R       | no      |
/// | MinecraftOwns  | Tab/G only   | Minecraft      | Minecraft | yes     |
/// | P5RBusy        | yes          | P5R            | P5R       | no      |
///
/// P5R has no single "the player has control" flag that is known, so control is read from the field
/// player's state machine: while Minecraft owns Joker his keyboard is filtered out, so he stands idle
/// in one state. The state he settles in at the end of the first handoff is learned as "free"; any
/// other state means P5R is doing something with him (a talk started by G, a door, an event). A
/// field player update that stops arriving means a menu, loading or a battle.
/// </summary>
internal sealed class Ownership
{
    private const double StaleUpdateMs = 250;

    public Owner State { get; private set; } = Owner.P5ROwns;

    /// <summary>The teleport Minecraft must acknowledge before it gets the body.</summary>
    public uint TeleportSeq { get; private set; } = (uint)(HostLink.TickCount / 1000 % 1000000) * 64 + 2;

    /// <summary>Joker's state number while he is free (idle, no input), learned at the first takeover.</summary>
    public int FreeState { get; private set; } = -1;

    public string Reason { get; private set; } = "";

    public bool MinecraftHasInput => State == Owner.MinecraftOwns;
    public bool P5RKeyboardOff => State is Owner.Handoff or Owner.MinecraftOwns;
    public bool BodyFollows => State == Owner.MinecraftOwns;
    public bool Overlay => State == Owner.MinecraftOwns;

    public void Update(bool linkUp, bool takeOverAllowed, bool inField, bool collisionReady, FieldSnapshot field,
                       double snapshotAgeMs, in Proto.GuestState guest)
    {
        Owner next = Decide(linkUp, takeOverAllowed, inField, collisionReady, field, snapshotAgeMs, guest);
        if (next == State)
            return;
        if (next == Owner.Handoff && State is Owner.P5ROwns or Owner.P5RBusy)
            TeleportSeq++; // a fresh takeover starts from where Joker is now
        if (next == Owner.MinecraftOwns && FreeState < 0)
        {
            FreeState = field.PcState;
            Log.Info($"ownership: Joker's free state is {FreeState}");
        }
        Log.Info($"ownership: {State} -> {next} ({Reason}){(next == Owner.Handoff ? $" teleport {TeleportSeq}" : "")}");
        State = next;
    }

    private Owner Decide(bool linkUp, bool takeOverAllowed, bool inField, bool collisionReady, FieldSnapshot field,
                         double snapshotAgeMs, in Proto.GuestState guest)
    {
        if (!linkUp)
            return Because(Owner.P5ROwns, "no link to Minecraft");
        if (!takeOverAllowed)
            return Because(Owner.P5ROwns, "takeover off");
        if (!inField)
            return Because(State is Owner.MinecraftOwns or Owner.Handoff or Owner.P5RBusy ? Owner.P5RBusy : Owner.P5ROwns, "not in the field");
        if (!field.HasJoker || snapshotAgeMs > StaleUpdateMs)
            return Because(State == Owner.P5ROwns ? Owner.P5ROwns : Owner.P5RBusy, "field player paused (menu, loading, battle)");
        if (!collisionReady)
            return Because(State == Owner.P5ROwns ? Owner.P5ROwns : Owner.P5RBusy, "collision not ready for this field");
        if ((guest.flags & (uint)Proto.GuestFlags.InWorld) == 0)
            return Because(Owner.P5ROwns, "Minecraft not in its world");
        if ((guest.flags & (uint)Proto.GuestFlags.Dead) != 0)
            return Because(Owner.P5RBusy, "Minecraft player dead");

        bool free = FreeState < 0 || field.PcState == FreeState;
        switch (State)
        {
            case Owner.P5ROwns:
            case Owner.P5RBusy:
                return free ? Because(Owner.Handoff, "Joker is free") : Because(Owner.P5RBusy, $"Joker busy (state {field.PcState})");
            case Owner.Handoff:
                return guest.teleportAck == TeleportSeq ? Because(Owner.MinecraftOwns, "teleport acknowledged") : Owner.Handoff;
            default:
                if (!field.Following)
                    return Owner.MinecraftOwns; // the game thread starts following on its next update
                return free ? Owner.MinecraftOwns : Because(Owner.P5RBusy, $"P5R took Joker (state {field.PcState})");
        }
    }

    private Owner Because(Owner state, string reason)
    {
        Reason = reason;
        return state;
    }
}
