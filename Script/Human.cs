using Godot;

// What a real table tennis player's body can do, from published measurements. The NPC's Skill
// (0 = recreational, 1 = elite) picks its place in each range.
//
// Sources:
//  [B&W] Bankosz & Winiarski 2018, J Sports Sci Med 17:330 - elite female juniors, topspin
//        forehand/backhand: racket 13-15 m/s at contact (forehand), 12-13 (backhand); peak joint
//        speeds (deg/s, mean): trunk rotation 513-579 FH / 115-145 BH, pelvis 312-392 FH,
//        shoulder flexion 440-473 FH, shoulder internal rotation 248-462 FH / 779-955 BH,
//        elbow flexion 291-324 FH / 190-263 BH, wrist flexion 224-228 FH / 269-387 BH.
//        Forward swing to contact 0.12-0.15 s.
//  [I&K] Iino & Kojima 2008 (ISBS) - topspin forehand, advanced college men 19.4-19.6 m/s
//        racket speed at contact, intermediate 17.9-18.4.
//  [K]   Kovacs et al. 2019 (ITTF congress) - recreational players: forehand loop 8.6-9.7 m/s,
//        41-61 rev/s spin.
//  [Ki]  Kidokoro et al. 2022, Sports Biomechanics - skilled players' topspin 117 +- 29 rev/s.
//  [Y]   Yoshida et al. 2014 - 2009 World Championships serves 13.7-62.5 rev/s, men 46 +- 9,
//        women 39 +- 9.
//  [G]   Guinness World Records 2016 - fastest table tennis hit 116 km/h (32 m/s).
//  [Z]   Zhou et al. 2021, JoVE 172 - chasse step / cross step during the stroke, national
//        level players: posterior ground force 0.82-0.89 body weights, lateral 0.38-0.46,
//        vertical 1.7-1.9.
//  [C]   Castellar et al. 2019, Int J Perf Anal Sport - faster reaction and movement times go
//        with better play; the reaction values here are the usual human visual reaction.
//  [W]   Winter 2009, Biomechanics and Motor Control of Human Movement - body segment masses as
//        shares of body mass (hand 0.6%, forearm 1.6%, upper arm 2.8%...).
//  [I]   ITTF equipment rules - the 40+ ball weighs 2.7 g; a blade with two rubbers is about
//        170-190 g.
public static class Human
{
    public const float G = 9.81f;

    // Racket speed at contact (m/s) for a full forehand topspin drive [K], [B&W], [I&K].
    public static float DriveRacketSpeed(float skill) => Mathf.Lerp(9f, 19.5f, skill);
    // A smash swings harder still; the ball can leave at up to 32 m/s [G].
    public static float SmashRacketSpeed(float skill) => Mathf.Lerp(12f, 24f, skill);
    public const float FastestBall = 32f;

    // Peak joint turning speeds (rad/s), the measured means plus about one spread [B&W].
    public const float TrunkTurn = 11f;          // forehand 10.1 +- 2.7 rad/s
    public const float BackhandTrunkShare = 0.25f; // backhand trunk turn is about a quarter of the forehand's
    public const float ShoulderTurn = 16.7f;     // backhand internal rotation 955 deg/s
    public const float ElbowTurn = 7.5f;         // forehand flexion 324 + 106 deg/s
    public const float WristTurn = 12.5f;        // backhand flexion 387 deg/s, wide spread

    // The forward swing, end of backswing to contact [B&W].
    public const float ForwardSwingTime = 0.14f;
    // Taking the racket back once the ball is read: about 0.3 s, not hurried. A rushed player
    // starts the forward swing from a shorter backswing.
    public const float BackswingTime = 0.3f;

    // From the other player's contact to moving for the ball (s): a choice reaction (which side,
    // where), slower than a simple one [C], varying a little from ball to ball. In a rally the
    // player reads the opponent's stroke as it is made and anticipates the ball, and the skilled
    // do this much better: about 0.17 s for top players, 0.32 s for recreational ones. A serve is
    // disguised, so the receiver must wait for the ball itself: no anticipation.
    public static float ReactionTime(float skill, bool serve) =>
        (serve ? Mathf.Lerp(0.34f, 0.24f, skill) : Mathf.Lerp(0.32f, 0.17f, skill)) * (float)GD.RandRange(0.9, 1.15);
    // Caught out of position (still moving, or finishing its own stroke) it reacts later, by up
    // to this much, and reads the ball worse (OutOfPositionRead times as far off, at most).
    public const float OutOfPositionDelay = 0.08f;
    public const float OutOfPositionRead = 1f;

    // How far off the first read of the spin is, from the opponent's stroke (one spread): a share
    // of the spin plus a little (rad/s) even for a ball with none. A disguised serve is read
    // worse, and only from its flight.
    public static float SpinReadShare(float skill, bool serve) => serve ? Mathf.Lerp(0.6f, 0.3f, skill) : Mathf.Lerp(0.45f, 0.12f, skill);
    public static float SpinReadFloor(float skill) => Mathf.Lerp(40f, 10f, skill);
    // The flight and the bounce show the spin as the ball comes, but not all of it: this share of
    // the first misread is still there at contact, and sends the return wrong (into the net, long).
    public static float SpinReadLeft(float skill) => Mathf.Lerp(0.45f, 0.2f, skill);

    // How far off the first read of where the ball will be met is from its pace and direction
    // alone (m, one spread: across, height, depth) for an easy ball; misreading the spin adds to
    // it. A fast ball is read worse. Right by the commit time.
    public static Vector3 ReadSpread(float skill) =>
        new(Mathf.Lerp(0.1f, 0.03f, skill), Mathf.Lerp(0.05f, 0.015f, skill), Mathf.Lerp(0.14f, 0.04f, skill));
    // The last part before contact, when the stroke is committed and can no longer be steered.
    public const float CommitTime = 0.12f;

    // The biggest horizontal push the legs give the body during play, in body weights [Z]
    // (national level players about 0.85-0.9), as an acceleration of the body (m/s^2).
    public static float PushAcceleration(float skill) => Mathf.Lerp(0.7f, 1.0f, skill) * G;
    public const float TopSpeed = 3.5f;          // fastest the body crosses the floor (m/s)

    // Serve spin (rev/s) [Y].
    public static float ServeSpin(float skill) => Mathf.Lerp(35f, 60f, skill);

    // Masses (kg) [I], [W].
    public const float BallMass = 0.0027f;
    public const float RacketMass = 0.18f;
    public const float DefaultBodyMass = 70f;
    public const float HandShare = 0.006f;

    // What the ball meets in the ~1 ms it is pressed hardest: the racket and the hand gripping
    // it. The forearm and the rest of the body are joined through the wrist, too loosely to act
    // in that time; their part is to make the racket speed before the contact.
    // racketMass is the racket's own, blade and rubbers (RacketFace.RacketMass for equipped ones).
    public static float EffectiveRacketMass(float bodyMass, float racketMass = RacketMass) => racketMass + HandShare * bodyMass;

    // In the collision the ball gets M / (M + m) of what an endlessly heavy racket would give it
    // (M the effective racket mass, m the ball's): about 99.6% for a 70 kg player. The body's
    // weight changes the ball's speed by well under 1%; the racket speed is what counts.
    public static float ImpactShare(float bodyMass, float racketMass = RacketMass)
    {
        float m = EffectiveRacketMass(bodyMass, racketMass);
        return m / (m + BallMass);
    }
}
