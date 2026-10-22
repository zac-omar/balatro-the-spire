using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's fights are Balatro blinds (see docs/JIMBO.md):
/// <list type="bullet">
/// <item>The enemies' HP is the blind: their total is set to its chip target, each keeping its share.</item>
/// <item>Each turn has a share of the blind's hands and discards. A played hand is a volley: each
/// scoring card strikes the target for its share of the score (attack damage, so Strength, Weak
/// and Vulnerable apply), and overkill spills onto the next enemy. Then the scoring cards' suits
/// add a little StS2: Hearts heal, Diamonds Block, Clubs Weak, Spades Vulnerable. A discard gives Block equal to
/// the discarded cards' chips. The turn ends when its hands run out, as a Balatro round does.</item>
/// <item>Winning beats the blind: Balatro cashes out, and the next map point is its shop. A Boss
/// Blind also heals Jimbo for a quarter of his HP.</item>
/// </list>
/// Plays and discards run as StS2 game actions (<see cref="JimboHandAction"/>), so they queue and
/// resolve like card plays.
/// </summary>
internal static class JimboCombat
{
    private const decimal MaxHp = 999_999_999m;
    private const decimal BossHeal = 0.25m;

    // What each scoring card's suit adds once the volley lands (see SuitRiders).
    private const decimal HeartHeal = 1m;
    private const decimal DiamondBlock = 3m;
    private const decimal ClubWeak = 1m;
    private const decimal SpadeVulnerable = 1m;

    // Overtime: each turn past the blind's hands, the enemies gain this much more Strength than the last.
    private const int OvertimeStrength = 3;

    // Mr. Bones saves a lost round whose hands scored at least this share of the blind.
    private const double MrBonesShare = 0.25;

    private static decimal _hpScale = 1;
    private static double _blindChips; // the blind's chips the enemies' HP was last scaled to
    private static bool _savedByBones;
    private static bool _begun;
    private static bool _saidLowHp;
    private static JimboQuips.Moment? _greeting; // said as his first turn begins, once the banner's gone
    private static string? _boss;

    /// <summary>How much the fight's enemy HP was multiplied by, once the blind has begun.</summary>
    public static decimal? HpScale => _begun ? _hpScale : null;

    /// <summary>The fight's docked hand, made when StS2's combat UI comes up.</summary>
    public static JimboHand? Hand => JimboHand.Current;

    /// <summary>Docks the hand as StS2 sets the fight's UI up (see <see cref="JimboCombatUi"/>).</summary>
    public static void Dock(Control combatUi, Player player)
    {
        _begun = false;
        JimboStatuses.Reset();
        JimboHand hand = JimboHand.Attach(combatUi);
        hand.Submitted += (play, ids) => Submit(player, play, ids);
        hand.SortRequested += bySuit => TaskHelper.RunSafely(Sort(player, bySuit));
        hand.Reordered += ids => TaskHelper.RunSafely(Reorder(ids));
        hand.SelectionChanged += ids => TaskHelper.RunSafely(Highlight(player, ids));
        JimboConsumables.SelectedCards = () => JimboHand.Current?.Selected ?? [];
        Float(player);
    }

    private static Node2D? Body(Player player) =>
        MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.GetCreatureNode(player.Creature) is MegaCrit.Sts2.Core.Nodes.Combat.NCreature node
            ? node.Visuals.FindChild("Body", recursive: true, owned: false) as Node2D
            : null;

