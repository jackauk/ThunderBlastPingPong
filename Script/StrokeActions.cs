using Godot;
using System.Collections.Generic;
using System.Linq;

// The kinds of action a player makes, each with its own motion (made in the Pose Lab and saved as
// res://Poses/<Id>.json) and a starting motion to shape it from. Names follow the usual English
// terms with the Vietnamese ones (phobongban.vn's glossary); the notes are the standard coaching
// points for each stroke.
//
// Positions in a template are relative to the player's body (x across, + to its left; y height;
// z towards the table), in the NPC's own frame (see PoseLab). Racket angles are the Pose Lab's:
// face open (+) / closed (-), wrist nose forward (+) / back, hand turned (+ to the left).
//
// Sidespin: "left" is a ball that will jump off the racket to the player's left (and "right" to
// its right); the face is turned the other way to send it straight.
public static class StrokeActions
{
    public record Action(string Id, string Name, string Vietnamese, string Group, string Notes, Template Template);

    // A motion: where the body stands, its stance, and the racket's steps. A footwork motion moves
    // the body From the ready place to Body (its steps say how far along, see Place).
    public record Template(bool Backhand, Vector2 Body, Stance Stance, float HipsHeight, float Lean, Key[] Steps, bool Serve = false,
                           Vector2? From = null);

    public enum Stance { Square, Forehand, Backhand, InTable, InTableForehand }

    // One step: the racket's place relative to the body, its angles, and how the body has turned.
    // A footwork step also says where the body and feet are (Where) and where the ball is (Ball,
    // in the NPC's own frame).
    public record Key(string Name, float Time, Vector3 Racket, float Open, float NoseForward = 0f, float Turn = 0f,
                      float HipsTurn = 0f, float WaistTurn = 0f, float NoseUp = 0f, Place Where = null, Vector3? Ball = null);

    // Where a footwork step has the body: Along the way from the ready place (0) to the template's
    // place (1), each foot along its own way (from where it stood to its place in the template's
    // stance there) and lifted off the floor by so much; Spread puts the feet that much wider each.
    // Carry: how much the racket, free hand and turns are held as in the first step (the ready
    // stance, moved along with the body; 1) rather than as the step says (0).
    public record Place(float Along, float HipsHeight, float Lean, float LeftAlong, float RightAlong,
                        float LeftLift = 0f, float RightLift = 0f, float Spread = 0f, float Carry = 0f);

    static Key K(string name, float time, float x, float y, float z, float open, float noseForward = 0f, float turn = 0f,
                 float hipsTurn = 0f, float waistTurn = 0f, float noseUp = 0f) => new(name, time, new Vector3(x, y, z), open, noseForward, turn, hipsTurn, waistTurn, noseUp);

    // Moving to the ball (footwork). The opponent strikes while the player stands ready; a split
    // step (a small hop onto the balls of the feet) as the ball leaves the racket; the first step
    // as the ball comes over the net: the foot on the side the ball is goes first (Lead: 0 the
    // left foot, 1 the right, -1 both pushing together, for a ball that sends the player back); in
    // place as the ball bounces; then the racket goes back for the stroke (set: the stroke's
    // backswing), the ball nearly at the hitting point. ball: its five places, one a step.
    const int BothFeet = -1;
    static Key[] MoveToBall(float hips, float lean, int lead, Vector3[] ball, Vector3 carry, float carryOpen, Key set, float arriveTime = 0.18f)
    {
        float midHips = (0.8f + hips) * 0.5f - 0.01f, midLean = (22f + lean) * 0.5f;
        // The racket is carried as in the ready stance while moving, half way to the backswing on
        // arriving, and fully back at the end.
        Place first = lead == BothFeet
            ? new Place(0.45f, midHips, midLean, 0.45f, 0.45f, 0.025f, 0.025f, Carry: 1f)
            : new Place(0.45f, midHips, midLean, lead == 0 ? 0.8f : 0.1f, lead == 1 ? 0.8f : 0.1f,
                        lead == 0 ? 0.035f : 0f, lead == 1 ? 0.035f : 0f, Carry: 1f);
        Key Step(string name, float time, Place where, Vector3 at, Vector3 racket, float open, Key turns = null) =>
            new(name, time, racket, open, turns?.NoseForward ?? 0f, turns?.Turn ?? 0f, turns?.HipsTurn ?? 0f, turns?.WaistTurn ?? 0f, 0f, where, at);
        return new[]
        {
            Step("ready", 0f, new Place(0f, 0.8f, 22f, 0f, 0f), ball[0], carry, carryOpen),
            Step("split step", 0.15f, new Place(0f, 0.82f, 20f, 0f, 0f, 0.03f, 0.03f, 0.03f, Carry: 1f), ball[1], carry, carryOpen),
            Step("first step", 0.15f, first, ball[2], carry, carryOpen),
            Step("in place", arriveTime, new Place(1f, hips, lean, 1f, 1f, Carry: 0.5f), ball[3], set.Racket, set.Open, set),
            Step("set, racket back", 0.15f, new Place(1f, hips, lean, 1f, 1f), ball[4], set.Racket, set.Open, set),
        };
    }
    static readonly Vector2 ReadyPlace = new(0.1f, -1.85f);
    static Vector3 B(float x, float y, float z) => new(x, y, z);

