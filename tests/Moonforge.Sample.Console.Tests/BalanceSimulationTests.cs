using System.Text;
using Moonforge.Core;
using Moonforge.Core.Combat;
using Moonforge.Core.Combat.Commands;
using Moonforge.Core.Combat.Events;
using Moonforge.Core.Combat.Queries;
using Moonforge.Core.Data.Definitions;
using Moonforge.Core.Equipment;
using Moonforge.Core.Equipment.Commands;
using Moonforge.Core.Inventory.Commands;
using Moonforge.Core.Inventory.Queries;
using Moonforge.Core.Progression;
using Moonforge.Core.Progression.Commands;
using Moonforge.Core.Runtime.Commands;
using Moonforge.Core.Runtime.Events;
using Moonforge.Core.Runtime.Random;
using Moonforge.Core.Runtime.Results;
using Moonforge.Core.Runtime.Time;
using Moonforge.Core.Stats;
using Moonforge.Sample.ConsoleApp.GameLoop;
using Moonforge.Sample.ConsoleApp.WorldGen;
using Xunit.Abstractions;

namespace Moonforge.Sample.Console.Tests;

public sealed class BalanceSimulationTests
{
    private readonly ITestOutputHelper _output;

    private const string HeroId = "party.hero";
    private const string PotionItem = "item.potion.medium";
    private const string GoldCurrency = "currency.gold";
    private const string HeroCurve = "curve.hero";
    private const string FocusResource = "focus";
    private const string SlotWeapon = "slot.weapon";
    private const string SlotArmor = "slot.armor";
    private const string SlotAccessory = "slot.accessory";
    private const string BronzeBlade = "item.gear.bronze_blade";
    private const string LeatherVest = "item.gear.leather_vest";
    private const string IronRing = "item.gear.iron_ring";
    private const string ShieldBash = "skill.knight.shieldbash";
    private const string GuardStance = "skill.knight.guardstance";
    private const int StartingPotions = 8;
    private const int MaxFocus = 3;
    private const int MaxFloor = 12;
    private const int NormalEncountersPerFloor = 4;

    private static readonly BattleSkillDefinition ShieldBashSkill = new(
        ShieldBash,
        BattleSkillEffectType.PhysicalDamage,
        power: 12,
        cooldownTurns: 2,
        resourceCosts: new Dictionary<string, int> { [FocusResource] = 1 },
        displayName: "Shield Bash");

    private static readonly BattleSkillDefinition GuardStanceSkill = new(
        GuardStance,
        BattleSkillEffectType.Heal,
        power: 9,
        cooldownTurns: 3,
        resourceCosts: new Dictionary<string, int> { [FocusResource] = 2 },
        displayName: "Guard Stance");

    private sealed record HeroConfig(bool UseGear, bool UseAbilities);
    private static readonly HeroConfig Bare = new(false, false);
    private static readonly HeroConfig Geared = new(true, true);

    public BalanceSimulationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Knight_Bare_Run_Distribution()
    {
        const int RunCount = 50;
        List<RunResult> results = new();
        for (int i = 0; i < RunCount; i++)
        {
            results.Add(SimulateRun(seed: (ulong)(1000 + i * 7919), Bare));
        }

        PrintSummary(results, "Bare Knight — basic attack + potions only, no gear, no abilities");

        int reachedFloor1 = results.Count(r => r.MaxFloorCleared >= 1);
        Assert.True(reachedFloor1 >= 48, $"Floor 1 should be very winnable; got {reachedFloor1}/50");
    }

    [Fact]
    public void Knight_Geared_Run_Distribution()
    {
        const int RunCount = 50;
        List<RunResult> results = new();
        for (int i = 0; i < RunCount; i++)
        {
            results.Add(SimulateRun(seed: (ulong)(1000 + i * 7919), Geared));
        }

        PrintSummary(results, "Geared Knight — Bronze Blade + Leather Vest + Iron Ring, Shield Bash + Guard Stance");

        int reachedFloor1 = results.Count(r => r.MaxFloorCleared >= 1);
        Assert.True(reachedFloor1 >= 48, $"Floor 1 should be very winnable; got {reachedFloor1}/50");
    }