    // Jimbo is a card: in a fight he floats and sways a little, the way Balatro's cards do.
    private static void Float(Player player)
    {
        if (Body(player) is not Node2D body) return;
        Vector2 home = body.Position;
        Tween bob = body.CreateTween().SetLoops();
        bob.TweenProperty(body, "position:y", home.Y - 7, 1.3).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        bob.TweenProperty(body, "position:y", home.Y + 3, 1.3).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        Tween sway = body.CreateTween().SetLoops();
        sway.TweenProperty(body, "rotation", 0.035f, 1.9).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        sway.TweenProperty(body, "rotation", -0.035f, 1.9).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    // As the hand is thrown: a quick hop toward the enemies and back.
    private static void Lunge(Player player)
    {
        if (Body(player) is not Node2D body || body.GetParent() is not Node2D visuals) return;
        Tween t = visuals.CreateTween();
        t.TweenProperty(visuals, "position:x", 46f, 0.1).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(visuals, "position:x", 0f, 0.3).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>The fight is starting: Balatro plays its blind and the enemies take on its chips as HP.</summary>
    public static async Task Begin(Player player)
    {
        CombatState? combat = player.Creature.CombatState as CombatState;
        try
        {
            BalatroSession session = await JimboTable.Connect();
            string kind = player.RunState.CurrentRoom?.RoomType is RoomType.Elite or RoomType.Boss ? "Boss" : "Small";
            TableSnapshot table = await session.JimboBlind(JimboRun.LinkFor(player), kind);
            await JimboRun.Apply(player, table);
            JimboMusic.SetTrack(table.Blind?.Boss == true ? BalatroAudio.Track.Boss : BalatroAudio.Track.Main);

            List<Creature> enemies = combat?.Enemies.Where(e => e.IsAlive).ToList() ?? [];
            decimal blind = (decimal)Math.Min(table.Blind?.Chips ?? 300, (double)MaxHp);
            decimal total = enemies.Sum(e => (decimal)e.MaxHp);
            _hpScale = total > 0 ? blind / total : 1;
            _blindChips = (double)blind;
            _savedByBones = false;
            foreach (Creature enemy in enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, ScaledHp(enemy.MaxHp));
            _begun = true;
            foreach (Creature enemy in enemies) JimboScaling.Rescale(enemy);

            if (Hand is JimboHand hand)
            {
                hand.ListenForClicks(enemies);
                hand.SetTarget(enemies.FirstOrDefault());
                await hand.Deal(table);
            }
            BridgeMod.LogInfo($"jimbo: {table.Blind?.Name} ({table.Blind?.Chips} chips) vs {enemies.Count} enemies, x{_hpScale:0.##} HP");
            _saidLowHp = false;
            _greeting = table.Blind?.Boss == true ? JimboQuips.Moment.Boss : JimboQuips.Moment.Start;
            _boss = table.Blind?.Key;
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: the blind didn't start: {e}");
            Hand?.SetStatus("Balatro isn't answering...", StsColors.red);
        }
    }

    private static decimal ScaledHp(int hp) => Math.Clamp(Math.Round(hp * _hpScale), 1, MaxHp);

    /// <summary>
    /// The blind's chips changed mid-fight (a Boss Blind disabled by Luchador: The Wall halves, Violet
    /// Vessel thirds): the enemies' HP and max HP follow, as they were scaled to the blind at the start.
    /// </summary>
    public static async Task FollowBlind(Player player, TableSnapshot table)
    {
        if (!_begun || !table.InFight || table.Blind?.Chips is not double chips || chips <= 0 || _blindChips <= 0) return;
        double ratio = Math.Min(chips, (double)MaxHp) / _blindChips;
        if (Math.Abs(ratio - 1) < 1e-9 || player.Creature.CombatState is not CombatState state) return;
        _blindChips = Math.Min(chips, (double)MaxHp);
        _hpScale *= (decimal)ratio;
        JimboScaling.Bypass = true;
        try
        {
            foreach (Creature enemy in state.Enemies.Where(e => e.IsAlive).ToList())
            {
                decimal current = enemy.CurrentHp;
                await CreatureCmd.SetMaxHp(enemy, Math.Clamp(Math.Round(enemy.MaxHp * (decimal)ratio), 1, MaxHp));
                await CreatureCmd.SetCurrentHp(enemy, Math.Clamp(Math.Round(current * (decimal)ratio), 1, enemy.MaxHp));
            }
        }
        finally
        {
            JimboScaling.Bypass = false;
        }
        BridgeMod.LogInfo($"jimbo: the blind is now {chips} chips; enemies x{ratio:0.##}");
    }

    /// <summary>
    /// Whether Mr. Bones (the joker <paramref name="relic"/>) would save Jimbo from dying now: as in
    /// Balatro, the fight's hands must have scored a quarter of the blind, and he mustn't be debuffed.
    /// </summary>
    internal static bool MrBonesWouldSave(Content.JokerRelic relic)
    {
        if (relic.BalatroKey != "j_mr_bones" || !_begun || _savedByBones || relic.Owner is not Player player || !JimboRun.IsJimbo(player)) return false;
        if (JimboRun.Last is not { InFight: true, Blind.Chips: > 0 } table) return false;
        TableCard? card = table.Jokers.FirstOrDefault(j => j.Id == relic.CardId);
        return card is { Debuffed: false } && table.RoundChips / table.Blind!.Chips!.Value >= MrBonesShare;
    }

    /// <summary>
    /// Mr. Bones stepped in: Jimbo lives on at 1 HP and the fight ends as won. Balatro then saves the
    /// round itself (no blind reward) and Mr. Bones goes, as he does in Balatro (see CashOut).
    /// </summary>
    internal static async Task SavedByMrBones(Player player, Creature creature)
    {
        _savedByBones = true;
        await CreatureCmd.Heal(creature, 1);
        Hand?.SetStatus("Saved by Mr. Bones!", StsColors.gold);
        if (player.Creature.CombatState is CombatState state)
        {
            await CreatureCmd.Kill(state.Enemies.Where(e => e.IsAlive).ToList(), force: true);
        }
    }

    /// <summary>A summoned enemy joins the blind at the same scale as the rest.</summary>
    public static async Task CreatureAdded(Creature creature)
    {
        if (!_begun || !creature.IsEnemy) return;
        JimboScaling.Bypass = true;
        try
        {
            await CreatureCmd.SetMaxAndCurrentHp(creature, ScaledHp(creature.MaxHp));
        }
        finally
        {
            JimboScaling.Bypass = false;
        }
        Hand?.ListenForClicks([creature]);
    }

    /// <summary>A new turn: this turn's hands and discards (and, past the blind's hands, overtime).</summary>
    public static async Task TurnStart(Player player, PlayerChoiceContext choiceContext)
    {
        if (!_begun) return;
        try
        {
            (System.Text.Json.Nodes.JsonObject rosetta, string? said) = JimboRosetta.Turn(player);
            TableSnapshot table = await JimboRun.Session().JimboTurn(JimboRun.LinkFor(player), rosetta);
            await JimboRun.Apply(player, table);
            if (Hand is JimboHand hand)
            {
                await hand.Deal(table);
                if (hand.Target is not { IsAlive: true }) hand.SetTarget(FirstEnemy(player));
                hand.SetInputEnabled(true);
            }
            if (await Overtime(player, table, choiceContext)) return;
            if (said != null) Hand?.SetStatus(said, StsColors.cream);
            if (_greeting is JimboQuips.Moment greeting)
            {
                _greeting = null;
                JimboQuips.Maybe(player, greeting, delay: 1.2, bossKey: _boss); // after the turn banner
            }
            else if (!_saidLowHp && player.Creature.CurrentHp * 10 < player.Creature.MaxHp * 3)
            {
                _saidLowHp = true;
                JimboQuips.Maybe(player, JimboQuips.Moment.LowHp);
            }
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: the turn didn't start in Balatro: {e}");
            Hand?.SetStatus("Balatro isn't answering...", StsColors.red);
        }
    }

    /// <summary>
    /// Overtime, Balatro's hand limit in StS2's terms: a blind gives its hands and no more. Once
    /// Jimbo's turns have used them all, the enemies gain Strength every turn, more each time (3,
    /// then 6, then 9...), so a blind he can't keep pace with gets him in the end instead of
    /// dragging on. Returns whether the turn is in overtime (it has its own word from Jimbo).
    /// </summary>
    private static async Task<bool> Overtime(Player player, TableSnapshot table, PlayerChoiceContext choiceContext)
    {
        int turn = player.PlayerCombatState?.TurnNumber ?? 1;
        int limit = table.PerTurn?.Turns ?? 0;
        Hand?.ShowTurn(turn, limit);
        int over = turn - limit;
        if (limit <= 0 || over <= 0 || player.Creature.CombatState is not CombatState state) return false;
        int strength = OvertimeStrength * over;
        foreach (Creature enemy in state.Enemies.Where(e => e.IsAlive).ToList())
        {
            await PowerCmd.Apply<StrengthPower>(choiceContext, enemy, strength, null, null);
        }
        TableFx.Sound(over == 1 ? "gong" : "timpani", 1f, 0.6f);
        Hand?.SetStatus($"Overtime! Enemies gain {strength} Strength.", StsColors.red);
        // A word as overtime starts, and now and then as it drags on.
        if (over % 3 == 1) JimboQuips.Maybe(player, JimboQuips.Moment.Overtime);
        return true;
    }

    private static Creature? FirstEnemy(Player player) =>
        (player.Creature.CombatState as CombatState)?.Enemies.FirstOrDefault(e => e.IsAlive && e.IsHittable);

    // The hand UI asked to play or discard: that happens as a game action.
    private static void Submit(Player player, bool play, int[] ids)
    {
        Creature? target = Hand?.Target is { IsAlive: true } t ? t : FirstEnemy(player);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new JimboHandAction(player, play, ids, target?.CombatId));
    }

    /// <summary>Plays or discards the cards (from <see cref="JimboHandAction"/>).</summary>
    public static async Task Resolve(Player player, bool play, int[] ids, uint? targetId, PlayerChoiceContext choiceContext)
    {
        JimboHand? hand = Hand;
        BalatroSession session = JimboRun.Session();
        JimboLink link = JimboRun.LinkFor(player);
        try
        {
            if (!play)
            {
                Task throwAway = hand?.ThrowAway(ids) ?? Task.CompletedTask;
                TableSnapshot refilled = await session.Discard(ids);
                await throwAway;
                if (refilled.DiscardedChips > 0)
                {
                    hand?.ShowBlock(refilled.DiscardedChips);
                    await CreatureCmd.GainBlock(player.Creature, (decimal)refilled.DiscardedChips, ValueProp.Move, null);
                }
                await JimboRun.Apply(player, refilled);
                if (hand != null) await hand.Deal(refilled);
                JimboQuips.Maybe(player, JimboQuips.Moment.Discard);
                return;
            }

            // The cards as they were in hand (their suits ride along with their strikes).
            Dictionary<int, TableCard> inHand = JimboRun.Last?.Hand.ToDictionary(c => c.Id) ?? [];
            Task moveToPlay = hand?.MoveToPlay(ids) ?? Task.CompletedTask;
            // The Rosetta Stone: Strength and Vigor join the hand as Mult and Chips.
            (int strength, int vigor) = JimboRosetta.Bonus(player);
            ScoredHand scored = await session.PlayCards(ids, strength, vigor);
            if (vigor > 0) await JimboRosetta.Spent(player);
            await moveToPlay;
            // NaN first: converting it to a decimal throws (an infinite score is just capped).
            decimal damage = double.IsNaN(scored.Score) ? 0 : (decimal)Math.Min(Math.Max(scored.Score, 0), (double)MaxHp);
            Creature? target = await TargetFor(player, targetId);
            // A hand that dwarfs what it's thrown at catches fire, and so does Jimbo.
            float fire = JimboFire.Heat(damage, target);
            if (hand != null) await hand.Score(scored, damage, fire);
            if (fire > 0)
            {
                JimboFire.Ignite(player, fire);
                JimboQuips.Maybe(player, fire >= 0.9f ? JimboQuips.Moment.Inferno : JimboQuips.Moment.OnFire);
            }

            // The volley: each scoring card flies at the target with its share of the score, and
            // its suit adds its own effect. A hand with nothing scoring is still one throw.
            // (A debuffed card doesn't score, so it doesn't strike either.)
            List<int> strikers = scored.Played.Where(id => scored.Scoring.Contains(id) && inHand.GetValueOrDefault(id)?.Debuffed != true).ToList();
            int strikes = Math.Max(1, strikers.Count);
            decimal enemyHp = (player.Creature.CombatState as CombatState)?.Enemies.Where(e => e.IsAlive).Sum(e => (decimal)e.CurrentHp) ?? 0;
            Lunge(player);
            await CreatureCmd.TriggerAnim(player.Creature, "Attack", player.Character.AttackAnimDelay);
            for (int i = 0; i < strikes && CombatManager.Instance.IsInProgress; i++)
            {
                if (target is not { IsAlive: true }) target = FirstEnemy(player);
                if (target == null) break;
                int? cardId = strikers.Count > 0 ? strikers[i] : null;
                if (hand != null) await hand.Strike(cardId, target);
                await Hit(player, target, Share(damage, strikes, i), choiceContext);
            }
            // Then the scoring cards' suits take effect, so a hand's damage is its score.
            await SuitRiders(player, target is { IsAlive: true } ? target : FirstEnemy(player),
                strikers.Select(id => inHand.GetValueOrDefault(id)).OfType<TableCard>().ToList(), choiceContext);
            // A fight with no discards (The Water, higher stakes): the played cards give the Block a discard would.
            if (JimboRun.Last?.PerTurn is { Discards: 0 } && CombatManager.Instance.IsInProgress)
            {
                double chips = scored.Played.Sum(id => inHand.GetValueOrDefault(id)?.Chips ?? 0);
                if (chips > 0)
                {
                    hand?.ShowBlock(chips);
                    await CreatureCmd.GainBlock(player.Creature, (decimal)chips, ValueProp.Move, null);
                }
            }
            if (hand != null) await hand.ClearPlay();
            if (fire > 0) JimboFire.Douse(player);
            // A hand that took a big bite out of the blind deserves a word (a blaze already got one).
            if (fire <= 0 && CombatManager.Instance.IsInProgress && enemyHp > 0 && damage * 5 >= enemyHp * 2) JimboQuips.Maybe(player, JimboQuips.Moment.BigHand);
            BridgeMod.LogInfo($"jimbo: {scored.Hand} for {scored.Score} in {strikes} strikes");

            TableSnapshot after = await session.JimboState(link);
            await JimboRun.Apply(player, after);
            if (hand != null) await hand.Deal(after);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: the {(play ? "hand" : "discard")} failed: {e}");
            hand?.SetStatus("Balatro isn't answering...", StsColors.red);
            try
            {
                TableSnapshot state = await session.JimboState(link);
                await JimboRun.Apply(player, state);
                if (hand != null) await hand.Deal(state);
            }
            catch (Exception again)
            {
                BridgeMod.LogError($"jimbo: and the table couldn't be refreshed: {again.Message}");
            }
        }
        finally
        {
            if (hand != null && CombatManager.Instance.IsInProgress && player.Creature.CombatState?.CurrentSide == CombatSide.Player)
            {
                if (hand.Target is not { IsAlive: true }) hand.SetTarget(FirstEnemy(player));
                if (play && JimboRun.Last is { InFight: true, HandsLeft: <= 0 })
                {
                    // Out of hands: the turn is over, as a Balatro round would be.
                    hand.SetStatus("Out of hands!", StsColors.gold);
                    EndTurnSoon(player);
                }
                else
                {
                    hand.SetInputEnabled(true);
                }
            }
        }
    }

    /// <summary>
    /// What the scoring cards' suits do once the volley lands: each Heart heals Jimbo, each Diamond
    /// gives him Block, each Club Weakens the target and each Spade makes it Vulnerable (for the
    /// enemies' turn and his next hand). Wild Cards count as every suit, Stone Cards as none.
    /// </summary>
    private static async Task SuitRiders(Player player, Creature? target, List<TableCard> cards, PlayerChoiceContext choiceContext)
    {
        int Count(string suit) => cards.Count(c => c.CountsAs(suit));
        int hearts = Count("Hearts"), diamonds = Count("Diamonds"), clubs = Count("Clubs"), spades = Count("Spades");
        if (!CombatManager.Instance.IsInProgress) return;
        if (hearts > 0) await CreatureCmd.Heal(player.Creature, HeartHeal * hearts);
        if (diamonds > 0) await CreatureCmd.GainBlock(player.Creature, DiamondBlock * diamonds, ValueProp.Unpowered, null);
        if (target is not { IsAlive: true } || !CombatManager.Instance.IsInProgress) return;
        if (clubs > 0) await PowerCmd.Apply<WeakPower>(choiceContext, target, ClubWeak * clubs, player.Creature, null);
        if (spades > 0) await PowerCmd.Apply<VulnerablePower>(choiceContext, target, SpadeVulnerable * spades, player.Creature, null);
    }

    // A strike's part of the score: the score shared out evenly, the odd chips going first.
    private static decimal Share(decimal damage, int strikes, int index)
    {
        decimal whole = Math.Round(damage);
        decimal each = Math.Floor(whole / strikes);
        return each + (index < whole - each * strikes ? 1 : 0);
    }

    // Ends the turn once the hand's action has finished (ending it from inside would wait on itself).
    private static void EndTurnSoon(Player player)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CreateTimer(0.5 / TableFx.Speed).Timeout += () =>
        {
            if (!CombatManager.Instance.IsInProgress || player.Creature.CombatState?.CurrentSide != CombatSide.Player) return;
            if (JimboRun.Last is { HandsLeft: > 0 }) return; // something gave a hand back
            PlayerCmd.EndTurn(player, canBackOut: false);
        };
    }