    // Forehand topspin family: from the right, the racket back and low, forward and up through the
    // ball, finishing high and across to the left; the hips and waist uncoil.
    static Key[] ForehandTopspin(float backY, float contactY, float finishY, float closed, float turnAtContact = 0f, float contactX = -0.35f) =>
    new[]
    {
        K("ready", 0f, -0.3f, 1.0f, 0.35f, -10f, hipsTurn: -10f),
        K("backswing", 0.3f, -0.5f, backY, -0.02f, closed + 15f, -20f, -30f, -35f, -20f),
        K("contact", 0.14f, contactX, contactY, 0.4f, closed, 0f, turnAtContact, -10f, 0f),
        // Forward and across, the elbow not folded shut: the higher the finish, the further forward.
        K("finish", 0.16f, 0.08f, finishY - 0.02f, 0.58f + Mathf.Max(finishY - 1.3f, 0f) * 1.2f, closed - 5f, 10f, 50f, 15f, 25f),
    };

    // Backhand topspin family: the racket in front of the body, elbow forward, from by the left
    // hip forward and up, finishing out to the right.
    static Key[] BackhandTopspin(float turnAtContact = 0f) =>
    new[]
    {
        K("ready", 0f, 0.05f, 1.0f, 0.35f, -10f),
        K("backswing", 0.25f, 0.14f, 0.9f, 0.3f, -15f, -15f, 15f, 5f, 10f),
        K("contact", 0.12f, 0.05f, 1.02f, 0.45f, -30f, 0f, turnAtContact, 0f, 0f),
        K("finish", 0.14f, -0.2f, 1.2f, 0.55f, -35f, 15f, -30f, -5f, -15f),
    };

    // A push over the table against backspin: short, the face open, forward and a little down
    // under the ball at the top of its bounce.
    static Key[] Push(bool backhand, float turnAtContact) =>
    new[]
    {
        K("ready", 0f, backhand ? 0.02f : -0.25f, 0.95f, 0.4f, 30f),
        K("backswing", 0.2f, backhand ? 0.07f : -0.28f, 0.97f, 0.48f, 45f),
        K("contact", 0.14f, backhand ? 0.02f : -0.22f, 0.9f, 0.58f, 55f, 0f, turnAtContact),
        K("finish", 0.12f, backhand ? 0f : -0.18f, 0.87f, 0.66f, 55f, 0f, turnAtContact),
    };

    // A chop block against topspin: the ball taken just after its bounce, the open face moving
    // down and forward under it, returning short backspin.
    static Key[] ChopBlock(bool backhand) =>
    new[]
    {
        K("ready", 0f, backhand ? 0.05f : -0.33f, 1.12f, 0.35f, 15f),
        K("set", 0.2f, backhand ? 0.05f : -0.35f, 1.15f, 0.35f, 20f),
        K("contact", 0.1f, backhand ? 0.05f : -0.3f, 1.0f, 0.5f, 30f),
        K("finish", 0.12f, backhand ? 0.05f : -0.27f, 0.86f, 0.55f, 35f),
    };

    // A block against topspin close to the table: the closed face set early in front of the body,
    // the ball met just after its bounce and sent back with its own pace, the racket going only a
    // little forward.
    static Key[] Block(bool backhand) =>
    new[]
    {
        K("ready", 0f, backhand ? 0.05f : -0.3f, 1.0f, 0.35f, -10f, hipsTurn: backhand ? 0f : -5f),
        K("set", 0.18f, backhand ? 0.07f : -0.33f, 1.04f, 0.36f, -25f, 0f, 0f, backhand ? 0f : -10f, backhand ? 0f : -5f),
        K("contact", 0.1f, backhand ? 0.04f : -0.3f, 1.05f, 0.48f, -35f),
        K("finish", 0.1f, backhand ? 0f : -0.25f, 1.07f, 0.56f, -35f, 5f, 0f, backhand ? 0f : 5f, backhand ? 0f : 5f),
    };

    // A chop: the defender's stroke, away from the table against topspin. The racket goes up
    // high on the side of the stroke with the face open, then cuts down and forward under the
    // back of the falling ball (taken late, below table height), finishing low in front of the
    // body; the waist turns through it. It returns heavy backspin.
    static Key[] Chop(bool backhand) => backhand
        ? new[]
        {
            K("ready", 0f, 0.05f, 1.0f, 0.35f, 15f),
            K("racket up", 0.35f, 0.5f, 1.25f, 0.26f, 50f, 0f, 0f, 20f, 25f),
            K("contact", 0.16f, 0.28f, 0.82f, 0.32f, 65f, 30f, 0f, 5f, 5f, noseUp: -10f),
            K("finish", 0.16f, 0.02f, 0.62f, 0.5f, 65f, 30f, -15f, -10f, -15f, noseUp: 10f),
        }
        : new[]
        {
            // The racket head stays above the hand through the stroke (nose up), the wrist easy.
            K("ready", 0f, -0.3f, 1.0f, 0.35f, 15f, hipsTurn: -10f),
            K("racket up", 0.35f, -0.55f, 1.35f, 0.15f, 50f, 0f, 0f, -25f, -30f),
            K("contact", 0.16f, -0.4f, 0.82f, 0.32f, 65f, 20f, 0f, -10f, -5f, noseUp: 30f),
            K("finish", 0.16f, -0.12f, 0.62f, 0.52f, 65f, 30f, 20f, 10f, 15f, noseUp: 30f),
        };