    private RunResult SimulateRun(ulong seed, HeroConfig config)
    {
        InMemoryGameDefinitionCatalog catalog = BuildCatalog();
        GameState gameState = new();
        InMemoryDomainEventSink sink = new();
        CommandDispatcher dispatcher = DefaultCommandDispatcher.Create();

        IRandomSource hostRng = new Pcg32RandomSource(seed, sequence: 11);
        CommandContext context = new(
            new Pcg32RandomSource(seed, sequence: 22),
            new SimulationClock(0),
            new ExpressionFormulaEvaluator(),
            sink,
            catalog);

        // Bootstrap hero.
        Assert.True(dispatcher.Dispatch(gameState, new ConfigureActorProgressionCommand(HeroId, HeroCurve), context).IsSuccess);
        Assert.True(dispatcher.Dispatch(gameState, new ConfigureInventoryCapacityCommand(20), context).IsSuccess);
        Assert.True(dispatcher.Dispatch(gameState, new AddInventoryItemCommand(PotionItem, StartingPotions), context).IsSuccess);

        // Seed hero stat block with Knight class bases.
        StatBlock heroBlock = gameState.ActorStatsState.GetOrCreate(HeroId);
        heroBlock.SetBase(StandardStats.Vitality, 44);
        heroBlock.SetBase(StandardStats.Attack, 12);
        heroBlock.SetBase(StandardStats.Defense, 7);
        heroBlock.SetBase(StandardStats.MagicAttack, 3);
        heroBlock.SetBase(StandardStats.MagicDefense, 5);
        heroBlock.SetBase(StandardStats.Initiative, 17);

        if (config.UseGear)
        {
            // Add and equip the starter gear set.
            Assert.True(dispatcher.Dispatch(gameState, new AddInventoryItemCommand(BronzeBlade, 1), context).IsSuccess);
            Assert.True(dispatcher.Dispatch(gameState, new AddInventoryItemCommand(LeatherVest, 1), context).IsSuccess);
            Assert.True(dispatcher.Dispatch(gameState, new AddInventoryItemCommand(IronRing, 1), context).IsSuccess);
            Assert.True(dispatcher.Dispatch(gameState, new EquipItemCommand(BronzeBlade, HeroId), context).IsSuccess);
            Assert.True(dispatcher.Dispatch(gameState, new EquipItemCommand(LeatherVest, HeroId), context).IsSuccess);
            Assert.True(dispatcher.Dispatch(gameState, new EquipItemCommand(IronRing, HeroId), context).IsSuccess);
        }

        RunResult result = new();
        bool died = false;

        for (int floor = 1; floor <= MaxFloor && !died; floor++)
        {
            FloorMetrics floorMetrics = new() { Floor = floor };
            int heroLevelAtStart = gameState.ProgressionState.TryGet(HeroId, out ActorProgression p) ? p.Level : 1;
            floorMetrics.LevelAtStart = heroLevelAtStart;
            floorMetrics.MaxHpAtStart = heroBlock.Get(StandardStats.MaxHp, catalog, context.FormulaEvaluator, new Dictionary<string, double> { ["level"] = heroLevelAtStart });
            floorMetrics.AtkAtStart = heroBlock.Get(StandardStats.Attack, catalog, context.FormulaEvaluator);
            floorMetrics.DefAtStart = heroBlock.Get(StandardStats.Defense, catalog, context.FormulaEvaluator);

            int battleSequence = floor * 100;

            // Normal encounters.
            for (int e = 0; e < NormalEncountersPerFloor && !died; e++)
            {
                EncounterBlueprint encounter = EncounterGenerator.Generate(floor, ++battleSequence, hostRng, catalog);
                BattleOutcome outcome = SimulateBattle(gameState, dispatcher, context, catalog, encounter, heroLevelAtStart, sink, config);
                floorMetrics.BattlesOnFloor++;
                floorMetrics.PotionsUsedOnFloor += outcome.PotionsUsed;
                died = outcome.HeroDied;
            }

            // Boss every 3 floors.
            if (!died && floor % 3 == 0)
            {
                EncounterBlueprint boss = EncounterGenerator.GenerateBoss(floor, ++battleSequence, hostRng, catalog);
                BattleOutcome outcome = SimulateBattle(gameState, dispatcher, context, catalog, boss, heroLevelAtStart, sink, config);
                floorMetrics.BattlesOnFloor++;
                floorMetrics.PotionsUsedOnFloor += outcome.PotionsUsed;
                floorMetrics.BossEncountered = true;
                floorMetrics.BossDefeated = !outcome.HeroDied;
                died = outcome.HeroDied;
            }

            floorMetrics.HeroLevelAtEnd = gameState.ProgressionState.TryGet(HeroId, out ActorProgression p2) ? p2.Level : 1;
            floorMetrics.HeroPotionsAtEnd = InventoryQty(gameState, PotionItem);
            result.PerFloor.Add(floorMetrics);

            if (!died)
            {
                result.MaxFloorCleared = floor;
            }
        }

        result.DiedOnFloor = died ? result.PerFloor[^1].Floor : -1;
        result.FinalLevel = gameState.ProgressionState.TryGet(HeroId, out ActorProgression pf) ? pf.Level : 1;
        result.FinalGold = gameState.CurrencyWallet.GetBalance(GoldCurrency);
        return result;
    }

