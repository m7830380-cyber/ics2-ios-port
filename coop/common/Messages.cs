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

    /// What the body is doing besides walking (PlayerState.Pose); the seat is in PlayerState.Seat*.
    public static class PlayerPoses
    {
        public const byte Stand = 0;
        public const byte Sit = 1;          // at a PC, on the strip club couch, at the blackjack table
        public const byte Sleep = 2;        // in bed
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
        public byte Pose;             // PlayerPoses
        public float SeatX, SeatY, SeatZ, SeatYaw;
        public byte Punches;          // counts up with every punch (the receiver plays one per step)
        public byte Lift;             // standing in elevator car Lift (NoLift: none); LiftY = height above the car
        public float LiftY;

        public const byte NoLift = 255;

        public void Write(NetDataWriter w)
        {
            w.Put(Player);
            w.Put(Time);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Yaw); w.Put(Pitch);
            w.Put(Flags);
            w.Put(Pose);
            if (Pose != PlayerPoses.Stand) { w.Put(SeatX); w.Put(SeatY); w.Put(SeatZ); w.Put(SeatYaw); }
            w.Put(Punches);
            w.Put(Lift);
            if (Lift != NoLift) w.Put(LiftY);
        }

        public static PlayerState Read(NetDataReader r)
        {
            var s = new PlayerState
            {
                Player = r.GetInt(),
                Time = r.GetFloat(),
                X = r.GetFloat(), Y = r.GetFloat(), Z = r.GetFloat(),
                Yaw = r.GetFloat(), Pitch = r.GetFloat(),
                Flags = r.GetByte(),
                Pose = r.GetByte(),
            };
            if (s.Pose != PlayerPoses.Stand) { s.SeatX = r.GetFloat(); s.SeatY = r.GetFloat(); s.SeatZ = r.GetFloat(); s.SeatYaw = r.GetFloat(); }
            s.Punches = r.GetByte();
            s.Lift = r.GetByte();
            if (s.Lift != NoLift) s.LiftY = r.GetFloat();
            return s;
        }
    }
}
