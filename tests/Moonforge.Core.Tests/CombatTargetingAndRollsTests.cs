using System.Collections.Generic;
using System.Linq;
using Moonforge.Core;
using Moonforge.Core.Combat;
using Moonforge.Core.Combat.Commands;
using Moonforge.Core.Combat.Events;
using Moonforge.Core.Data.Definitions;
using Moonforge.Core.Runtime.Commands;
using Moonforge.Core.Runtime.Events;
using Moonforge.Core.Runtime.Formulas;
using Moonforge.Core.Runtime.Random;
using Moonforge.Core.Runtime.Results;
using Moonforge.Core.Runtime.Time;

namespace Moonforge.Core.Tests;

public sealed class CombatTargetingAndRollsTests
{
    [Fact]
    public void AllEnemies_Aoe_Hits_Every_Enemy_In_One_Dispatch()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition fireBurst = new(
            "skill.fireburst",
            BattleSkillEffectType.MagicalDamage,
            power: 6,
            targetMode: BattleSkillTargetMode.AllEnemies);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.fireburst"],
                extraSkills: [fireBurst]),
            CreateContext(sink)).IsSuccess);

        // explicit target is ignored for AoE; pick anything alive.
        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.fireburst", "enemy.goblin"),
            CreateContext(sink)).IsSuccess);

        List<BattleActionResolvedEvent> hits = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Where(e => e.SkillId == "skill.fireburst" && !e.WasHeal)
            .ToList();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.TargetActorId == "enemy.goblin");
        Assert.Contains(hits, h => h.TargetActorId == "enemy.shaman");
        Assert.True(gameState.ActiveBattle!.Actors["enemy.goblin"].Hp < 20);
        Assert.True(gameState.ActiveBattle!.Actors["enemy.shaman"].Hp < 24);
    }

    [Fact]
    public void AllAllies_Heal_Skips_Full_Hp_Allies_Silently()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition healAll = new(
            "skill.healall",
            BattleSkillEffectType.Heal,
            power: 8,
            targetMode: BattleSkillTargetMode.AllAllies);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.healall"],
                extraSkills: [healAll]),
            CreateContext(sink)).IsSuccess);

        // hero starts at full HP (35/35). Wound the cleric so only she's eligible.
        gameState.ActiveBattle!.Actors["party.cleric"].Hp = 10;

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.healall", "party.cleric"),
            CreateContext(sink)).IsSuccess);

        List<BattleActionResolvedEvent> heals = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Where(e => e.SkillId == "skill.healall" && e.WasHeal)
            .ToList();

        Assert.Single(heals);
        Assert.Equal("party.cleric", heals[0].TargetActorId);
        Assert.Equal(35, gameState.ActiveBattle!.Actors["party.hero"].Hp); // unchanged
        Assert.True(gameState.ActiveBattle!.Actors["party.cleric"].Hp > 10);
    }

    [Fact]
    public void Self_Target_Resolves_Caster_As_Target()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition selfHeal = new(
            "skill.selfheal",
            BattleSkillEffectType.Heal,
            power: 5,
            targetMode: BattleSkillTargetMode.Self);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.selfheal"],
                extraSkills: [selfHeal]),
            CreateContext(sink)).IsSuccess);

        gameState.ActiveBattle!.Actors["party.hero"].Hp = 10;

        // pass an irrelevant target id; Self mode ignores it.
        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.selfheal", "enemy.goblin"),
            CreateContext(sink)).IsSuccess);

        BattleActionResolvedEvent heal = Assert.Single(sink.Events.OfType<BattleActionResolvedEvent>().Where(e => e.SkillId == "skill.selfheal"));
        Assert.Equal("party.hero", heal.TargetActorId);
        Assert.True(heal.WasHeal);
        Assert.True(gameState.ActiveBattle!.Actors["party.hero"].Hp > 10);
    }

    [Fact]
    public void Skill_With_Zero_Accuracy_Always_Misses_And_Emits_Miss_Event()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition airSlash = new(
            "skill.airslash",
            BattleSkillEffectType.PhysicalDamage,
            power: 99,
            accuracyPercent: 0);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.airslash"],
                extraSkills: [airSlash]),
            CreateContext(sink)).IsSuccess);

        int hpBefore = gameState.ActiveBattle!.Actors["enemy.goblin"].Hp;

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.airslash", "enemy.goblin"),
            CreateContext(sink)).IsSuccess);

        Assert.Contains(sink.Events, e => e is BattleActionMissedEvent);
        Assert.DoesNotContain(sink.Events, e => e is BattleActionResolvedEvent r && r.SkillId == "skill.airslash");
        Assert.Equal(hpBefore, gameState.ActiveBattle!.Actors["enemy.goblin"].Hp);
    }

    [Fact]
    public void Damage_Variance_Is_Deterministic_Per_Seed_But_Differs_From_No_Variance()
    {
        int noVariance = RunSingleHitWithVariance(seed: 1234, variancePercent: 0);
        int withVarianceRunA = RunSingleHitWithVariance(seed: 1234, variancePercent: 50);
        int withVarianceRunB = RunSingleHitWithVariance(seed: 1234, variancePercent: 50);

        // Same seed → same result with variance.
        Assert.Equal(withVarianceRunA, withVarianceRunB);

        // Variance changed at least one rolled value vs. the deterministic baseline.
        // (Could in principle land on exactly the same value, but the seed/variance combo here doesn't.)
        Assert.NotEqual(noVariance, withVarianceRunA);
    }

    [Fact]
    public void Buff_Applies_Status_To_Ally_With_No_Hp_Change()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        StatusEffectDefinition fortify = new(
            id: "status.fortify",
            displayName: "Fortify",
            durationTurns: 3,
            tickHpDelta: 0,
            preventsAction: false,
            statModifiers: new Dictionary<string, int>(System.StringComparer.Ordinal) { ["def"] = 5 },
            stackPolicy: StatusStackPolicy.RefreshDuration);

        BattleSkillDefinition fortifySkill = new(
            "skill.fortify",
            BattleSkillEffectType.Buff,
            power: 0,
            targetMode: BattleSkillTargetMode.Self,
            appliesStatuses: [new StatusApplicationDefinition("status.fortify", chancePercent: 100, targetMode: StatusApplicationTarget.Self)]);

        InMemoryGameDefinitionCatalog defs = new InMemoryGameDefinitionCatalog()
            .AddCurrency(new CurrencyDefinition("currency.gold", 999))
            .AddStatusEffect(fortify);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.fortify"],
                extraSkills: [fortifySkill]),
            CreateContext(sink, defs)).IsSuccess);

        int hpBefore = gameState.ActiveBattle!.Actors["party.hero"].Hp;

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.fortify", "party.hero"),
            CreateContext(sink, defs)).IsSuccess);

        Assert.Equal(hpBefore, gameState.ActiveBattle!.Actors["party.hero"].Hp);
        Assert.Contains(sink.Events, e => e is StatusAppliedEvent s && s.ActorId == "party.hero" && s.StatusId == "status.fortify");
        Assert.DoesNotContain(sink.Events, e => e is BattleActionResolvedEvent r && r.SkillId == "skill.fortify");
    }

    [Fact]
    public void Debuff_Applies_Status_To_Enemy_With_No_Hp_Change()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        StatusEffectDefinition slow = new(
            id: "status.slow",
            displayName: "Slow",
            durationTurns: 2,
            tickHpDelta: 0,
            preventsAction: false,
            statModifiers: new Dictionary<string, int>(System.StringComparer.Ordinal) { ["atk"] = -3 },
            stackPolicy: StatusStackPolicy.RefreshDuration);

        BattleSkillDefinition slowSkill = new(
            "skill.slow",
            BattleSkillEffectType.Debuff,
            power: 0,
            targetMode: BattleSkillTargetMode.Single,
            appliesStatuses: [new StatusApplicationDefinition("status.slow", chancePercent: 100)]);

        InMemoryGameDefinitionCatalog defs = new InMemoryGameDefinitionCatalog()
            .AddCurrency(new CurrencyDefinition("currency.gold", 999))
            .AddStatusEffect(slow);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(
                heroSkills: ["skill.slow"],
                extraSkills: [slowSkill]),
            CreateContext(sink, defs)).IsSuccess);

        int hpBefore = gameState.ActiveBattle!.Actors["enemy.goblin"].Hp;

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.slow", "enemy.goblin"),
            CreateContext(sink, defs)).IsSuccess);

        Assert.Equal(hpBefore, gameState.ActiveBattle!.Actors["enemy.goblin"].Hp);
        Assert.Contains(sink.Events, e => e is StatusAppliedEvent s && s.ActorId == "enemy.goblin" && s.StatusId == "status.slow");
    }

    [Fact]
    public void Crit_Chance_100_Always_Crits_And_Multiplies_Damage()
    {
        int noCritDamage = RunSingleHit(critChance: 0, critMultiplier: 200, out _);
        int alwaysCritDamage = RunSingleHit(critChance: 100, critMultiplier: 200, out BattleActionResolvedEvent critEvent);

        Assert.True(critEvent.WasCritical);
        // 2× multiplier should at least roughly double the deterministic baseline.
        // (No variance configured, so the only difference is the crit multiplier.)
        Assert.Equal(noCritDamage * 2, alwaysCritDamage);
    }

    [Fact]
    public void Crit_Chance_0_Never_Crits()
    {
        RunSingleHit(critChance: 0, critMultiplier: 200, out BattleActionResolvedEvent ev);
        Assert.False(ev.WasCritical);
    }

    [Fact]
    public void Heal_Never_Crits_Even_With_High_Crit_Chance()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition healCrit = new(
            "skill.miraculous",
            BattleSkillEffectType.Heal,
            power: 8,
            critChancePercent: 100);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(heroSkills: ["skill.miraculous"], extraSkills: [healCrit]),
            CreateContext(sink)).IsSuccess);

        gameState.ActiveBattle!.Actors["party.hero"].Hp = 5;

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.miraculous", "party.hero"),
            CreateContext(sink)).IsSuccess);

        BattleActionResolvedEvent heal = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Single(e => e.SkillId == "skill.miraculous");

        Assert.True(heal.WasHeal);
        Assert.False(heal.WasCritical);
    }

    [Fact]
    public void Crit_Does_Not_Bypass_Immunity()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        InMemoryGameDefinitionCatalog defs = new InMemoryGameDefinitionCatalog()
            .AddCurrency(new CurrencyDefinition("currency.gold", 999))
            .AddDamageType(new DamageTypeDefinition(
                "damage.frost",
                attackStatId: "matk",
                flatDefenseStatId: null,
                resistanceStatId: "res.frost"));

        BattleSkillDefinition icicle = new(
            "skill.icicle",
            BattleSkillEffectType.MagicalDamage,
            power: 99,
            damageTypeId: "damage.frost",
            critChancePercent: 100);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(heroSkills: ["skill.icicle"], extraSkills: [icicle]),
            CreateContext(sink, defs)).IsSuccess);

        // Make the goblin frost-immune.
        gameState.ActorStatsState.GetOrCreate("enemy.goblin").SetBase("res.frost", 100);

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.icicle", "enemy.goblin"),
            CreateContext(sink, defs)).IsSuccess);

        BattleActionResolvedEvent hit = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Single(e => e.SkillId == "skill.icicle");

        Assert.Equal(0, hit.Amount);
        Assert.False(hit.WasCritical); // immune target → crit roll skipped (no damage to multiply)
    }

    [Fact]
    public void Ai_Skips_Skill_That_Is_Currently_On_Cooldown()
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        // Bite has a 3-turn cooldown; Tackle is the always-available fallback.
        BattleSkillDefinition bite = new(
            "skill.bite",
            BattleSkillEffectType.PhysicalDamage,
            power: 20,
            cooldownTurns: 3);
        BattleSkillDefinition tackle = new(
            "skill.tackle",
            BattleSkillEffectType.PhysicalDamage,
            power: 3);

        BattleAiPolicyDefinition wolfAi = new(
            rules:
            [
                new BattleAiRuleDefinition(
                    skillId: "skill.bite",
                    priorityWeight: 100,
                    targetPolicy: BattleAiTargetPolicy.LowestHpEnemy,
                    conditions: [])
            ],
            fallbackSkillId: "skill.tackle",
            fallbackTargetPolicy: BattleAiTargetPolicy.LowestHpEnemy);

        List<BattleActorDefinition> actors =
        [
            new BattleActorDefinition(
                actorId: "party.hero",
                displayName: "Hero",
                faction: CombatFaction.Party,
                maxHp: 100,
                atk: 5,
                def: 0,
                matk: 0,
                mdef: 0,
                initiative: 5,
                skillIds: ["skill.tackle"],
                playerControlled: true),
            new BattleActorDefinition(
                actorId: "enemy.wolf",
                displayName: "Wolf",
                faction: CombatFaction.Enemy,
                maxHp: 100,
                atk: 0,
                def: 0,
                matk: 0,
                mdef: 0,
                initiative: 20,
                skillIds: ["skill.bite", "skill.tackle"],
                playerControlled: false,
                aiPolicy: wolfAi)
        ];

        Assert.True(dispatcher.Dispatch(
            gameState,
            new StartBattleCommand("battle.cd", actors, [bite, tackle], seed: 1, sequence: 1),
            CreateContext(sink)).IsSuccess);

        // Wolf goes first (higher initiative). Cast 1: should pick Bite.
        Assert.True(dispatcher.Dispatch(gameState, new ExecuteAiTurnCommand(), CreateContext(sink)).IsSuccess);

        // Hero burns a turn so the wolf comes up again.
        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.tackle", "enemy.wolf"),
            CreateContext(sink)).IsSuccess);

        // Cast 2: Bite is on cooldown — must fall through to Tackle without erroring.
        Assert.True(dispatcher.Dispatch(gameState, new ExecuteAiTurnCommand(), CreateContext(sink)).IsSuccess);

        List<BattleActionResolvedEvent> wolfHits = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Where(e => e.ActorId == "enemy.wolf")
            .ToList();

        Assert.Equal(2, wolfHits.Count);
        Assert.Equal("skill.bite", wolfHits[0].SkillId);
        Assert.Equal("skill.tackle", wolfHits[1].SkillId);
    }

    private static int RunSingleHit(int critChance, int critMultiplier, out BattleActionResolvedEvent hit)
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition strike = new(
            "skill.crit_test",
            BattleSkillEffectType.PhysicalDamage,
            power: 10,
            critChancePercent: critChance,
            critMultiplierPercent: critMultiplier);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(heroSkills: ["skill.crit_test"], extraSkills: [strike]),
            CreateContext(sink)).IsSuccess);

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.crit_test", "enemy.goblin"),
            CreateContext(sink)).IsSuccess);

        hit = sink.Events.OfType<BattleActionResolvedEvent>().Single(e => e.SkillId == "skill.crit_test");
        return hit.Amount;
    }

    private static int RunSingleHitWithVariance(ulong seed, int variancePercent)
    {
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = CreateDispatcher();

        BattleSkillDefinition strike = new(
            "skill.strike",
            BattleSkillEffectType.PhysicalDamage,
            power: 20,
            damageVariancePercent: variancePercent);

        Assert.True(dispatcher.Dispatch(
            gameState,
            BuildBattle(heroSkills: ["skill.strike"], extraSkills: [strike], seed: seed),
            CreateContext(sink)).IsSuccess);

        Assert.True(dispatcher.Dispatch(
            gameState,
            new UseBattleSkillCommand("party.hero", "skill.strike", "enemy.goblin"),
            CreateContext(sink)).IsSuccess);

        BattleActionResolvedEvent hit = sink.Events
            .OfType<BattleActionResolvedEvent>()
            .Single(e => e.SkillId == "skill.strike");
        return hit.Amount;
    }

    private static StartBattleCommand BuildBattle(
        IReadOnlyList<string> heroSkills,
        IReadOnlyList<BattleSkillDefinition> extraSkills,
        ulong seed = 42)
    {
        List<BattleSkillDefinition> skills = new(extraSkills);

        List<BattleActorDefinition> actors =
        [
            new BattleActorDefinition(
                actorId: "party.hero",
                displayName: "Hero",
                faction: CombatFaction.Party,
                maxHp: 35,
                atk: 12,
                def: 5,
                matk: 8,
                mdef: 3,
                initiative: 30,
                skillIds: heroSkills,
                playerControlled: true),
            new BattleActorDefinition(
                actorId: "party.cleric",
                displayName: "Cleric",
                faction: CombatFaction.Party,
                maxHp: 28,
                atk: 4,
                def: 4,
                matk: 10,
                mdef: 6,
                initiative: 20,
                skillIds: ["skill.attack"],
                playerControlled: true),
            new BattleActorDefinition(
                actorId: "enemy.goblin",
                displayName: "Goblin",
                faction: CombatFaction.Enemy,
                maxHp: 20,
                atk: 7,
                def: 2,
                matk: 3,
                mdef: 1,
                initiative: 15,
                skillIds: ["skill.attack"]),
            new BattleActorDefinition(
                actorId: "enemy.shaman",
                displayName: "Shaman",
                faction: CombatFaction.Enemy,
                maxHp: 24,
                atk: 5,
                def: 3,
                matk: 9,
                mdef: 4,
                initiative: 10,
                skillIds: ["skill.attack"])
        ];

        // Make sure every actor's known skills exist in the battle's skill list.
        bool hasAttack = skills.Any(s => s.Id == "skill.attack");
        if (!hasAttack)
        {
            skills.Add(new BattleSkillDefinition("skill.attack", BattleSkillEffectType.PhysicalDamage, 5));
        }

        return new StartBattleCommand("battle.aoe.test", actors, skills, seed: seed, sequence: 11);
    }

    private static CommandDispatcher CreateDispatcher()
    {
        CommandDispatcher dispatcher = new();
        dispatcher.Register(new StartBattleCommandHandler());
        dispatcher.Register(new UseBattleSkillCommandHandler());
        dispatcher.Register(new ExecuteAiTurnCommandHandler());
        return dispatcher;
    }

    private static CommandContext CreateContext(
        InMemoryDomainEventSink sink,
        IGameDefinitionCatalog? definitions = null)
    {
        return new CommandContext(
            new Pcg32RandomSource(seed: 9999, sequence: 54),
            new SimulationClock(0),
            new NoOpFormulaEvaluator(),
            sink,
            definitions ?? new InMemoryGameDefinitionCatalog().AddCurrency(new CurrencyDefinition("currency.gold", 999)));
    }
}