    // A flick over the table against a short ball: low over the table, the wrist cocked back, then
    // snapped forward and up through the ball.
    static Key[] Flick(bool backhand) =>
    new[]
    {
        K("ready", 0f, backhand ? 0.02f : -0.25f, 0.92f, 0.4f, 0f),
        K("wrist back", 0.25f, backhand ? 0.05f : -0.27f, 0.9f, 0.42f, -5f, -40f),
        K("contact", 0.1f, backhand ? 0.02f : -0.2f, 0.95f, 0.55f, -15f, 0f),
        K("finish", 0.12f, backhand ? -0.05f : -0.1f, 1.08f, 0.62f, -20f, 30f, backhand ? -20f : 25f),
    };

    // The ready stance: its own action, and once saved (res://Poses/ready.json, or ready_<name>)
    // the first step of every rally stroke's starting motion (serves keep their own) and the
    // NPCs' stance between strokes in the game (ReadyStance).
    public const string ReadyId = "ready";

    static readonly Action[] Defined =
    {
        new(ReadyId, "Ready stance", "Tư thế chuẩn bị", "Ready",
            "The stance every rally stroke starts from and comes back to: square to the table, feet a little wider than the shoulders, knees bent and the weight forward on the balls of the feet; the racket in front of the body at about table height, the free hand up. Save it (as ready or ready_<name>) and the NPCs stand in it between strokes in the game, its steps playing round and round, and it becomes step 1 of every stroke's starting motion (serves keep their own).",
            new(false, new(0.1f, -1.85f), Stance.Square, 0.8f, 22f, new[]
            {
                K("ready", 0f, -0.12f, 1.0f, 0.38f, -5f),
            })),
        new("serve_fh_right", "Serve: forehand, from the right", "Phát bóng thuận tay (bên phải)", "Serve",
            "Stand in the forehand corner, side-on, right foot back. Ball shown on the open palm, tossed up at least 16 cm; the racket swings back and up as it rises and brushes under (backspin) or behind (topspin) the falling ball low over the table.",
            new(false, new(-0.3f, -1.88f), Stance.Forehand, 0.8f, 22f, new[]
            {
                K("ball on the palm", 0f, -0.35f, 1.0f, 0.26f, 20f, 0f, 0f, -25f, -15f),
                K("toss, racket back", 0.35f, -0.55f, 1.12f, 0.06f, 50f, -20f, 0f, -25f, -35f),
                K("contact under the ball", 0.45f, -0.2f, 0.93f, 0.38f, 70f, 10f, 35f, -25f, -5f),
                K("finish", 0.2f, -0.12f, 0.97f, 0.46f, 60f, 25f, 50f, -25f, 5f),
            }, Serve: true)),
        new("serve_bh_left", "Serve: backhand, from the left", "Phát bóng trái tay (bên trái)", "Serve",
            "Stand square in the backhand corner, the racket in front of the body on the left. After the toss the racket swings from left to right under the falling ball, face open for backspin, the wrist adding the snap.",
            new(true, new(0.35f, -1.85f), Stance.Square, 0.8f, 25f, new[]
            {
                K("ball on the palm", 0f, 0.22f, 1.0f, 0.4f, 40f),
                K("toss, racket left", 0.35f, 0.3f, 1.05f, 0.25f, 55f, -15f, 20f),
                K("contact under the ball", 0.4f, 0.1f, 0.94f, 0.4f, 65f, 0f, 0f),
                K("finish", 0.18f, -0.12f, 0.95f, 0.45f, 60f, 15f, -25f),
            }, Serve: true)),

        new("fh_vs_topspin", "Forehand against topspin", "Thuận tay đánh bóng xoáy lên", "Forehand",
            "Close the face (about 30-45°) to keep the spinning ball down; meet it at the top of the bounce or as it rises; swing forward more than up, finishing by the forehead.",
            new(false, new(0.1f, -1.9f), Stance.Forehand, 0.82f, 20f, ForehandTopspin(0.95f, 1.05f, 1.35f, -35f))),
        new("fh_vs_underspin", "Forehand against underspin (loop)", "Thuận tay giật bóng xoáy xuống", "Forehand",
            "Racket starts low by the knee, face nearly square; take the ball as it falls and brush up steeply to lift the backspin; legs drive up, finish high.",
            new(false, new(0.1f, -1.95f), Stance.Forehand, 0.76f, 26f, ForehandTopspin(0.7f, 0.92f, 1.45f, -10f))),
        new("fh_vs_side_left", "Forehand against sidespin (left)", "Thuận tay đánh bóng xoáy ngang (trái)", "Forehand",
            "The ball wants to jump to your left: turn the face to the right and meet the ball a little more on its left side.",
            new(false, new(0.1f, -1.9f), Stance.Forehand, 0.82f, 20f, ForehandTopspin(0.95f, 1.05f, 1.35f, -30f, -18f))),
        new("fh_vs_side_right", "Forehand against sidespin (right)", "Thuận tay đánh bóng xoáy ngang (phải)", "Forehand",
            "The ball wants to jump to your right: turn the face to the left and meet the ball a little more on its right side.",
            new(false, new(0.1f, -1.9f), Stance.Forehand, 0.82f, 20f, ForehandTopspin(0.95f, 1.05f, 1.35f, -30f, 18f))),

        new("bh_vs_topspin", "Backhand against topspin", "Trái tay đánh bóng xoáy lên", "Backhand",
            "Square stance, elbow in front; close the face, take the ball early in front of the body and drive forward, the forearm and wrist uncoiling out to the right.",
            new(true, new(-0.05f, -1.85f), Stance.Square, 0.82f, 20f, BackhandTopspin())),
        new("bh_vs_side_left", "Backhand against sidespin (left)", "Trái tay đánh bóng xoáy ngang (trái)", "Backhand",
            "Turn the face to the right against a ball that wants to jump left; contact a little more on its left side.",
            new(true, new(-0.05f, -1.85f), Stance.Square, 0.82f, 20f, BackhandTopspin(-15f))),
        new("bh_vs_side_right", "Backhand against sidespin (right)", "Trái tay đánh bóng xoáy ngang (phải)", "Backhand",
            "Turn the face to the left against a ball that wants to jump right; contact a little more on its right side.",
            new(true, new(-0.05f, -1.85f), Stance.Square, 0.82f, 20f, BackhandTopspin(15f))),

        new("chopblock_right", "Chop block against topspin (forehand side)", "Chặn cắt bóng xoáy lên (bên phải)", "Block",
            "Meet the topspin just after the bounce; open the face a little and move it down and forward under the ball, killing its pace and returning backspin.",
            new(false, new(0.05f, -1.8f), Stance.Square, 0.84f, 18f, ChopBlock(false))),
        new("chopblock_left", "Chop block against topspin (backhand side)", "Chặn cắt bóng xoáy lên (bên trái)", "Block",
            "Backhand side: the open face moves down and forward under the ball just after its bounce.",
            new(true, new(-0.1f, -1.8f), Stance.Square, 0.84f, 18f, ChopBlock(true))),
        new("block_fh", "Forehand block", "Chặn bóng thuận tay", "Block",
            "Close to the table against a topspin attack: little backswing, the face closed (about 30-40°) and set in front of the body on the right; meet the ball early, just after its bounce, and let its own pace send it back, the racket going only a little forward.",
            new(false, new(0.05f, -1.8f), Stance.Square, 0.84f, 18f, Block(false))),
        new("block_bh", "Backhand block", "Chặn bóng trái tay", "Block",
            "Square to the table, elbow in front: the closed face set in front of the body, the ball met early just after its bounce and sent back with its own pace, a short push forward from the forearm.",
            new(true, new(-0.05f, -1.8f), Stance.Square, 0.84f, 18f, Block(true))),

        new("chop_fh", "Forehand chop", "Thuận tay cắt bóng (phòng thủ xa bàn)", "Chop",
            "Defend away from the table (1-1.5 m back) against a loop: side-on, the right foot back; take the racket up high by the right shoulder with the face open, wait for the ball to drop below table height, then cut down and forward under the back of the ball, the face open about 60°, finishing low in front of the body; the knees bend and the weight goes forward through it. Returns heavy backspin.",
            new(false, new(0.1f, -2.6f), Stance.Forehand, 0.76f, 25f, Chop(false))),
        new("chop_bh", "Backhand chop", "Trái tay cắt bóng (phòng thủ xa bàn)", "Chop",
            "Defend away from the table against a loop to the backhand: the left foot back, the racket up by the left shoulder, the face open; let the ball drop, then cut down and forward under it in front of the body, the forearm leading, finishing low and out to the right. Returns heavy backspin.",
            new(true, new(-0.05f, -2.6f), Stance.Backhand, 0.76f, 25f, Chop(true))),
        new("push_underspin", "Push / cut against underspin, over the table", "Đẩy / cắt bóng xoáy xuống (trên bàn)", "Short game",
            "Step in with the right foot under the table; open the face (45-60°) and push forward and slightly down under the ball at the top of its bounce, keeping it short and low.",
            new(true, new(0f, -1.66f), Stance.InTable, 0.78f, 34f, Push(true, 0f))),
        new("push_underspin_side_left", "Push against underspin with sidespin (left)", "Đẩy bóng xoáy xuống + ngang (trái)", "Short game",
            "As the push, the face turned to the right against the ball's pull to the left.",
            new(true, new(0f, -1.66f), Stance.InTable, 0.78f, 34f, Push(true, -15f))),
        new("push_underspin_side_right", "Push against underspin with sidespin (right)", "Đẩy bóng xoáy xuống + ngang (phải)", "Short game",
            "As the push, the face turned to the left against the ball's pull to the right.",
            new(true, new(0f, -1.66f), Stance.InTable, 0.78f, 34f, Push(true, 15f))),
        new("push_fh", "Forehand push over the table", "Đẩy bóng thuận tay (trên bàn)", "Short game",
            "Against a short backspin ball to the forehand: step in with the right foot under the table, the racket low over it on the right; open the face (45-60°) and push forward and slightly down under the ball at the top of its bounce, short and low.",
            new(false, new(0.05f, -1.66f), Stance.InTableForehand, 0.78f, 34f, Push(false, 0f))),
        new("flick_fh", "Forehand flick over the table", "Hất bóng thuận tay trên bàn", "Short game",
            "Right foot in under the table, racket low over it; cock the wrist back, then snap it forward and up through the top of the short ball.",
            new(false, new(0.05f, -1.66f), Stance.InTableForehand, 0.78f, 34f, Flick(false))),
        new("flick_bh", "Backhand flick over the table", "Hất bóng trái tay trên bàn", "Short game",
            "Elbow high and forward over the table, the wrist curled back towards the body, then flicked out forward and up through the ball.",
            new(true, new(0f, -1.66f), Stance.InTable, 0.78f, 34f, Flick(true))),

        new("smash", "Smash", "Đập bóng", "Attack",
            "Against a high ball: racket back at shoulder height, face slightly closed, the whole body turning into a fast forward and down hit at the top of the bounce.",
            new(false, new(0.1f, -1.95f), Stance.Forehand, 0.86f, 15f, new[]
            {
                K("ready", 0f, -0.3f, 1.1f, 0.35f, -10f, hipsTurn: -10f),
                K("backswing", 0.3f, -0.62f, 1.4f, -0.02f, -10f, -5f, -30f, -40f, -20f),
                K("contact", 0.12f, -0.35f, 1.32f, 0.42f, -20f, 0f, 0f, -10f, 0f),
                K("finish", 0.15f, 0.05f, 0.98f, 0.42f, -30f, 10f, 50f, 20f, 25f),
            })),

        // Footwork: from the ready stance to where the opponent's ball comes. Put the body where
        // you want to meet it (J / L / I / K in the Pose Lab, shift to step), then Save.
        new("move_fh_wide", "Move to a wide forehand ball", "Di chuyển sang phải đón bóng thuận tay", "Footwork",
            "Split step as the opponent hits; read the ball going wide to your forehand; push off the left foot and step the right foot out (side-step / chassé, not crossing the feet), the body staying low and level; arrive side-on with the right foot back, the racket going back as the ball bounces.",
            new(false, new(-0.32f, -1.88f), Stance.Forehand, 0.79f, 22f, MoveToBall(0.79f, 22f, 1,
                new[] { B(0.15f, 1.05f, 1.55f), B(0f, 1.12f, 0.85f), B(-0.25f, 0.98f, 0f), B(-0.52f, 0.78f, -0.78f), B(-0.64f, 1.02f, -1.36f) },
                new(-0.12f, 1.0f, 0.38f), -5f,
                K("set", 0f, -0.5f, 0.95f, -0.02f, -20f, -10f, -30f, -35f, -20f)), From: ReadyPlace)),
        new("move_bh_wide", "Move to a wide backhand ball", "Di chuyển sang trái đón bóng trái tay", "Footwork",
            "Split step as the opponent hits; the ball goes wide to your backhand: push off the right foot and step the left foot out, staying square to the table; arrive with the elbow in front, the racket coming back by the left hip as the ball bounces.",
            new(true, new(0.5f, -1.85f), Stance.Square, 0.81f, 20f, MoveToBall(0.81f, 20f, 0,
                new[] { B(-0.1f, 1.05f, 1.55f), B(0.1f, 1.12f, 0.85f), B(0.3f, 0.98f, 0f), B(0.48f, 0.78f, -0.8f), B(0.55f, 1.0f, -1.3f) },
                new(0f, 1.0f, 0.38f), -10f,
                K("set", 0f, 0.14f, 0.9f, 0.3f, -15f, -15f, 15f, 5f, 10f)), From: ReadyPlace)),
        new("move_in_short", "Step in to a short ball (middle)", "Bước vào đón bóng ngắn (giữa)", "Footwork",
            "Split step; the ball is short (it would bounce twice on your side): step in with the right foot under the table, the body leaning in over the knee, the racket low and the face open, meeting the ball at the top of its bounce; step back out after the stroke.",
            new(true, new(0f, -1.66f), Stance.InTable, 0.78f, 34f, MoveToBall(0.78f, 34f, 1,
                new[] { B(0.1f, 1.0f, 1.5f), B(0.05f, 1.05f, 0.8f), B(0.02f, 0.95f, 0f), B(0.02f, 0.78f, -0.5f), B(0.02f, 0.9f, -0.98f) },
                new(0f, 0.98f, 0.4f), 20f,
                K("set", 0f, 0.07f, 0.97f, 0.48f, 45f)), From: ReadyPlace)),
        new("move_in_short_left", "Step in to a short ball (left, backhand)", "Bước vào đón bóng ngắn bên trái (trái tay)", "Footwork",
            "Split step; the ball is short to your backhand side: step in with the right foot towards it, under the table across in front of the left foot, the body moving over to the left and leaning in; elbow forward, the backhand face open low over the table, meeting the ball at the top of its bounce; step back out after the stroke.",
            new(true, new(0.46f, -1.66f), Stance.InTable, 0.78f, 34f, MoveToBall(0.78f, 34f, 1,
                new[] { B(-0.05f, 1.0f, 1.5f), B(0.1f, 1.05f, 0.8f), B(0.28f, 0.95f, 0f), B(0.44f, 0.78f, -0.5f), B(0.48f, 0.9f, -0.98f) },
                new(0f, 0.98f, 0.4f), 20f,
                K("set", 0f, 0.07f, 0.97f, 0.48f, 45f)), From: ReadyPlace)),
        new("move_in_short_right", "Step in to a short ball (right, forehand)", "Bước vào đón bóng ngắn bên phải (thuận tay)", "Footwork",
            "Split step; the ball is short to your forehand side: step in with the right foot under the table towards the forehand corner, the body going with it and leaning in over the knee; the forehand face open low over the table, meeting the ball at the top of its bounce; step back out after the stroke.",
            new(false, new(-0.28f, -1.66f), Stance.InTableForehand, 0.78f, 34f, MoveToBall(0.78f, 34f, 1,
                new[] { B(0.15f, 1.0f, 1.5f), B(0f, 1.05f, 0.8f), B(-0.2f, 0.95f, 0f), B(-0.42f, 0.78f, -0.5f), B(-0.48f, 0.9f, -0.98f) },
                new(-0.12f, 0.98f, 0.4f), 20f,
                K("set", 0f, -0.28f, 0.97f, 0.48f, 45f)), From: ReadyPlace)),
        new("move_back_long", "Step back for a deep ball", "Lùi về đón bóng dài", "Footwork",
            "Split step; the ball is long and fast, to the end line: push back off both feet to give it time, turning side-on for the forehand and dropping the hips; the racket goes back low to loop it as it falls.",
            new(false, new(0.12f, -2.25f), Stance.Forehand, 0.78f, 25f, MoveToBall(0.78f, 25f, BothFeet,
                new[] { B(0.1f, 1.1f, 1.6f), B(0f, 1.2f, 0.8f), B(-0.1f, 1.05f, 0f), B(-0.2f, 0.78f, -1.2f), B(-0.25f, 1.0f, -1.72f) },
                new(-0.12f, 1.0f, 0.38f), -5f,
                K("set", 0f, -0.5f, 0.75f, -0.02f, -20f, -10f, -30f, -35f, -20f), arriveTime: 0.2f), From: ReadyPlace)),
        new("drive", "Drive", "Bạt bóng / Giật (drive)", "Attack",
            "A flatter, faster forehand: short backswing at waist height, face a little closed, the swing mostly forward through the ball, finishing by the forehead.",
            new(false, new(0.1f, -1.9f), Stance.Forehand, 0.84f, 18f, ForehandTopspin(1.0f, 1.05f, 1.25f, -20f))),
        new("loop", "Loop", "Líp / Giật bóng (loop)", "Attack",
            "Heavy topspin from mid-distance: racket back and low, the face closed, brushing the upper back of the ball forward and up with the legs, hips and waist; long follow-through above the head.",
            new(false, new(0.1f, -2.1f), Stance.Forehand, 0.78f, 25f, ForehandTopspin(0.75f, 1.0f, 1.5f, -35f))),
    };

