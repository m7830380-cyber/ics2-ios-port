using LiteNetLib.Utils;

namespace ICS2Coop
{
    /// Flags in PlayerState.
    public static class PlayerFlags
    {
        public const byte Running = 1;
        public const byte Crouching = 2;
        public const byte AtComputer = 4;   // sitting at a PC (camera zoomed into the screen)
        public const byte Frozen = 8;       // in a menu / cursor mode
    }

    /// One player's body: sent by its owner ~20 times a second (unreliable, sequenced). The host forwards
    /// client states to the other clients. Position is the player's root (character controller) position.
    public struct PlayerState
    {
        public int Player;
        public float Time;            // sender's clock (seconds), for interpolation
        public float X, Y, Z;
        public float Yaw, Pitch;      // degrees
        public byte Flags;

        public void Write(NetDataWriter w)
        {
            w.Put(Player);
            w.Put(Time);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Yaw); w.Put(Pitch);
            w.Put(Flags);
        }

        public static PlayerState Read(NetDataReader r) => new PlayerState
        {
            Player = r.GetInt(),
            Time = r.GetFloat(),
            X = r.GetFloat(), Y = r.GetFloat(), Z = r.GetFloat(),
            Yaw = r.GetFloat(), Pitch = r.GetFloat(),
            Flags = r.GetByte(),
        };
    }
}