    private BattleOutcome SimulateBattle(
        GameState gameState,
        CommandDispatcher dispatcher,
        CommandContext context,
        InMemoryGameDefinitionCatalog catalog,
        EncounterBlueprint encounter,
        int currentLevel,
        InMemoryDomainEventSink sink,
        HeroConfig config)
    {
        // Replace the encounter's stock hero with a faithful Knight stat-block-driven actor.
        BattleActorDefinition customHero = BuildKnightActor(gameState, catalog, context, currentLevel, config);
        List<BattleActorDefinition> actors = new(encounter.Actors.Count);
        actors.Add(customHero);
        foreach (BattleActorDefinition a in encounter.Actors)
        {
            if (a.ActorId != HeroId) actors.Add(a);
        }

        List<BattleSkillDefinition> battleSkills = new(encounter.Skills);
        if (config.UseAbilities)
        {
            battleSkills.Add(ShieldBashSkill);
            battleSkills.Add(GuardStanceSkill);
        }

        DomainResult startResult = dispatcher.Dispatch(
            gameState,
            new StartBattleCommand(
                battleId: encounter.IntroText.GetHashCode().ToString(),
                actors: actors,
                skills: battleSkills,
                seed: (ulong)Math.Abs(encounter.IntroText.GetHashCode()),
                sequence: 7,
                rewardCurrency: encounter.RewardCurrency,
                rewardInventory: encounter.RewardInventory,
                rewardLootTableId: null),
            context);
        Assert.True(startResult.IsSuccess, $"StartBattle failed: {startResult.Error?.Message}");

        // Apply per-encounter resistances if any.
        if (encounter.ActorResistances is not null)
        {
            foreach (var (actorId, resistances) in encounter.ActorResistances)
            {
                StatBlock block = gameState.ActorStatsState.GetOrCreate(actorId);
                foreach (var (statId, value) in resistances)
                {
                    block.SetBase(statId, value);
                }
            }
        }

        BattleOutcome outcome = new();
        GetCurrentBattleTurnActorQueryHandler turnQuery = new();
        int eventCountBefore = sink.Events.Count;
        int safety = 400;
        while (gameState.ActiveBattle is { Status: BattleStatus.Active } && safety-- > 0)
        {
            string? current = turnQuery.Query(gameState, new GetCurrentBattleTurnActorQuery());
            if (current == HeroId)
            {
                DecidePlayerAction(gameState, dispatcher, context, encounter, outcome, config);
            }
            else
            {
                Assert.True(dispatcher.Dispatch(gameState, new ExecuteAiTurnCommand(), context).IsSuccess);
            }
        }

        BattleEndedEvent? ended = sink.Events
            .Skip(eventCountBefore)
            .OfType<BattleEndedEvent>()
            .LastOrDefault();
        outcome.HeroDied = ended?.Status == BattleStatus.Defeat;

        // Clear lingering battle reference so the next StartBattle accepts.
        gameState.ActiveBattle = null;
        return outcome;
    }