    public static Action Find(string id) => Defined.FirstOrDefault(a => a.Id == id);

    // The method (MotionMethods) an action is the main action of: the same list, in the same
    // words, as the method table. Each method has one default action (DefaultOf), the others are
    // its variants. Null for footwork (a method's first action).
    public static string MethodOf(Action action) => action.Id switch
    {
        ReadyId => "ready",
        "serve_fh_right" or "serve_bh_left" => "serve",
        "drive" or "loop" or "fh_vs_topspin" or "fh_vs_underspin" or "fh_vs_side_left" or "fh_vs_side_right" => "fh_drive",
        "bh_vs_topspin" or "bh_vs_side_left" or "bh_vs_side_right" => "bh_drive",
        "push_fh" => "fh_push",
        "push_underspin" or "push_underspin_side_left" or "push_underspin_side_right" => "bh_push",
        "flick_fh" => "fh_flick",
        "flick_bh" => "bh_flick",
        "block_fh" or "chopblock_right" => "fh_block",
        "block_bh" or "chopblock_left" => "bh_block",
        "chop_fh" => "fh_chop",
        "chop_bh" => "bh_chop",
        "smash" => "smash",
        _ => null,
    };

    // Each method's default action: what the method is when nothing else is said, shown under the
    // method's own name in the Pose Lab, and what the game plays for it while nothing is ticked
    // in the method table (once its motion is saved).
    static readonly Dictionary<string, string> Defaults = new()
    {
        ["ready"] = ReadyId, ["serve"] = "serve_fh_right", ["fh_drive"] = "drive", ["bh_drive"] = "bh_vs_topspin",
        ["fh_push"] = "push_fh", ["bh_push"] = "push_underspin", ["fh_flick"] = "flick_fh", ["bh_flick"] = "flick_bh",
        ["fh_block"] = "block_fh", ["bh_block"] = "block_bh", ["fh_chop"] = "chop_fh", ["bh_chop"] = "chop_bh", ["smash"] = "smash",
    };

