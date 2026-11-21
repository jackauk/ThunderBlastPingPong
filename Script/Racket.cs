using Godot;
using System.Collections.Generic;
using System.Linq;

// Rackets from the collection (RacketCatalog): a blade with a rubber on each side, and what they
// make of the contact (RacketFace) on each side.
//
// Each item carries its real brand and model with the maker's published specs (reference only:
// the real names are trademarks, not for shipping without a licence) and an invented brand and
// model for the game to show.
//
// From specs to physics. The makers' ratings do not compare across brands, so the contact values
// come from what the specs describe:
//  - restitution (how fast the ball comes off): 0.74 + 0.07 x blade speed + 0.08 x rubber speed,
//    so an offensive blade with a 2.0 mm tensor rubber gives the standard 0.85, a defensive blade
//    with a pimpled rubber about 0.78;
//    - blade speed 0..1 from the maker's class (DEF .. OFF+), nudged by thickness and by a
//      composite layer just under the outer ply;
//    - rubber speed 0..1 from its kind (tensor fastest, then short pips, classic, Chinese tacky,
//      medium and long pips, anti), its sponge (harder is faster for tensor and tacky rubbers;
//      thicker is faster; no sponge, OX, is slow);
//  - friction (the most grip before the ball slides): tacky Chinese topsheets most (1.4), tensor
//    1.2 (the standard), classic 1.1, short pips 0.8, medium 0.55, long pips 0.25, anti 0.1. With
//    little friction the ball keeps its own spin, which is how long pips "reverse" it;
//  - spring-back (the catapult that adds spin): tensor rubbers most, more with a softer sponge;
//    tacky and classic rubbers less, pips and anti almost none;
//  - mass: the blade plus both rubber sheets cut to it.
public enum RubberKind { InvertedTensor, InvertedTacky, InvertedClassic, ShortPips, MediumPips, LongPips, Anti }

