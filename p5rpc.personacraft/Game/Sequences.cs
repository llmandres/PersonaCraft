using p5rpc.lib.interfaces;
using static p5rpc.lib.interfaces.Sequence;

namespace p5rpc.personacraft.Game;

internal static class Sequences
{
    /// <summary>
    /// p5rpc.lib 1.1.0's sequence numbers are one higher than the current Steam build's: it reports
    /// booting (INIT_READ) as CALENDAR and loading a save (TITLE -> FIELD) as TITLE_RAPID -> BATTLE.
    /// Measured 2026-10-09; the game evidently gained a sequence before INIT_READ.
    /// </summary>
    public static SequenceType Real(SequenceType reported) =>
        reported <= SequenceType.TITLE ? reported : (SequenceType)((int)reported - 1);

    public static SequenceType Current(ISequencer sequencer) => Real(sequencer.GetSequenceInfo().CurrentSequence);
}