    public static Action DefaultOf(string method) => method != null && Defaults.TryGetValue(method, out string id) ? Find(id) : null;
    public static bool IsDefault(Action action) => MethodOf(action) is string m && Defaults.GetValueOrDefault(m) == action.Id;

    // How an action is listed: a default action by its method's name, a variant under it, footwork
    // as the first action it is.
    public static string Label(Action action) => MotionMethods.Find(MethodOf(action)) is { } m
        ? IsDefault(action) ? m.Name : $"{m.Name}: {Without(action.Name, m.Name + ": ")}"
        : $"Footwork (first action): {action.Name}";

    static string Without(string name, string prefix) => name.StartsWith(prefix) ? name[prefix.Length..] : name;

    // Every action in the method table's order: each method's default, then its variants; the
    // footwork last.
    public static readonly Action[] All = Defined
        .Select((a, i) => (a, i))
        .OrderBy(x => MotionMethods.Find(MethodOf(x.a)) is { } m ? System.Array.IndexOf(MotionMethods.All, m) : int.MaxValue)
        .ThenBy(x => IsDefault(x.a) ? 0 : 1)
        .ThenBy(x => x.i)
        .Select(x => x.a)
        .ToArray();

    // The pose a motion ends on (its last step), to start the next one from: the precondition
    // of a main action is where its first action (footwork) leaves the player.
    public static Dictionary<string, float> EndOf(string motion) =>
        PoseSequence.Load(motion) is { Steps.Count: > 0 } s ? new Dictionary<string, float>(s.Steps[^1].Values) : null;

