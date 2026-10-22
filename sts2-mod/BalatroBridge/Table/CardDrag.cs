using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// A row of cards handled the way Balatro's are: press a card and let go to pick it, or hold it
/// and drag it along the row to move it while the others make room. The row's owner keeps the
/// list, lays it out (each card's <see cref="CardView.Home"/>), and hears about clicks and moves.
/// </summary>
internal sealed class CardDrag
{
    // How far the mouse travels before a press is a drag rather than a click.
    private const float Threshold = 12f;
    private const float Lifted = 1.08f;

    private readonly List<CardView> _row;
    private readonly Func<bool> _enabled;
    private readonly Action _layout;
    private readonly Action<CardView> _click;
    private readonly Action<CardView, int[]> _moved;
    private CardView? _pressed;
    private Vector2 _pressAt, _grab, _last;
    private int[] _before = [];
    private Tween? _settle;

    /// <param name="row">The cards, left to right; reordered in place as a card is dragged.</param>
    /// <param name="enabled">Whether cards can be dragged right now.</param>
    /// <param name="layout">Sets each card's Home for the row's current order (doesn't move them).</param>
    /// <param name="click">A press and release without dragging.</param>
    /// <param name="moved">A drag that changed the order: the card dragged, and the ids left to right.</param>
    public CardDrag(List<CardView> row, Func<bool> enabled, Action layout, Action<CardView> click, Action<CardView, int[]> moved)
    {
        _row = row;
        _enabled = enabled;
        _layout = layout;
        _click = click;
        _moved = moved;
    }

    /// <summary>The card being dragged: layout leaves it where the mouse has it.</summary>
    public CardView? Dragging { get; private set; }

    /// <summary>A drag began (the owner hides the card's tooltip, say).</summary>
    public event Action<CardView>? Started;

    public void Attach(CardView view) => view.Root.GuiInput += input => OnInput(view, input);

    /// <summary>Drops whatever is held where it is, without a move (the row is being redealt).</summary>
    public void Cancel()
    {
        if (Dragging is CardView view && GodotObject.IsInstanceValid(view.Root)) Settle(view);
        Dragging = null;
        _pressed = null;
    }

    private void OnInput(CardView view, InputEvent input)
    {
        if (!GodotObject.IsInstanceValid(view.Root)) return;
        switch (input)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _pressed = view;
                _pressAt = _last = Local(view, press.GlobalPosition);
                view.Root.AcceptEvent();
                break;
            case InputEventMouseMotion motion when _pressed == view:
                Vector2 at = Local(view, motion.GlobalPosition);
                if (Dragging == null)
                {
                    if (at.DistanceTo(_pressAt) < Threshold || !_enabled() || !_row.Contains(view)) return;
                    Begin(view);
                }
                if (Dragging == view) Follow(view, at);
                view.Root.AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _pressed == view:
                _pressed = null;
                if (Dragging == view) Drop(view);
                else if (Dragging == null) _click(view);
                view.Root.AcceptEvent();
                break;
        }
    }

    // The mouse in the row's own space (the cards' parent), however the card under it is turned.
    private static Vector2 Local(CardView view, Vector2 global) =>
        view.Root.GetParent() is CanvasItem parent ? parent.GetGlobalTransform().AffineInverse() * global : global;

    private void Begin(CardView view)
    {
        Dragging = view;
        _before = _row.Select(c => c.Id).ToArray();
        _grab = view.Center - _pressAt;
        view.MoveTween?.Kill();
        view.Root.ZIndex = 50;
        view.Root.CreateTween().TweenProperty(view.Root, "scale", Vector2.One * Lifted, 0.12 / TableFx.Speed)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        TableFx.Sound("cardSlide1", 1.1f, 0.5f);
        Started?.Invoke(view);
    }

    private void Follow(CardView view, Vector2 at)
    {
        Vector2 centre = at + _grab;
        view.Root.Position = centre - view.Root.Size / 2;
        // It leans into the way it's moving, then rights itself, as Balatro's cards do.
        float lean = Mathf.Clamp((at.X - _last.X) * 0.012f, -0.3f, 0.3f);
        _last = at;
        _settle?.Kill();
        view.Root.Rotation = Mathf.Lerp(view.Root.Rotation, lean, 0.5f);
        _settle = view.Root.CreateTween();
        _settle.TweenProperty(view.Root, "rotation", 0f, 0.25 / TableFx.Speed).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

        // Its place in the row: after every other card whose spot is left of it.
        int slot = _row.Count(c => c != view && c.Home.X < centre.X);
        int now = _row.IndexOf(view);
        if (now < 0 || slot == now) return;
        _row.RemoveAt(now);
        _row.Insert(slot, view);
        _layout();
        foreach (CardView other in _row.Where(c => c != view)) TableFx.Move(other, other.Home, other.HomeRotation, 0.16f);
        Restack();
        TableFx.Sound("paper1", 1f + 0.05f * slot, 0.25f);
    }

    private void Drop(CardView view)
    {
        Dragging = null;
        Settle(view);
        _layout();
        TableFx.Move(view, view.Home, view.HomeRotation, 0.2f, 0, Tween.TransitionType.Back);
        Restack();
        TableFx.Sound("cardSlide2", 1f, 0.4f);
        int[] after = _row.Select(c => c.Id).ToArray();
        if (!after.SequenceEqual(_before)) _moved(view, after);
    }

    private void Settle(CardView view)
    {
        _settle?.Kill();
        view.Root.ZIndex = 0;
        view.Root.CreateTween().TweenProperty(view.Root, "scale", Vector2.One, 0.15 / TableFx.Speed);
    }

    // Cards further right draw over the ones before them: the row's cards swap places among the
    // spots they hold in their parent, leaving anything else there (buttons, prices) as it was.
    private void Restack()
    {
        if (_row.Count < 2 || _row[0].Root.GetParent() is not Node parent || _row.Any(c => c.Root.GetParent() != parent)) return;
        List<int> spots = _row.Select(c => c.Root.GetIndex()).OrderBy(i => i).ToList();
        for (int i = 0; i < _row.Count; i++)
        {
            Control want = _row[i].Root;
            int at = want.GetIndex();
            if (at == spots[i]) continue;
            Node there = parent.GetChild(spots[i]);
            parent.MoveChild(want, spots[i]);
            parent.MoveChild(there, at);
        }
    }
}