    private static async Task<Creature?> TargetFor(Player player, uint? targetId)
    {
        ICombatState? state = player.Creature.CombatState;
        if (state == null) return null;
        Creature? target = targetId is uint id ? await state.GetCreatureAsync(id, 2.0) : null;
        return target is { IsAlive: true } ? target : FirstEnemy(player);
    }

    /// <summary>Deals the score to the target; what's left over after it falls hits the next enemy.</summary>
    private static async Task Hit(Player player, Creature? target, decimal damage, PlayerChoiceContext choiceContext)
    {
        var hit = new HashSet<Creature>();
        while (target != null && damage > 0)
        {
            hit.Add(target);
            IEnumerable<DamageResult> results = await CreatureCmd.Damage(choiceContext, target, damage, ValueProp.Move, player.Creature, null, null);
            decimal overkill = results.Where(r => r.Receiver == target).Sum(r => (decimal)r.OverkillDamage);
            if (target.IsAlive || overkill <= 0) break;
            damage = overkill;
            target = (player.Creature.CombatState as CombatState)?.Enemies.FirstOrDefault(e => e.IsAlive && e.IsHittable && !hit.Contains(e));
        }
    }

    private static async Task Sort(Player player, bool bySuit)
    {
        try
        {
            TableSnapshot sorted = await JimboRun.Session().SortHand(bySuit);
            Hand?.Reorder(sorted);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: sort failed: {e.Message}");
        }
    }