    // Channels that are places on the floor plan (moved together when a motion is started from
    // somewhere else).
    static readonly string[] Across = { "body.x", "racket.x", "lfoot.x", "rfoot.x", "lhand.x", "ball.x" };
    static readonly string[] Along = { "body.z", "racket.z", "lfoot.z", "rfoot.z", "lhand.z", "ball.z" };

    // Whether this action's starting motion begins from the saved ready stance.
    public static bool UsesReady(Action action) => !action.Template.Serve && action.Id != ReadyId;

    // The ready stance as saved in the Pose Lab (its first step; see ReadyStance), or null if it
    // has not been.
    public static Dictionary<string, float> SavedReady() => ReadyStance.FirstStep();

    // The template as poses (values for every channel the Pose Lab knows; the rest stay at its
    // defaults). Positions become the NPC's own frame from the body's place. start: the
    // precondition, the pose it starts from (the end of the method before, such as a footwork);
    // without it, the saved ready stance. Started from a precondition, a stroke is played where
    // that leaves the player: its other steps are moved there with the body.
    public static PoseSequence Build(Action action, Dictionary<string, float> defaults, Dictionary<string, float> start = null)
    {
        Template t = action.Template;
        var sequence = new PoseSequence { Name = action.Id };
        Dictionary<string, float> ready = UsesReady(action) && t.Steps.Length > 0 && t.Steps[0].Name == "ready" ? start ?? SavedReady() : null;
        // A footwork motion starts where the ready stance stands (the saved one, or the template's
        // From in a square stance) and ends at Body in the template's stance.
        Vector2 from = t.From ?? t.Body;
        Feet fromFeet = FeetOf(Stance.Square, from.X, from.Y);
        if (ready != null && ready.TryGetValue("body.x", out float rx) && ready.TryGetValue("body.z", out float rz))
        {
            from = new Vector2(rx, rz);
            fromFeet = new Feet(ready.GetValueOrDefault("lfoot.x", fromFeet.LX), ready.GetValueOrDefault("lfoot.z", fromFeet.LZ), ready.GetValueOrDefault("lfoot.turn", fromFeet.LT),
                                ready.GetValueOrDefault("rfoot.x", fromFeet.RX), ready.GetValueOrDefault("rfoot.z", fromFeet.RZ), ready.GetValueOrDefault("rfoot.turn", fromFeet.RT));
        }
        Feet toFeet = FeetOf(t.Stance, t.Body.X, t.Body.Y);
        foreach (Key key in t.Steps)
        {
            Place w = key.Where;
            Vector2 body = w == null ? t.Body : from.Lerp(t.Body, w.Along);
            float bx = body.X, bz = body.Y;
            var p = new Dictionary<string, float>(defaults)
            {
                ["body.x"] = bx, ["body.z"] = bz,
                ["hips.height"] = w?.HipsHeight ?? t.HipsHeight, ["hips.lean"] = w?.Lean ?? t.Lean,
                ["hips.turn"] = key.HipsTurn, ["waist.turn"] = key.WaistTurn,
                ["racket.x"] = bx + key.Racket.X, ["racket.y"] = key.Racket.Y, ["racket.z"] = bz + key.Racket.Z,
                ["racket.backhand"] = t.Backhand ? 1f : 0f,
                ["racket.open"] = key.Open, ["racket.noseForward"] = key.NoseForward, ["racket.noseUp"] = key.NoseUp,
                ["racket.turn"] = key.Turn,
                // The free hand up in front for balance.
                ["lhand.x"] = bx + 0.32f, ["lhand.y"] = 1.02f, ["lhand.z"] = bz + 0.3f, ["lhand.palmUp"] = 0f, ["lhand.open"] = 0.3f,
                ["ball.show"] = 0f,
            };
            if (w == null)
                PlaceFeet(p, toFeet);
            else
            {
                // Each foot along its own way, lifted while it travels; spread wider for a split step.
                float l = w.LeftAlong, r = w.RightAlong;
                PlaceFeet(p, new Feet(Mathf.Lerp(fromFeet.LX, toFeet.LX, l) + w.Spread, Mathf.Lerp(fromFeet.LZ, toFeet.LZ, l), Mathf.Lerp(fromFeet.LT, toFeet.LT, l),
                                      Mathf.Lerp(fromFeet.RX, toFeet.RX, r) - w.Spread, Mathf.Lerp(fromFeet.RZ, toFeet.RZ, r), Mathf.Lerp(fromFeet.RT, toFeet.RT, r)));
                p["lfoot.lift"] = w.LeftLift;
                p["rfoot.lift"] = w.RightLift;
            }
            if (key.Ball is Vector3 b)
            {
                p["ball.x"] = b.X; p["ball.y"] = b.Y; p["ball.z"] = b.Z;
                p["ball.show"] = 1f;
            }
            sequence.Steps.Add(new PoseSequence.Step { Name = key.Name, Duration = key.Time, Values = p });
        }
        if (t.Serve)
            AddServeBall(sequence);
        // Step 1 is the saved ready stance, where there is one (with the ball where the step has it).
        if (ready != null)
        {
            var p = new Dictionary<string, float>(defaults);
            foreach (var (key, value) in ready)
                p[key] = value;
            p["ball.show"] = 0f;
            if (t.Steps[0].Ball is Vector3 b)
            {
                p["ball.x"] = b.X; p["ball.y"] = b.Y; p["ball.z"] = b.Z;
                p["ball.show"] = 1f;
            }
            sequence.Steps[0].Values = p;
            if (start != null)
                sequence.Steps[0].Name = "start (precondition)";
        }
        CarryAsReady(sequence, t);
        // A stroke started from a precondition is played where it leaves the player (a footwork
        // motion goes to its own place, so it is not moved).
        if (start != null && ready != null && t.From == null && start.TryGetValue("body.x", out float sx) && start.TryGetValue("body.z", out float sz))
        {
            float dx = sx - t.Body.X, dz = sz - t.Body.Y;
            for (int i = 1; i < sequence.Steps.Count; i++)
            {
                Dictionary<string, float> p = sequence.Steps[i].Values;
                foreach (string key in Across)
                    if (p.ContainsKey(key))
                        p[key] += dx;
                foreach (string key in Along)
                    if (p.ContainsKey(key))
                        p[key] += dz;
            }
        }
        return sequence;
    }

