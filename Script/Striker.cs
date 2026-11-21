using Godot;

// The racket face that meets the ball, as the contact physics (Ball.HitByRacket) sees it: how
// much of the closing speed the rubber, sponge and blade give back along the face's normal
// (Restitution), how hard the topsheet can grip before the ball slides (Friction, a Coulomb
// limit), how much the stretched topsheet springs back and adds to the spin (SpringBack, the
// "catapult"), and the whole racket's mass (kg). Standard is the game's own racket: an
// offensive blade with tensor rubbers, about 180 g.
public readonly record struct RacketFace(float Restitution, float Friction, float SpringBack, float RacketMass)
{
    public static readonly RacketFace Standard = new(Ball.RubberRestitution, Ball.RubberFriction, Ball.RubberSpringBack, Human.RacketMass);

    // Share of the contact slip the friction takes out of the ball's velocity (2/5 stops a thin
    // shell's slip; the spring-back pushes past that).
    public float SlipTransfer => 0.4f * Ball.RubberGrip * (1f + SpringBack);
}

// Who strikes the ball: the hitter's body mass (kg; the hand on the handle adds to what the ball
// meets) and the racket face it is struck with. A plain body mass converts to one with the
// standard racket, so code that knows only the mass works as before.
public readonly record struct Striker(float BodyMass, RacketFace Face)
{
    public static readonly Striker Default = new(Human.DefaultBodyMass, RacketFace.Standard);

    public static implicit operator Striker(float bodyMass) => new(bodyMass, RacketFace.Standard);

    // The share of an endlessly heavy racket's push the ball gets (Human.ImpactShare).
    public float ImpactShare => Human.ImpactShare(BodyMass, Face.RacketMass);
}