    // Dragging cards around moves them in Balatro's hand too (Death copies the rightmost selected card).
    private static async Task Reorder(int[] ids)
    {
        try
        {
            await JimboRun.Session().ReorderHand(ids);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: reordering the hand failed: {e.Message}");
        }
    }

    // Selecting cards tells Balatro, so the potion belt knows which consumables work on them.
    private static async Task Highlight(Player player, int[] ids)
    {
        try
        {
            HighlightResult result = await JimboRun.Session().JimboHighlight(JimboRun.LinkFor(player), ids);
            JimboConsumables.SetUsable(result.Usable);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: highlight failed: {e.Message}");
        }
    }

    /// <summary>
    /// Between a won fight and collecting its cash-out, the map stays shut (see
    /// <see cref="BalatroBridge.Content.StakeRelic"/>): StS2 opens travel the moment a fight ends,
    /// while Balatro is still adding up the round, and an early click would skip the shop.
    /// </summary>
    public static bool CashingOut { get; private set; }

    /// <summary>The fight is won: the blind is beaten.</summary>
    public static async Task Won(Player player, CombatRoom room)
    {
        _begun = false;
        CashingOut = true;
        JimboFire.Douse(player);
        JimboQuips.Maybe(player, JimboQuips.Moment.Victory);
        JimboMusic.SetTrack(BalatroAudio.Track.Main);
        JimboHand? hand = Hand;
        if (hand != null) await hand.Close();
        if (room.RoomType is RoomType.Elite or RoomType.Boss && player.Creature.IsAlive)
        {
            await CreatureCmd.Heal(player.Creature, Math.Ceiling(player.Creature.MaxHp * BossHeal));
        }
    }