    // Footwork steps hold the racket, the free hand and the turns as the first step does (moved
    // along with the body), as much as their Carry says.
    static readonly string[] Held = { "racket.y", "racket.open", "racket.noseForward", "racket.noseUp", "racket.turn",
                                      "hips.turn", "waist.turn", "lhand.y", "lhand.palmUp", "lhand.open" };
    static void CarryAsReady(PoseSequence sequence, Template t)
    {
        if (sequence.Steps.Count == 0)
            return;
        Dictionary<string, float> first = sequence.Steps[0].Values;
        float V(Dictionary<string, float> p, string key) => p.GetValueOrDefault(key);
        for (int i = 1; i < sequence.Steps.Count && i < t.Steps.Length; i++)
        {
            float carry = t.Steps[i].Where?.Carry ?? 0f;
            if (carry <= 0f)
                continue;
            Dictionary<string, float> p = sequence.Steps[i].Values;
            float dx = V(p, "body.x") - V(first, "body.x"), dz = V(p, "body.z") - V(first, "body.z");
            foreach (string axis in new[] { "racket", "lhand" })
            {
                p[axis + ".x"] = Mathf.Lerp(V(p, axis + ".x"), V(first, axis + ".x") + dx, carry);
                p[axis + ".z"] = Mathf.Lerp(V(p, axis + ".z"), V(first, axis + ".z") + dz, carry);
            }
            // The racket's angles only while it plays the same face as the first step: half way
            // between a forehand and a backhand face is no grip at all.
            bool sameFace = carry > 0.5f || V(p, "racket.backhand") == V(first, "racket.backhand");
            foreach (string key in Held)
                if (sameFace || !key.StartsWith("racket."))
                    p[key] = Mathf.Lerp(V(p, key), V(first, key), carry);
            if (carry > 0.5f)
                p["racket.backhand"] = V(first, "racket.backhand");
        }
    }