public class BladeSpec
{
    public string Id { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
    public string Country { get; set; }
    public int? Plies { get; set; }
    public string Construction { get; set; }
    public string Composite { get; set; }
    public string CompositeLayer { get; set; }  // "outer" | "inner" | null
    public string OuterPly { get; set; }
    public float? ThicknessMm { get; set; }
    public float? WeightG { get; set; }
    public string SpeedClass { get; set; }      // DEF, ALL-, ALL, ALL+, OFF-, OFF, OFF+
    public List<string> Handles { get; set; } = new();
    public string Source { get; set; }
    public string FictionalBrand { get; set; }
    public string FictionalModel { get; set; }

    public bool PenholdOnly => Handles.Count > 0 && Handles.All(h => h is "CPEN" or "JPEN");
    public string Name(bool fictional) => fictional ? $"{FictionalBrand} {FictionalModel}" : $"{Brand} {Model}";

    // 0 (slowest defensive) .. 1 (fastest offensive).
    public float Speed
    {
        get
        {
            float s = SpeedClass switch
            {
                "DEF" => 0.05f, "DEF+" => 0.12f, "ALL-" => 0.2f, "ALL" => 0.3f, "ALL+" => 0.4f,
                "OFF-" => 0.5f, "OFF" => 0.65f, "OFF+" => 0.82f,
                _ => 0.5f + (Composite != null ? 0.15f : 0f) + ((Plies ?? 5) >= 7 ? 0.05f : 0f),
            };
            if (ThicknessMm is float t)
                s += Mathf.Clamp((t - 5.8f) * 0.06f, -0.1f, 0.1f);
            if (Composite != null && CompositeLayer == "outer")
                s += 0.03f;
            return Mathf.Clamp(s, 0f, 1f);
        }
    }

    public float Mass => (WeightG ?? 85f) / 1000f;
}

public class RubberSpec
{
    public string Id { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
    public string Type { get; set; }            // inverted-tensor | inverted-tacky | inverted-classic | short-pips | medium-pips | long-pips | anti
    public float? Hardness { get; set; }
    public string HardnessScale { get; set; }   // ESN | JP | CN | other
    public List<float> ThicknessesMm { get; set; } = new();
    public float? WeightG { get; set; }
    public bool WeightIsCut { get; set; }
    public List<string> Colors { get; set; } = new();
    public string Source { get; set; }
    public string FictionalBrand { get; set; }
    public string FictionalModel { get; set; }

    public string Name(bool fictional) => fictional ? $"{FictionalBrand} {FictionalModel}" : $"{Brand} {Model}";

    public RubberKind Kind => Type switch
    {
        "inverted-tacky" => RubberKind.InvertedTacky,
        "inverted-classic" => RubberKind.InvertedClassic,
        "short-pips" => RubberKind.ShortPips,
        "medium-pips" => RubberKind.MediumPips,
        "long-pips" => RubberKind.LongPips,
        "anti" => RubberKind.Anti,
        _ => RubberKind.InvertedTensor,
    };

    public bool Pimpled => Kind is RubberKind.ShortPips or RubberKind.MediumPips or RubberKind.LongPips;

    // The sponge's hardness on the German (ESN) scale: Japanese degrees read about 11.5 lower,
    // Chinese about 11 lower. Unlisted: what is usual for the kind.
    public float EsnHardness => Hardness is float h
        ? h + HardnessScale switch { "JP" => 11.5f, "CN" => 11f, _ => 0f }
        : Kind switch
        {
            RubberKind.InvertedTacky => 50f, RubberKind.InvertedClassic => 45f, RubberKind.ShortPips => 45f,
            RubberKind.MediumPips => 42f, RubberKind.LongPips => 40f, RubberKind.Anti => 38f, _ => 47.5f,
        };

    // The thickness used when none is chosen: the thickest offered (what fast players glue), up
    // to 2.1 mm; for long pips and anti the thinnest.
    public float DefaultThickness =>
        ThicknessesMm.Count == 0 ? (Kind is RubberKind.LongPips or RubberKind.Anti ? 1.0f : 2.0f)
        : Kind is RubberKind.LongPips or RubberKind.Anti ? ThicknessesMm.Min()
        : ThicknessesMm.Where(t => t <= 2.15f).DefaultIfEmpty(ThicknessesMm.Min()).Max();

    // 0 (dead) .. 1 (fastest) at this sponge thickness (mm, 0 = OX).
    public float Speed(float thickness)
    {
        float hard = EsnHardness;
        float s = Kind switch
        {
            RubberKind.InvertedTensor => 0.75f + (hard - 47.5f) * 0.008f,
            RubberKind.InvertedTacky => 0.45f + (hard - 50f) * 0.012f,
            RubberKind.InvertedClassic => 0.55f + (hard - 45f) * 0.006f,
            RubberKind.ShortPips => 0.62f,
            RubberKind.MediumPips => 0.5f,
            RubberKind.LongPips => 0.3f,
            _ => 0.28f,
        };
        s += thickness <= 0f ? -0.3f : (thickness - 2.0f) * 0.15f;
        return Mathf.Clamp(s, 0f, 1f);
    }

    public float Friction => Kind switch
    {
        RubberKind.InvertedTacky => 1.4f,
        RubberKind.InvertedClassic => 1.1f,
        RubberKind.ShortPips => 0.8f,
        RubberKind.MediumPips => 0.55f,
        RubberKind.LongPips => 0.25f,
        RubberKind.Anti => 0.1f,
        _ => Ball.RubberFriction,
    };

    public float SpringBack => Kind switch
    {
        RubberKind.InvertedTensor => Mathf.Clamp(Ball.RubberSpringBack + (47.5f - EsnHardness) * 0.01f, 0.2f, 0.4f),
        RubberKind.InvertedClassic => 0.22f,
        RubberKind.InvertedTacky => Mathf.Clamp(0.15f + (EsnHardness - 50f) * 0.005f, 0.1f, 0.22f),
        RubberKind.ShortPips => 0.12f,
        RubberKind.MediumPips => 0.06f,
        _ => 0f,
    };

    // A sheet cut to a blade (kg). A listed weight is for the thickest sponge; an uncut sheet
    // loses about 28% when cut. Unlisted: usual weights for the kind.
    public float Mass(float thickness)
    {
        float Usual(float t) => Kind switch
        {
            RubberKind.ShortPips or RubberKind.MediumPips or RubberKind.LongPips => 14f + 12f * t,
            RubberKind.Anti => 14f + 13f * t,
            _ => 18f + 14f * t,
        };
        if (WeightG is float w)
        {
            float listed = ThicknessesMm.Count > 0 ? ThicknessesMm.Max() : thickness;
            float cut = WeightIsCut ? w : w * 0.72f;
            return cut * Usual(thickness) / Usual(listed) / 1000f;
        }
        return Usual(thickness) / 1000f;
    }
}

// What is equipped (saved in Equipment): ids into the catalogue, sponge thickness and topsheet
// colour for each side, and the handle.
public class RacketSetup
{
    public string Blade { get; set; }
    public string Forehand { get; set; }
    public float? ForehandMm { get; set; }
    public string ForehandColor { get; set; } = "red";
    public string Backhand { get; set; }
    public float? BackhandMm { get; set; }
    public string BackhandColor { get; set; } = "black";
    public string Handle { get; set; }
}

// An equipped racket, put together from the catalogue.
public class Racket
{
    public BladeSpec Blade { get; }
    public RubberSpec Forehand { get; }
    public RubberSpec Backhand { get; }
    public float ForehandMm { get; }
    public float BackhandMm { get; }
    public string ForehandColor { get; }
    public string BackhandColor { get; }
    public string Handle { get; }