    /// <summary>
    /// After a won fight (in place of StS2's rewards): Balatro adds up the cash-out and Jimbo
    /// collects it, then the map opens. The last act's boss leads to StS2's ending instead.
    /// </summary>
    public static async Task CashOut(Player player, CombatRoom room)
    {
        try
        {
            bool saved = _savedByBones;
            _savedByBones = false;
            CashOut cash = await JimboRun.Session().JimboWin(JimboRun.LinkFor(player), saved);
            // What the round's end already paid (Gold Cards held, Rental) is shown on the cash-out
            // and arrives with it, so gold doesn't move before the player collects.
            await JimboRun.Apply(player, cash.Table with { Dollars = cash.Table.Dollars - cash.PaidTotal });
            await JimboCashOutScreen.Show(cash);
            TableSnapshot shop = await JimboRun.Session().JimboCashOut(JimboRun.LinkFor(player));
            await JimboRun.Apply(player, shop);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: cashing out failed: {e}");
        }
        finally
        {
            CashingOut = false;
            MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.SetTravelEnabled(true);
        }
        // As StS2's reward screen would: an act's boss moves the run on to the next act (or the end).
        IRunState state = player.RunState;
        bool firstOfTwoBosses = state.Map.SecondBossMapPoint != null && state.CurrentMapCoord == state.Map.BossMapPoint.coord;
        if (room.RoomType == RoomType.Boss && !firstOfTwoBosses) RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
        else await RunManager.Instance.ProceedFromTerminalRewardsScreen();
    }
}