    private void DecidePlayerAction(
        GameState gameState,
        CommandDispatcher dispatcher,
        CommandContext context,
        EncounterBlueprint encounter,
        BattleOutcome outcome,
        HeroConfig config)
    {
        BattleState battle = gameState.ActiveBattle!;
        BattleActorState hero = battle.Actors[HeroId];
        double hpFraction = hero.MaxHp == 0 ? 0 : (double)hero.Hp / hero.MaxHp;
        int potions = InventoryQty(gameState, PotionItem);
        int focus = hero.Resources.TryGetValue(FocusResource, out int f) ? f : 0;

        // Priority 1 (only if abilities available): Guard Stance heal when wounded + focus available.
        if (config.UseAbilities
            && hpFraction < 0.45
            && focus >= 2
            && !IsOnCooldown(hero, GuardStance))
        {
            Assert.True(dispatcher.Dispatch(gameState, new UseBattleSkillCommand(HeroId, GuardStance, HeroId), context).IsSuccess);
            return;
        }

        // Priority 2: potion when seriously hurt.
        if (hpFraction < 0.35 && potions > 0)
        {
            DomainResult consume = dispatcher.Dispatch(gameState, new ConsumeInventoryItemCommand(PotionItem, 1), context);
            if (consume.IsSuccess)
            {
                outcome.PotionsUsed++;
                Assert.True(dispatcher.Dispatch(gameState, new UseBattleSkillCommand(HeroId, "skill.potion", HeroId), context).IsSuccess);
                return;
            }
        }

        BattleActorState? target = battle.Actors.Values
            .Where(a => a.Faction != hero.Faction && !a.IsDowned)
            .OrderBy(a => a.Hp)
            .ThenBy(a => a.ActorId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (target is null) return;

        // Priority 3 (only if abilities available): Shield Bash on enemy.
        if (config.UseAbilities && focus >= 1 && !IsOnCooldown(hero, ShieldBash))
        {
            DomainResult sb = dispatcher.Dispatch(gameState, new UseBattleSkillCommand(HeroId, ShieldBash, target.ActorId), context);
            if (sb.IsSuccess) return;
        }

        // Fallback: basic attack.
        DomainResult attackResult = dispatcher.Dispatch(gameState, new UseBattleSkillCommand(HeroId, "skill.attack", target.ActorId), context);
        if (!attackResult.IsSuccess)
        {
            string knownSkills = string.Join(",", hero.SkillIds);
            string availableSkills = string.Join(",", battle.Skills.Keys);
            Assert.Fail($"Hero attack failed: {attackResult.Error?.Code} {attackResult.Error?.Message}. Hero knows: [{knownSkills}]. Battle has: [{availableSkills}].");
        }
    }

    private static bool IsOnCooldown(BattleActorState actor, string skillId)
    {
        return actor.Cooldowns.TryGetValue(skillId, out int remaining) && remaining > 0;
    }

    private BattleActorDefinition BuildKnightActor(
        GameState gameState,
        InMemoryGameDefinitionCatalog catalog,
        CommandContext context,
        int level,
        HeroConfig config)
    {
        StatBlock block = gameState.ActorStatsState.GetOrCreate(HeroId);
        var extras = new Dictionary<string, double> { ["level"] = level };
        int hp = block.Get(StandardStats.MaxHp, catalog, context.FormulaEvaluator, extras);
        int atk = block.Get(StandardStats.Attack, catalog, context.FormulaEvaluator);
        int def = block.Get(StandardStats.Defense, catalog, context.FormulaEvaluator);
        int matk = block.Get(StandardStats.MagicAttack, catalog, context.FormulaEvaluator);
        int mdef = block.Get(StandardStats.MagicDefense, catalog, context.FormulaEvaluator);
        int initiative = block.Get(StandardStats.Initiative, catalog, context.FormulaEvaluator);

        List<string> skillIds = ["skill.attack", "skill.potion"];
        Dictionary<string, int>? resourceMaxes = null;
        Dictionary<string, int>? startingResources = null;
        Dictionary<string, int>? resourceRefresh = null;

        if (config.UseAbilities)
        {
            skillIds.Add(ShieldBash);
            skillIds.Add(GuardStance);
            resourceMaxes = new Dictionary<string, int> { [FocusResource] = MaxFocus };
            startingResources = new Dictionary<string, int> { [FocusResource] = MaxFocus };
            resourceRefresh = new Dictionary<string, int> { [FocusResource] = 1 };
        }

        return new BattleActorDefinition(
            actorId: HeroId,
            displayName: "Knight",
            faction: CombatFaction.Party,
            maxHp: hp,
            atk: atk,
            def: def,
            matk: matk,
            mdef: mdef,
            initiative: initiative,
            skillIds: skillIds,
            playerControlled: true,
            resourceMaxes: resourceMaxes,
            startingResources: startingResources,
            resourceRefreshPerTurn: resourceRefresh);
    }

    private InMemoryGameDefinitionCatalog BuildCatalog()
    {
        InMemoryGameDefinitionCatalog catalog = new();
        catalog
            .AddCurrency(new CurrencyDefinition(GoldCurrency, 999_999))
            .AddCurrency(new CurrencyDefinition("currency.token", 999))
            .AddItem(new ItemDefinition(PotionItem, stackLimit: 20))
            .AddStat(new StatDefinition(StandardStats.Vitality, displayName: "Vitality"))
            .AddStat(new StatDefinition(
                StandardStats.MaxHp,
                min: 1,
                derivedFromFormula: "vit + (level - 1) * 4",
                displayName: "Max HP"))
            .AddDamageType(new DamageTypeDefinition(
                StandardDamageTypes.Physical,
                attackStatId: StandardStats.Attack,
                flatDefenseStatId: StandardStats.Defense,
                resistanceStatId: StandardStats.ResistancePhysical))
            .AddDamageType(new DamageTypeDefinition(
                StandardDamageTypes.Magical,
                attackStatId: StandardStats.MagicAttack,
                flatDefenseStatId: StandardStats.MagicDefense,
                resistanceStatId: StandardStats.ResistanceMagical))
            .AddDamageType(new DamageTypeDefinition(
                StandardDamageTypes.Fire,
                attackStatId: StandardStats.MagicAttack,
                flatDefenseStatId: null,
                resistanceStatId: StandardStats.ResistanceFire))
            .AddDamageType(new DamageTypeDefinition(
                StandardDamageTypes.Ice,
                attackStatId: StandardStats.MagicAttack,
                flatDefenseStatId: null,
                resistanceStatId: StandardStats.ResistanceIce))
            .AddStatusEffect(new StatusEffectDefinition(
                id: "status.poison",
                durationTurns: 3,
                tickHpDelta: -2,
                displayName: "Poison"))
            .AddStatusEffect(new StatusEffectDefinition(
                id: "status.wreath_of_flame",
                durationTurns: 4,
                statModifiers: new Dictionary<string, int> { ["matk"] = 5 },
                displayName: "Wreath of Flame"))
            .AddStatusEffect(new StatusEffectDefinition(
                id: "status.cinderbrand",
                durationTurns: 3,
                statModifiers: new Dictionary<string, int> { ["def"] = -3 },
                displayName: "Cinderbrand"))
            .AddExperienceCurve(new ExperienceCurveDefinition(
                id: HeroCurve,
                xpThresholds: new long[] { 20, 60, 120, 200, 320, 480, 700, 1000, 1400, 1900 },
                displayName: "Hero Curve",
                statGainsPerLevel: new Dictionary<string, int>
                {
                    [StandardStats.Vitality] = 1,
                    [StandardStats.Attack] = 1,
                    [StandardStats.Defense] = 1
                }))
            // Equipment slots and starter gear definitions. Used only when HeroConfig.UseGear is true.
            .AddEquipmentSlot(new EquipmentSlotDefinition(SlotWeapon, "Weapon"))
            .AddEquipmentSlot(new EquipmentSlotDefinition(SlotArmor, "Armor"))
            .AddEquipmentSlot(new EquipmentSlotDefinition(SlotAccessory, "Accessory"))
            .AddItem(new ItemDefinition(BronzeBlade, stackLimit: 1))
            .AddItem(new ItemDefinition(LeatherVest, stackLimit: 1))
            .AddItem(new ItemDefinition(IronRing, stackLimit: 1))
            .AddEquipment(new EquipmentDefinition(
                BronzeBlade,
                SlotWeapon,
                statBonuses: new Dictionary<string, int> { [StandardEquipmentStats.Attack] = 2 },
                displayName: "Bronze Blade"))
            .AddEquipment(new EquipmentDefinition(
                LeatherVest,
                SlotArmor,
                statBonuses: new Dictionary<string, int> { [StandardEquipmentStats.Defense] = 2 },
                displayName: "Leather Vest"))
            .AddEquipment(new EquipmentDefinition(
                IronRing,
                SlotAccessory,
                statBonuses: new Dictionary<string, int>
                {
                    [StandardEquipmentStats.Attack] = 1,
                    [StandardEquipmentStats.Defense] = 1,
                    [StandardEquipmentStats.CritChance] = 5
                },
                displayName: "Iron Ring"));

        EncounterGenerator.RegisterEncounterTables(catalog);
        return catalog;
    }

    private static int InventoryQty(GameState gameState, string itemId)
    {
        return new GetInventoryItemQuantityQueryHandler().Query(gameState, new GetInventoryItemQuantityQuery(itemId));
    }

    private void PrintSummary(List<RunResult> results, string label)
    {
        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine($"=== {label} ===");
        sb.AppendLine($"=== {results.Count} runs, {StartingPotions} starting potions ===");
        sb.AppendLine();

        // Overall reach distribution.
        sb.AppendLine("Floor reach distribution:");
        for (int f = 1; f <= MaxFloor; f++)
        {
            int reached = results.Count(r => r.MaxFloorCleared >= f);
            string bar = new('█', reached);
            sb.AppendLine($"  Floor {f,2}: {reached,2}/{results.Count} {bar}");
        }

        // Per-floor metrics, averaged across runs that reached that floor.
        sb.AppendLine();
        sb.AppendLine("Per-floor averages (runs that started the floor):");
        sb.AppendLine($"  {"Flr",3} {"Runs",4} {"Lvl",3} {"MaxHp",5} {"Atk",3} {"Def",3} {"PotUsed",7} {"BossKill",8}");
        for (int f = 1; f <= MaxFloor; f++)
        {
            var floorRuns = results
                .SelectMany(r => r.PerFloor)
                .Where(fm => fm.Floor == f)
                .ToList();

            if (floorRuns.Count == 0) continue;

            int avgLevel = (int)Math.Round(floorRuns.Average(x => x.LevelAtStart));
            int avgMaxHp = (int)Math.Round(floorRuns.Average(x => x.MaxHpAtStart));
            int avgAtk = (int)Math.Round(floorRuns.Average(x => x.AtkAtStart));
            int avgDef = (int)Math.Round(floorRuns.Average(x => x.DefAtStart));
            double avgPotUsed = floorRuns.Average(x => x.PotionsUsedOnFloor);
            int bossRuns = floorRuns.Count(x => x.BossEncountered);
            int bossKills = floorRuns.Count(x => x.BossDefeated);
            string bossInfo = bossRuns > 0 ? $"{bossKills}/{bossRuns}" : "-";

            sb.AppendLine($"  {f,3} {floorRuns.Count,4} {avgLevel,3} {avgMaxHp,5} {avgAtk,3} {avgDef,3} {avgPotUsed,7:F1} {bossInfo,8}");
        }

        // Death-floor distribution.
        sb.AppendLine();
        var deaths = results.Where(r => r.DiedOnFloor > 0).GroupBy(r => r.DiedOnFloor);
        sb.AppendLine("Deaths by floor:");
        foreach (var g in deaths.OrderBy(x => x.Key))
        {
            sb.AppendLine($"  Floor {g.Key,2}: {g.Count()}");
        }

        int survivors = results.Count(r => r.MaxFloorCleared >= MaxFloor);
        sb.AppendLine();
        sb.AppendLine($"Runs that cleared floor {MaxFloor}: {survivors}/{results.Count}");

        _output.WriteLine(sb.ToString());
    }

    private sealed class RunResult
    {
        public int MaxFloorCleared { get; set; }
        public int DiedOnFloor { get; set; }
        public int FinalLevel { get; set; }
        public long FinalGold { get; set; }
        public List<FloorMetrics> PerFloor { get; } = new();
    }

    private sealed class FloorMetrics
    {
        public int Floor { get; set; }
        public int LevelAtStart { get; set; }
        public int MaxHpAtStart { get; set; }
        public int AtkAtStart { get; set; }
        public int DefAtStart { get; set; }
        public int HeroLevelAtEnd { get; set; }
        public int HeroPotionsAtEnd { get; set; }
        public int BattlesOnFloor { get; set; }
        public int PotionsUsedOnFloor { get; set; }
        public bool BossEncountered { get; set; }
        public bool BossDefeated { get; set; }
    }

    private sealed class BattleOutcome
    {
        public bool HeroDied { get; set; }
        public int PotionsUsed { get; set; }
    }
}