    public Racket(BladeSpec blade, RubberSpec forehand, float? forehandMm, string forehandColor,
                  RubberSpec backhand, float? backhandMm, string backhandColor, string handle)
    {
        Blade = blade;
        Forehand = forehand;
        Backhand = backhand;
        ForehandMm = forehandMm ?? forehand.DefaultThickness;
        BackhandMm = backhandMm ?? backhand.DefaultThickness;
        ForehandColor = forehandColor ?? "red";
        BackhandColor = backhandColor ?? "black";
        Handle = handle ?? blade.Handles.FirstOrDefault() ?? "FL";
    }

    public bool Penhold => Handle is "CPEN" or "JPEN";

    // Blade and both sheets (kg).
    public float Mass => Blade.Mass + Forehand.Mass(ForehandMm) + Backhand.Mass(BackhandMm);

    // The face that meets the ball: the forehand rubber's side or the backhand's.
    public RacketFace Face(bool forehandSide)
    {
        RubberSpec rubber = forehandSide ? Forehand : Backhand;
        float mm = forehandSide ? ForehandMm : BackhandMm;
        float restitution = Mathf.Clamp(0.74f + 0.07f * Blade.Speed + 0.08f * rubber.Speed(mm), 0.6f, 0.92f);
        return new RacketFace(restitution, rubber.Friction, rubber.SpringBack, Mass);
    }

    // The ITTF rule since 2021: one side black, the other a bright colour (red, blue, green,
    // pink or violet). Null when the colours are legal, else why not.
    public string ColourRuleBroken()
    {
        bool fhBlack = ForehandColor == "black", bhBlack = BackhandColor == "black";
        if (fhBlack == bhBlack)
            return fhBlack ? "both sides are black: one must be a bright colour" : "neither side is black: one must be black";
        return null;
    }

    public string Name(bool fictional) =>
        $"{Blade.Name(fictional)} | {Forehand.Name(fictional)} / {Backhand.Name(fictional)}";

    public static Color Colour(string name) => name switch
    {
        "black" => new Color(0.035f, 0.035f, 0.035f),
        "blue" => new Color(0.06f, 0.22f, 0.75f),
        "green" => new Color(0.08f, 0.5f, 0.2f),
        "pink" => new Color(0.95f, 0.35f, 0.62f),
        "violet" => new Color(0.42f, 0.18f, 0.7f),
        _ => new Color(0.72f, 0.04f, 0.04f), // red
    };
}