/// <summary>Jimbo plays or discards the selected cards (see <see cref="JimboCombat.Resolve"/>).</summary>
public sealed class JimboHandAction : GameAction
{
    public JimboHandAction(Player player, bool play, int[] cards, uint? targetId)
    {
        Player = player;
        Play = play;
        Cards = cards;
        TargetId = targetId;
    }

    public Player Player { get; }

    public bool Play { get; }

    public int[] Cards { get; }

    public uint? TargetId { get; }

    public override ulong OwnerId => Player.NetId;

    public override GameActionType ActionType => GameActionType.CombatPlayPhaseOnly;

    protected override Task ExecuteAction() =>
        JimboCombat.Resolve(Player, Play, Cards, TargetId, new GameActionPlayerChoiceContext(this));

    public override INetAction ToNetAction() => new NetJimboHandAction { play = Play, cards = Cards, targetId = TargetId };

    public override string ToString() => $"{nameof(JimboHandAction)} {(Play ? "play" : "discard")} [{string.Join(",", Cards)}] target {TargetId}";
}

/// <summary>A <see cref="JimboHandAction"/> as StS2 records it for combat replays.</summary>
public struct NetJimboHandAction : INetAction, IPacketSerializable
{
    public bool play;

    public int[] cards;

    public uint? targetId;

    public GameAction ToGameAction(Player player) => new JimboHandAction(player, play, cards ?? [], targetId);

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(play);
        int[] ids = cards ?? [];
        writer.WriteInt(ids.Length, 8);
        foreach (int id in ids) writer.WriteInt(id);
        writer.WriteBool(targetId.HasValue);
        if (targetId.HasValue) writer.WriteUInt(targetId.Value, 16);
    }

    public void Deserialize(PacketReader reader)
    {
        play = reader.ReadBool();
        int count = reader.ReadInt(8);
        cards = new int[count];
        for (int i = 0; i < count; i++) cards[i] = reader.ReadInt();
        targetId = reader.ReadBool() ? reader.ReadUInt(16) : null;
    }
}
