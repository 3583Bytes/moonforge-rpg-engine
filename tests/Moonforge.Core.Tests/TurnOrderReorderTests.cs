using Moonforge.Core;
using Moonforge.Core.Combat;
using Moonforge.Core.Combat.Commands;
using Moonforge.Core.Data.Definitions;
using Moonforge.Core.Runtime.Commands;
using Moonforge.Core.Runtime.Events;
using Moonforge.Core.Runtime.Formulas;
using Moonforge.Core.Runtime.Random;
using Moonforge.Core.Runtime.Time;
using Moonforge.Core.Stats;

namespace Moonforge.Core.Tests;

public sealed class TurnOrderReorderTests
{
    [Fact]
    public void Stable_Initiative_Yields_Same_Order_Each_Round()
    {
        GameState gameState = BuildBattle(out CommandDispatcher dispatcher, out InMemoryDomainEventSink sink, out CommandContext context);
        IReadOnlyList<string> roundOneOrder = gameState.ActiveBattle!.TurnOrder.ToList();

        // Run a full round so the wrap-and-resort happens.
        RunRound(gameState, dispatcher, sink, context);

        Assert.Equal(roundOneOrder, gameState.ActiveBattle!.TurnOrder);
        Assert.Equal(2, gameState.ActiveBattle!.Round);
    }

    [Fact]
    public void Initiative_Debuff_Mid_Round_Shifts_Next_Round_Turn_Order()
    {
        GameState gameState = BuildBattle(out CommandDispatcher dispatcher, out InMemoryDomainEventSink sink, out CommandContext context);

        // Round 1 starts: Hero(20) → Goblin(15). Crush Hero's initiative before the
        // round wraps so they go AFTER Goblin in round 2.
        Assert.Equal("party.hero", gameState.ActiveBattle!.TurnOrder[0]);
        Assert.Equal("enemy.goblin", gameState.ActiveBattle!.TurnOrder[1]);

        gameState.ActorStatsState.GetOrCreate("party.hero").AddModifier(new StatModifier(
            statId: "initiative",
            bucket: StatModifierBucket.Flat,
            value: -100,
            sourceKind: "status",
            sourceId: "status.snare"));

        RunRound(gameState, dispatcher, sink, context);

        // Round 2: Goblin now has the higher effective initiative.
        Assert.Equal("enemy.goblin", gameState.ActiveBattle!.TurnOrder[0]);
        Assert.Equal("party.hero", gameState.ActiveBattle!.TurnOrder[1]);
    }

    [Fact]
    public void Reorder_Is_Stable_On_Initiative_Ties()
    {
        GameState gameState = BuildBattle(out CommandDispatcher dispatcher, out InMemoryDomainEventSink sink, out CommandContext context);

        // Drop hero's initiative to match goblin (15 vs 15) — tiebreak is ascending by
        // actor id, so "enemy.goblin" sorts before "party.hero".
        gameState.ActorStatsState.GetOrCreate("party.hero").AddModifier(new StatModifier(
            statId: "initiative",
            bucket: StatModifierBucket.Flat,
            value: -5,
            sourceKind: "test",
            sourceId: "tie"));

        RunRound(gameState, dispatcher, sink, context);

        Assert.Equal("enemy.goblin", gameState.ActiveBattle!.TurnOrder[0]);
        Assert.Equal("party.hero", gameState.ActiveBattle!.TurnOrder[1]);
    }

    private static GameState BuildBattle(out CommandDispatcher dispatcher, out InMemoryDomainEventSink sink, out CommandContext context)
    {
        GameState gameState = new();
        sink = new InMemoryDomainEventSink();
        dispatcher = new CommandDispatcher();
        dispatcher.Register(new StartBattleCommandHandler());
        dispatcher.Register(new UseBattleSkillCommandHandler());
        dispatcher.Register(new ExecuteAiTurnCommandHandler());

        BattleSkillDefinition attack = new("skill.attack", BattleSkillEffectType.PhysicalDamage, power: 1);

        BattleActorDefinition hero = new(
            actorId: "party.hero",
            displayName: "Hero",
            faction: CombatFaction.Party,
            maxHp: 100,
            atk: 2,
            def: 0,
            matk: 0,
            mdef: 0,
            initiative: 20,
            skillIds: ["skill.attack"],
            playerControlled: true);

        BattleActorDefinition goblin = new(
            actorId: "enemy.goblin",
            displayName: "Goblin",
            faction: CombatFaction.Enemy,
            maxHp: 100,
            atk: 2,
            def: 0,
            matk: 0,
            mdef: 0,
            initiative: 15,
            skillIds: ["skill.attack"]);

        context = BuildContext(sink);
        Assert.True(dispatcher.Dispatch(
            gameState,
            new StartBattleCommand("battle.reorder", [hero, goblin], [attack], seed: 1, sequence: 1),
            context).IsSuccess);

        return gameState;
    }

    private static void RunRound(GameState gameState, CommandDispatcher dispatcher, InMemoryDomainEventSink sink, CommandContext context)
    {
        BattleState battle = gameState.ActiveBattle!;
        int startingRound = battle.Round;

        // Each iteration of this loop runs whoever is currently up. Stop once we've
        // crossed into the next round.
        int safety = battle.TurnOrder.Count * 4;
        while (battle.Round == startingRound && safety-- > 0)
        {
            string currentActorId = battle.TurnOrder[battle.TurnIndex];
            BattleActorState actor = battle.Actors[currentActorId];
            if (actor.PlayerControlled)
            {
                string targetId = battle.Actors.Values.First(a => a.Faction != actor.Faction).ActorId;
                Assert.True(dispatcher.Dispatch(gameState, new UseBattleSkillCommand(currentActorId, "skill.attack", targetId), context).IsSuccess);
            }
            else
            {
                Assert.True(dispatcher.Dispatch(gameState, new ExecuteAiTurnCommand(), context).IsSuccess);
            }
        }

        Assert.True(battle.Round > startingRound, "round did not advance — test setup probably broken");
    }

    private static CommandContext BuildContext(InMemoryDomainEventSink sink)
    {
        return new CommandContext(
            new Pcg32RandomSource(seed: 999, sequence: 54),
            new SimulationClock(0),
            new NoOpFormulaEvaluator(),
            sink,
            new InMemoryGameDefinitionCatalog().AddCurrency(new CurrencyDefinition("currency.gold", 999)));
    }
}