    // Where the feet stand (own frame) and how they are turned (degrees).
    record struct Feet(float LX, float LZ, float LT, float RX, float RZ, float RT);

    // Left foot (+X) and right foot (-X) round the body: square, forehand (right foot back), or
    // stepping in under the table (the right foot forward).
    static Feet FeetOf(Stance stance, float bx, float bz)
    {
        (float lx, float lz, float lt, float rx, float rz, float rt) = stance switch
        {
            Stance.Forehand => (0.24f, 0.08f, 15f, -0.22f, -0.12f, -40f),
            Stance.Backhand => (0.22f, -0.08f, 30f, -0.24f, 0.06f, -10f),
            Stance.InTable => (0.25f, -0.05f, 10f, -0.2f, 0.2f, -5f),
            Stance.InTableForehand => (0.25f, -0.08f, 10f, -0.25f, 0.22f, -20f),
            _ => (0.24f, 0f, 15f, -0.24f, 0f, -15f),
        };
        return new Feet(bx + lx, bz + lz, lt, bx + rx, bz + rz, rt);
    }

    static void PlaceFeet(Dictionary<string, float> p, Feet f)
    {
        p["lfoot.x"] = f.LX; p["lfoot.z"] = f.LZ; p["lfoot.turn"] = f.LT; p["lfoot.lift"] = 0f;
        p["rfoot.x"] = f.RX; p["rfoot.z"] = f.RZ; p["rfoot.turn"] = f.RT; p["rfoot.lift"] = 0f;
    }

    // A serve's free hand and ball: shown on the open palm, tossed straight up, falling to the
    // contact, then away over the table.
    // The palm is under where the racket meets the ball (the third step).
    static void AddServeBall(PoseSequence s)
    {
        Dictionary<string, float> c = s.Steps[2].Values;
        var hit = new Vector3(c["racket.x"], c["racket.y"], c["racket.z"]) + new Vector3(0f, 0.03f, 0.02f);
        var palm = new Vector3(hit.X, 0.95f, hit.Z); // tossed straight up, falling back to the racket
        Vector3[] ball = { palm + new Vector3(0f, 0.025f, 0f), palm + new Vector3(0f, 0.45f, 0f), hit, hit + new Vector3(0f, -0.02f, 0.4f) };
        Vector3[] hand = { palm, palm + new Vector3(0f, 0.2f, 0f), palm + new Vector3(0.15f, 0.08f, -0.05f), palm + new Vector3(0.2f, 0.08f, -0.08f) };
        for (int i = 0; i < s.Steps.Count; i++)
        {
            Dictionary<string, float> p = s.Steps[i].Values;
            int k = Mathf.Min(i, 3);
            p["lhand.x"] = hand[k].X; p["lhand.y"] = hand[k].Y; p["lhand.z"] = hand[k].Z;
            p["lhand.palmUp"] = k < 2 ? 90f : 20f;
            p["lhand.open"] = k < 2 ? 1f : 0.4f;
            p["ball.x"] = ball[k].X; p["ball.y"] = ball[k].Y; p["ball.z"] = ball[k].Z;
            p["ball.show"] = 1f;
        }
    }
}
