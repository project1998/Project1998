using Shared;

namespace Server;

/// <summary>
/// The API an NPC behaviour (an <see cref="INpcAbility"/> or a unique script) uses to talk to the player.
/// It's a thin, awaitable facade over the owning <see cref="Session"/>'s dialog primitives, bound to one
/// NPC, so ability code reads as linear script — <c>var c = await ctx.Menu(...); if (c == 1) ...</c> — with
/// no packet or continuation plumbing. One context is created per click (<see cref="Session.RunNpcAsync"/>).
/// </summary>
public sealed class NpcContext
{
    private readonly Session _s;
    private readonly Mob _npc;

    /// <summary>The NPC's static definition (name, identifier, shop/bank flags, …).</summary>
    public NpcDef Def { get; }

    internal NpcContext(Session s, Mob npc, NpcDef def) { _s = s; _npc = npc; Def = def; }

    /// <summary>Show a prompt + picker buttons; returns the 1-based pick (0 = the player cancelled).</summary>
    public Task<int> Menu(string prompt, IReadOnlyList<string> options) => _s.DlgMenu(_npc, prompt, options);

    /// <summary>Show one or more text pages (the player clicks through) with the NPC's own portrait, waiting
    /// for the last to close (RTK dialogSeq with the npc graphic).</summary>
    public Task Say(params string[] pages) => _s.DlgSayNpc(_npc, pages);

    /// <summary>Like <see cref="Say"/> but drawn with a creature-look portrait (RTK convertGraphic(look,"monster")).</summary>
    public Task SayLook(int look, int color, params string[] pages) => _s.DlgSayLook(_npc, look, color, pages);

    /// <summary>Like <see cref="Say"/> but drawn with an item-icon portrait (RTK Item(key).icon).</summary>
    public Task SayItem(string itemKey, params string[] pages) => _s.DlgSayItem(_npc, itemKey, pages);

    // ---- karma (Server/Karma.cs; RTK player.karma + Tools.checkKarma) ----

    /// <summary>The raw karma score. Fractional — see <see cref="Karma"/>.</summary>
    public double KarmaValue => _s.CharKarma;

    /// <summary>The karma tier as a display name ("Cat", "Tiger", …), for dialog that names it.</summary>
    public string KarmaLevel() => Karma.LevelName(_s.CharKarma);

    /// <summary>Does this player meet a named karma tier? The form every RTK gate is written in.</summary>
    public bool KarmaCheck(string tier) => Karma.Meets(_s.CharKarma, tier);

    /// <summary>Award karma (RTK addKarma) — sparkle + "Your karma has risen."</summary>
    public void AddKarma(double amount) => _s.AddKarma(amount);

    /// <summary>Dock karma (RTK removeKarma).</summary>
    public void RemoveKarma(double amount) => _s.RemoveKarma(amount);

    /// <summary>RTK Tools.checkKarma: true (and the player is told "Go away scum!") if they are below the
    /// scum floor. Scripts read it as an early return at the top of a handler.</summary>
    public bool KarmaTooLow() => _s.KarmaTooLow();

    /// <summary>Run the NPC's buy flow (its <see cref="Shops"/> catalogue, resolved by identifier).</summary>
    public Task Buy() => _s.DlgBuy(_npc, Shops.For(Def.Key));

    /// <summary>Run the sell flow: the player's droppable, sellable inventory, narrowed to what this NPC
    /// actually buys (<see cref="Shops.BuysFrom"/> — null there means it takes anything, as before).</summary>
    public Task Sell() => _s.DlgSell(_npc, Shops.BuysFrom(Def.Key));

    /// <summary>Vault: put coin in. Each is its own top-level menu entry — 4.95 has no combined "Banking"
    /// submenu (that's a later-client thing RTK's inn_npc.lua shows).</summary>
    public Task DepositMoney() => _s.BankDepositMoney(_npc);
    /// <summary>Vault: put an item in (picked from the pack).</summary>
    public Task DepositItem()  => _s.BankDepositItem(_npc);
    /// <summary>Vault: take an item back out (picked from what's stored).</summary>
    public Task WithdrawItem() => _s.BankWithdrawItem(_npc);

    /// <summary>Does the player have any parcel waiting here (gates the "Receive Parcel" menu entry)?</summary>
    public bool HasParcels => _s.HasWaitingParcels;
    /// <summary>Run the send-a-parcel flow: gold or an item, to a named recipient (RTK sendParcelTo).</summary>
    public Task SendParcel() => _s.ParcelSendFlow(_npc);
    /// <summary>Run the collect-your-parcels flow (RTK receiveParcelFrom).</summary>
    public Task ReceiveParcel() => _s.ParcelReceiveFlow(_npc);

    // The three spoken shortcuts below are synchronous on Session (no dialog round trip — they act and
    // bubble); the Task<bool> shape is kept here because the script-facing API awaits every NPC verb alike.
    /// <summary>Spoken "buy [my] [all|N] &lt;item&gt;" shortcut: sell `amount` (or the whole stack, if &lt;= 0)
    /// of a fuzzy-matched item by name. False if nothing in the bag matched the name, so the speech falls
    /// through instead of being silently swallowed.</summary>
    public Task<bool> SellByName(string name, int amount) =>
        Task.FromResult(_s.SellItemToNpcByName(_npc, name, amount, Shops.BuysFrom(Def.Key)));

    /// <summary>Spoken "take my &lt;item|coin&gt; [count]" shortcut: deposit `amount` (or the whole stack, if
    /// &lt;= 0) of a fuzzy-matched item — or coin, if the word is "coin"/"coins" — into the vault.</summary>
    public Task<bool> Deposit(string item, int amount) => Task.FromResult(_s.DepositItemToBank(_npc, item, amount));

    /// <summary>Spoken "give my &lt;item|coin&gt; [count]" shortcut: withdraw `amount` (or the whole stack, if
    /// &lt;= 0) of a fuzzy-matched item — or coin, if the word is "coin"/"coins" — from the vault.</summary>
    public Task<bool> Withdraw(string item, int amount) => Task.FromResult(_s.WithdrawItemFromBank(_npc, item, amount));

    /// <summary>Spoken "i buy [all] &lt;item&gt; [number N]" shortcut: buy `amount` of an item this NPC stocks
    /// (or as many as gold/pack allow, if &lt;= 0). False if this NPC doesn't sell it, so the speech falls
    /// through to normal chat instead of being swallowed.</summary>
    public Task<bool> BuyByName(string name, int amount) => _s.BuyItemFromNpcByName(_npc, name, amount);

    /// <summary>Spoken "what have i deposited?": bubble the vault's coin + item contents out loud.</summary>
    public void ShowVault() => _s.ShowBankContents(_npc);

    // ---- quest helpers (used by QuestDef.Talk scripts; see Server/Quests.cs) ---------------------
    /// <summary>This player's stage for a quest (0 = not started; a quest defines the rest).</summary>
    public int  Stage(string questKey) => _s.QuestStage(questKey);
    /// <summary>Set this player's stage for a quest (persists).</summary>
    public void SetStage(string questKey, int stage) => _s.SetQuestStage(questKey, stage);
    /// <summary>A quest progress counter (e.g. "trial_of_iron.kills"); 0 if unset.</summary>
    public int  Counter(string counterKey) => _s.QuestCounter(counterKey);

    /// <summary>Award experience (updates the HUD + persists).</summary>
    /// <summary><paramref name="totemTime"/> opts the grant into the +5% totem-time bonus, which quest
    /// rewards normally do NOT take (see <see cref="Session.AwardExp"/>). Only the Old dog's Restore reward
    /// asks for it, because its archived page states the bonused figure alongside the base one.</summary>
    public void AwardExp(uint amount, bool totemTime = false) => _s.AwardExp(amount, killExp: totemTime);
    /// <summary>Award coin (updates the HUD + persists).</summary>
    public void AwardGold(uint amount) => _s.AwardGold(amount);

    /// <summary>How many of an item (by content key) the player holds.</summary>
    public int  CountItem(string itemKey) => _s.CountItem(itemKey);
    /// <summary>Consume <paramref name="amount"/> of an item by key; false if the player hasn't that many.</summary>
    public bool TakeItem(string itemKey, int amount) => _s.TakeItem(itemKey, amount);
    /// <summary>Give a reward item by key; false if the item is unknown or the pack is full.</summary>
    public bool GiveItem(string itemKey, int amount = 1) => _s.GiveRewardItem(itemKey, amount);

    /// <summary>How many of an item the player could sacrifice under the armor-quest rule — bag AND worn
    /// slots, full durability only ("must be 100% and can be worn at the time or in your inventory").</summary>
    public int  CountReady(string itemKey) => _s.CountReady(itemKey);
    /// <summary>Take <paramref name="amount"/> under that same rule (bag first, then off the body). False and
    /// nothing taken if the player is short. See <see cref="Session.TakeReady"/>.</summary>
    public bool TakeReady(string itemKey, int amount) => _s.TakeReady(itemKey, amount);

    /// <summary>Lifetime kills for a mob key (RTK <c>player:killCount</c>). Quests compare a snapshot delta.</summary>
    public int  KillCount(string mobKey) => _s.KillCount(mobKey);
    /// <summary>Lifetime kills of ANY mob, for a quest that cares what ELSE you killed (the Old dog's
    /// "do NOT kill anything else along the way"). Compare a snapshot delta, same as KillCount.</summary>
    public int  TotalKills => _s.TotalKills;

    /// <summary>An int-valued quest registry entry (RTK registry), 0 if unset. General store for quest
    /// bookkeeping (counters, snapshots, timers) — distinct from <see cref="Stage"/>'s quest-stage meaning.</summary>
    public int  Reg(string key) => _s.QuestCounter(key);
    public void SetReg(string key, int value) => _s.SetQuestStage(key, value);

    /// <summary>A string-valued quest registry entry (RTK registryString), "" if unset.</summary>
    public string QuestStr(string key) => _s.QuestStr(key);
    public void   SetQuestStr(string key, string value) => _s.SetQuestStr(key, value);

    /// <summary>Does the player have the legend with this internal name?</summary>
    public bool HasLegend(string name) => _s.HasLegend(name);
    /// <summary>Add (or replace by name) a legend mark.</summary>
    public void AddLegend(string text, string name, byte icon, byte color) => _s.AddLegend(text, name, icon, color);
    /// <summary>Remove the legend with this internal name.</summary>
    public void RemoveLegend(string name) => _s.RemoveLegend(name);

    /// <summary>The player's level.</summary>
    public int  Level => _s.CharLevel;
    /// <summary>The "power" number quests gate on (RTK baseMagic*2 + baseHealth analog).</summary>
    public int  Stat  => _s.CharStat;
    /// <summary>Subpath mark count (0 for now).</summary>
    public int  Mark  => _s.CharMark;
    /// <summary>The base class as RTK's <c>player.baseClass</c>: 1 Warrior · 2 Rogue · 3 Mage · 4 Poet (0 for
    /// Peasant/unknown). Subpaths collapse onto their base — a Chung Ryong reads 1, same as a Warrior. Used by
    /// <see cref="BonHwaAbility"/> to pick the class weapon ladder and the per-class stat caps.</summary>
    public int  BaseClass => _s.CharBasePathId;
    /// <summary>Random int in [1, maxInclusive].</summary>
    public int  Random(int maxInclusive) => _s.QuestRandom(maxInclusive);
    /// <summary>Wall-clock seconds since the Unix epoch (for cooldown timers).</summary>
    public long NowUnix => _s.NowUnix;
    /// <summary>The player's sex byte (RTK player.sex; used to pick sex-specific quest items).</summary>
    public int  Sex => _s.CharSex;
    /// <summary>The player's current face id (RTK player.face).</summary>
    public int  Face => _s.CharFace;
    /// <summary>A menu that shows the player's own paperdoll wearing <paramref name="face"/> as the dialog
    /// portrait. Pure preview — nothing about the character changes until <see cref="CommitFace"/>.</summary>
    public Task<int> MenuWithFace(string prompt, IReadOnlyList<string> options, int face)
        => _s.DlgMenuFace(_npc, prompt, options, face);
    /// <summary>Keep a face for good (persists + redraws self and peers).</summary>
    public void CommitFace(int face) => _s.CommitFace(face);
    /// <summary>Is anything currently equipped (RTK player:isEquipped()).</summary>
    public bool IsEquipped => _s.IsEquipped;

    // ---- war paint / armor dye (WarPaintAbility; RTK arena_master.lua / general_npc_funcs.warPaint) ---
    /// <summary>The current armor-dye palette index (RTK player.armorColor; 0 = undyed).</summary>
    public int ArmorColor => _s.CharArmorColor;
    /// <summary>Is a visible armor/coat worn (RTK's "you need armor or a coat equipped to see your war paint")?</summary>
    public bool HasVisibleArmor => _s.HasVisibleArmor;
    /// <summary>Dye (or bleach, with 0) the worn armor — persists + redraws self &amp; peers (RTK player:refresh).</summary>
    public void SetArmorColor(int color) => _s.SetArmorColor((byte)color);

    /// <summary>Have this NPC cast a warding spell on the player, by Spells.csv key — the scripted cast a
    /// quest performs on the player's behalf (Claw's Harden Armor on a new tiger mail). Mechanics and fx come
    /// from the spell's own SpellParams/spell_effects rows, so it shares an exclusivity slot with the player
    /// version. False (nothing changed) if the key is not a ward or that slot is already taken.</summary>
    public bool CastWard(string spellKey) => _s.NpcCastWard(spellKey, _npc.Name);

    // ---- the mythic alliances (Server/MythicAlliance.cs) -----------------------------------------
    /// <summary>Kills of a creature kind still on the player's KILL TRACK — the last eight kinds killed, most
    /// recent first (<see cref="Session.TrackedKills"/>). 0 means either never killed or killed and since
    /// pushed off the end, a distinction the game does not draw either. This, and not lifetime
    /// <see cref="KillCount"/>, is what an alliance counts.</summary>
    public int TrackedKills(string mobKey) => _s.TrackedKills(mobKey);
    /// <summary>Wipe the kill track — what accepting an alliance does.</summary>
    public void ClearKillTrack() => _s.ClearKillTrack();

    /// <summary>Set this NPC's guards on the player: creatures spawned around HIM, hostile, owned by nobody,
    /// and gone again after <paramref name="seconds"/>. Master Dagger's answer to a third tap on the
    /// shoulder. Returns how many landed (a blocked tile is skipped, not stacked).</summary>
    public int SpawnAmbush(string mobKey, int seconds) => _s.SpawnNpcAmbush(_npc, mobKey, seconds);

    /// <summary>Have this NPC cast Rebirth on the player: full heal, and resurrection if they are a ghost.</summary>
    public void CastRebirth() => _s.NpcCastRebirth(_npc.Name);
    /// <summary>Have this NPC cast Stormstrike on the player and send them home to a tavern — the mythic's
    /// answer to an enemy's sworn ally standing in its chamber.</summary>
    public void CastStormstrikeAndBanish() => _s.NpcCastStormstrike(_npc.Name);

    // ---- hair dye (AppearanceAbility; RTK salon.lua / general_npc_funcs.hairdye) ---
    /// <summary>The current hair-colour palette index (RTK player.hairColor; 0 = base). 5.33 appearance[3].</summary>
    public int HairColor => _s.CharHairColor;
    /// <summary>Whether this client actually renders hair colour — only 5.33 has the appearance[3] slot, so the
    /// dye service is offered to V533 sessions only (on 4.95 it would take gold for an invisible change).</summary>
    public bool RendersHairColor => _s.IsV533;
    /// <summary>Dye the hair — persists + redraws self &amp; peers, clearing any preview (RTK player:refresh).</summary>
    public void SetHairColor(int color) => _s.SetHairColor((byte)color);
    /// <summary>Live-preview a hair colour on the player's own sprite WITHOUT persisting (RTK gfxHairC).</summary>
    public void PreviewHairColor(int color) => _s.PreviewHairColor((byte)color);
    /// <summary>Drop any hair-colour preview and redraw the real colour (browse cancelled).</summary>
    public void ClearHairPreview() => _s.ClearHairPreview();
    /// <summary>Free bag slots remaining.</summary>
    public int  FreeSlotCount => _s.FreeSlotCount;
    /// <summary>Unequip everything back into the bag; false (unchanged) if the bag lacks room for it all.</summary>
    public bool StripAllEquipment() => _s.StripAllEquipment();
    /// <summary>Flip sex, persist, and redraw self + peers.</summary>
    public void CommitSexChange() => _s.CommitSexChange();
    /// <summary>The player's nation/kingdom id (RTK player.country; 0 = Neutral/wilderness, 1 = Koguryo/
    /// Kugnae, 2 = Buya, 3 = Nagnang).</summary>
    public int  Nation => _s.CharNation;

    /// <summary>The totem animal this character follows (0 Ju Jak · 1 Baekho · 2 Hyun Moo · 3 Chung Ryong ·
    /// 4 none) — see <see cref="Content.TotemName"/>. Set by worshipping at a shrine.</summary>
    public int  Totem => _s.CharTotem;
    /// <summary>Switch allegiance to a totem, persisting and re-sending the stat pane the crest sits on.</summary>
    public void SetTotem(int totem) => _s.SetTotem(totem);
    /// <summary>Emigrate to another kingdom (RTK <c>player:updateCountry</c>) — persists, clears any bound
    /// home, repaints the HUD crest. The town criers and Rotah are the only callers.</summary>
    public void SetNation(int nation) => _s.SetNation((byte)Math.Clamp(nation, 0, 255));
    /// <summary>The map the conversation is happening on. RTK's town-crier script branches on
    /// <c>npc.mapTitle</c> to decide which kingdom it is recruiting for; the player is standing next to the
    /// NPC, so their map is the same answer without a second lookup.</summary>
    public int  MapId => _s.CharMap;

    /// <summary>Is the player sitting on a horse? (RTK checks <c>player.state == 3 and player.disguise == 26</c>
    /// — a mounted state plus the horse disguise; here the two are one flag, since the mount IS the appearance
    /// form byte.) The tutorial's horse-riding stage gates on this.</summary>
    public bool Mounted => _s.CharMounted;

    /// <summary>Coin on hand (RTK player.money).</summary>
    public uint Coins => _s.CharCoins;
    /// <summary>Spend coin if the player can afford it; false (no change) if they can't.</summary>
    public bool SpendGold(uint amount) => _s.SpendGold(amount);

    // ---- shadow-stat vendors (ShadowStatsAbility; RTK ExpSeller.lua) ------------------------------
    /// <summary>Banked experience (RTK player.exp) — spendable currency for the shadow-stat vendors once
    /// leveling itself stops consuming it (level 99, or a Peasant walled at 5).</summary>
    public uint Exp => _s.CharExp;
    /// <summary>Spend banked exp if the player has enough; false (no change) if they can't.</summary>
    public bool SpendExp(uint amount) => _s.SpendExp(amount);
    public int  Might => _s.CharMight;
    public int  Grace => _s.CharGrace;
    public int  Will  => _s.CharWill;
    public uint MaxHp => _s.CharMaxHp;
    public uint MaxMp => _s.CharMaxMp;
    /// <summary>Permanently raise a base stat/pool (RTK baseMight/baseGrace/baseWill/baseHealth/baseMagic).</summary>
    public void RaiseMight(int by) => _s.RaiseMight(by);
    public void RaiseGrace(int by) => _s.RaiseGrace(by);
    public void RaiseWill(int by)  => _s.RaiseWill(by);
    public void RaiseMaxHp(uint by) => _s.RaiseMaxHp(by);
    public void RaiseMaxMp(uint by) => _s.RaiseMaxMp(by);

    /// <summary>Does the player carry at least <paramref name="n"/> of an item (by key)?</summary>
    public bool HasItem(string itemKey, int n = 1) => _s.CountItem(itemKey) >= n;
    /// <summary>Is an item (by key) currently worn?</summary>
    public bool HasEquipped(string itemKey) => _s.HasEquipped(itemKey);
    /// <summary>Display name of an item by key (for dialog).</summary>
    public string ItemName(string itemKey) => _s.ItemName(itemKey);
    /// <summary>Warp the player to a map/tile; false if that map isn't renderable here (no strand).</summary>
    public bool Warp(int map, int x, int y) => _s.Warp((ushort)map, (ushort)x, (ushort)y);

    // ---- death / revival (ReviveAbility; RTK shaman.lua + totem_npc.lua `_resurrect`) --------------
    /// <summary>Is the player a ghost right now? (RTK <c>player.state == 1</c>.)</summary>
    public bool IsDead => _s.IsDead;
    /// <summary>Bring a ghost back to life where they stand: full HP/MP, ghost form dropped. No warp — the
    /// player already walked to the reviver.</summary>
    public void Revive(string message) => _s.ReviveInPlace(message);

    /// <summary>The player's current tile (for warps that keep the same position).</summary>
    public int  X => _s.CharX;
    public int  Y => _s.CharY;
    /// <summary>Send the player a status/minitext line (RTK sendMinitext).</summary>
    public void Notify(string text) => _s.Notify(text);
    /// <summary>Make this NPC speak an over-head bubble (RTK npc:talk), rather than open a dialog box.</summary>
    public void Bubble(string text) => _s.NpcBubble(_npc, text);

    /// <summary>Prompt the player for a line of text (RTK inputSeq); null if they cancelled.</summary>
    public Task<string?> Input(string prompt) => _s.DlgInput(_npc, prompt);

    // ---- class / path + title + spell-learning (used by ClassTrainerAbility; RTK *_trainer.lua) ---
    /// <summary>The player's path id (0 = Peasant, 1 Warrior / 2 Rogue / 3 Mage / 4 Poet); -1 if unknown.</summary>
    public int ClassId => _s.CharClassId;
    /// <summary>The BASE path this class descends from — what Content.SpellCosts is keyed to, so a fee lookup
    /// must use this rather than <see cref="ClassId"/> (an NPC subpath has no rows of its own and would read
    /// as FREE while its level still came from the base class's row).</summary>
    public int BasePathId => _s.CharBasePathId;
    /// <summary>Set the player's path (RTK updatePath) — changes the profile class line, persists.</summary>
    public void SetClass(int pathId) => _s.SetCharClass(pathId);

    // ---- sub-alignment (AlignmentAbility / SummitAbility; RTK swapAlignment.lua) ------------------
    /// <summary>The player's sub-alignment (0 Unaligned · 1 Kwi-Sin · 2 Ming-Ken · 3 Ohaeng).</summary>
    public int Alignment => _s.LuaAlignment;
    /// <summary>Devote to (or renounce, with 0) a sub-alignment: sets it, rebuilds the spellbook to the new
    /// alignment's entitlement, and manages the "&lt;Align&gt; &lt;Class&gt; since" legend (RTK swapAlignment).</summary>
    public void Devote(int alignment) => _s.SwapAlignment(alignment);
    /// <summary>The player's current noble title ("" if none).</summary>
    public string Title => _s.CharTitle;
    /// <summary>Set the player's noble title (RTK setTitle), persisted.</summary>
    public void SetTitle(string title) => _s.SetCharTitle(title);

    /// <summary>The RTK region this conversation is happening in (0 Kugnae · 1 Buya · 3 Nagnang · …). The player
    /// has to be standing on the NPC's map to click it, so their map IS the NPC's. -1 for a map with no region.</summary>
    public int Region => Content.RegionOf(_s.CharMap);

    /// <summary>Spells the player can learn now (class + level, minus known) — the Learn Secret menu. Filtered
    /// to what THIS city's trainer may teach: a few secrets belong to one kingdom's trainer alone (see
    /// <see cref="Content.TeachableInRegion"/> — the rogue Remedies), and every other trainer must refuse them.</summary>
    public List<SpellDef> LearnableSpells() =>
        _s.LearnableClassSpells().Where(s => Content.TeachableInRegion(s, Region)).ToList();
    /// <summary>The other half of that split: secrets the player QUALIFIES for but that only another city's
    /// trainer teaches. Not learnable here — this is what lets a trainer point the player at the right city
    /// instead of the spell simply never appearing anywhere.</summary>
    public List<SpellDef> ElsewhereOnlySpells() =>
        _s.LearnableClassSpells().Where(s => !Content.TeachableInRegion(s, Region)).ToList();
    /// <summary>Spells the player's class unlocks over the next <paramref name="levelsAhead"/> insights — the
    /// Divine Secret preview. City-locked to this trainer's own kingdom, same as <see cref="LearnableSpells"/>.</summary>
    public List<SpellDef> FutureSpells(int levelsAhead = 5) =>
        _s.FutureClassSpells(levelsAhead).Where(s => Content.TeachableInRegion(s, Region)).ToList();
    /// <summary>Spells the player currently knows — the Forget Secret menu.</summary>
    public List<SpellDef> KnownSpells() => _s.KnownSpellList();
    /// <summary>Teach one spell; false if the spellbook is full.</summary>
    public bool LearnSpell(SpellDef sp) => _s.LearnSpellFromNpc(sp);
    /// <summary>Forget one spell (resyncs the book so later slots realign).</summary>
    public void ForgetSpell(int spellId) => _s.ForgetOneSpell(spellId);
    /// <summary>Does the player already know this spell (by Spells.csv key)? Unknown key = false.</summary>
    public bool KnowsSpell(string key) =>
        Content.SpellByKey(key) is SpellDef sp && _s.KnowsSpellId(sp.Id);
    /// <summary>Forget a spell by key (the Dog "cleanse"). No-op for an unknown or unknown-to-the-player key.</summary>
    public void ForgetSpellByKey(string key)
    {
        if (Content.SpellByKey(key) is SpellDef sp && _s.KnowsSpellId(sp.Id)) _s.ForgetOneSpell(sp.Id);
    }
    /// <summary>May this path hold Dog spells at all (base classes and NPC subpaths, never a PC subpath)?</summary>
    public bool CanLearnDogSpells => Content.CanLearnDogSpells(_s.CharClassId);

    // ---- marriage (ChapelAbility; RTK NPCs/Common/chapel_npc.lua + Spells/common/propose.lua) -----
    /// <summary>Is the player currently engaged (not yet married)?</summary>
    public bool IsEngaged => !string.IsNullOrEmpty(_s.CharFiance);
    /// <summary>Did THIS player accept the proposal (RTK's "only the proposee may start the ceremony")?</summary>
    public bool IsProposee => _s.CharIsProposee;
    /// <summary>The player's spouse's name ("" if unmarried).</summary>
    public string SpouseName => _s.CharSpouseName;
    /// <summary>Wall-clock seconds until the wedding ceremony may run (RTK's 3-day post-engagement cool-down); 0/negative = ready now.</summary>
    public long MarriageWaitSeconds => _s.CharMarriageTimer - _s.NowUnix;
    /// <summary>Wall-clock seconds until another engagement ring may be bought; 0/negative = ready now.</summary>
    public long RingWaitSeconds => _s.CharRingCooldown - _s.NowUnix;
    /// <summary>Set the 24h cooldown after buying an engagement ring.</summary>
    public void SetRingCooldown(long seconds) => _s.SetRingCooldown(_s.NowUnix + seconds);
    /// <summary>Break off the current engagement on both sides (persists).</summary>
    public void BreakEngagement() => _s.BreakOffEngagement();
    /// <summary>Run the wedding ceremony against the player's current fiancé — asks the fiancé for their
    /// "I do", then marries both on accept. Returns the message to show, or null if already messaged.</summary>
    public Task<string?> Marry() => _s.RunMarriageCeremony();
    /// <summary>End the current marriage on both sides (persists).</summary>
    public void Divorce() => _s.FinishDivorce();
    /// <summary>Permanently lower a base pool as a divorce sacrifice (RTK baseHealth/baseMagic -= penalty).</summary>
    public void LowerMaxHp(uint by) => _s.LowerMaxHp(by);
    public void LowerMaxMp(uint by) => _s.LowerMaxMp(by);
}

/// <summary>
/// A reusable NPC feature (shopkeeping, banking, transport, …). An NPC is COMPOSED of abilities in
/// <see cref="NpcScripts"/>; each ability contributes zero or more entries to the NPC's top menu and
/// supplies the behaviour behind each. This is how shared features live in ONE place and NPCs declare
/// only what they are — not how each feature works.
/// </summary>
public interface INpcAbility
{
    IEnumerable<(string label, Func<NpcContext, Task> run)> Entries(NpcContext ctx);
}

/// <summary>An ability that also responds to the player SPEAKING near the NPC (RTK onSayClick — e.g. saying
/// "i'd like to fish" to Bate, or a tutor's name to the librarian). <see cref="OnSay"/> returns true if it
/// consumed the speech (ran a dialog); the dispatcher then stops, so unrelated speech falls through to normal
/// chat. Implemented alongside <see cref="INpcAbility"/> when an NPC has both a click menu and a spoken trigger.</summary>
public interface INpcSayHandler
{
    Task<bool> OnSay(NpcContext ctx, string speech);
}

/// <summary>An ability that accepts an item HANDED to the NPC — the native 'h'/'H' gesture (0x29, RTK
/// clif_handitem's <c>receiveItem</c>/<c>handItem</c> branch), i.e. a quest turn-in. <see cref="OnHandItem"/>
/// returns true if it consumed the hand; the handler owns taking the item (via <see cref="NpcContext.TakeItem"/>)
/// and granting any reward, and the dispatcher then stops. EVERY item reaches here, quest or not — see
/// <c>Session.HandItemToNpcAsync</c>, which gives RTK's refusal line (and puts the item back on the ground)
/// when no ability accepts, so a handler declines by returning false rather than by swallowing the hand.</summary>
public interface INpcHandItemHandler
{
    Task<bool> OnHandItem(NpcContext ctx, ItemDef item, int amount);
}

/// <summary>Shared empty menu for speech-only NPCs (they respond to <see cref="INpcSayHandler"/> but add no
/// click options — clicking just shows the default greeting).</summary>
internal static class NoClickMenu
{
    public static readonly (string, Func<NpcContext, Task>)[] None = System.Array.Empty<(string, Func<NpcContext, Task>)>();
}

/// <summary>Buy + Sell, backed by the NPC's <see cref="Shops"/> catalogue. Contributes nothing if the NPC
/// has no catalogue (so a shop-flagged NPC we haven't stocked simply shows no buy/sell options).</summary>
public sealed class ShopAbility : INpcAbility, INpcSayHandler
{
    public static readonly ShopAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (Shops.For(ctx.Def.Key) is null) yield break;
        yield return ("Buy",  c => c.Buy());
        yield return ("Sell", c => c.Sell());
    }

    // The two real NexusTK shop voice commands (nexusatlas Voice Commands list):
    //   sell TO the shop:  "buy my <item>" / "buy my all <item>" / "buy my <item> number <N>"
    //   buy FROM the shop: "i buy <item>"  / "i buy all <item>"  / "i buy <item> number <N>"
    // ("buy my" reads backwards but is verbatim from the game — the shopkeeper "buys" your item.) Each side
    // has its OWN list: buying is limited to what this NPC stocks (Shops.For), selling to what it buys
    // (Shops.BuysFrom) — the spoken form goes through the same gate as the Sell menu, so "buy my sword" at the
    // butcher is refused the same way the sword never appears in her grid.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech.StartsWith("buy my ") || speech == "buy my")
        {
            var (item, amount) = VoiceGrammar.ParseQty(speech.Length > 7 ? speech["buy my ".Length..] : "");
            return item.Length != 0 && await ctx.SellByName(item, amount);
        }
        if (speech.StartsWith("i buy "))
        {
            var (item, amount) = VoiceGrammar.ParseQty(speech["i buy ".Length..]);
            return item.Length != 0 && await ctx.BuyByName(item, amount);
        }
        return false;
    }
}

/// <summary>Shared parser for the quantity grammar the NexusTK voice commands use: an item phrase optionally
/// carrying a leading "all" (whole stack) or a trailing "number &lt;N&gt;" (that many). Returns amount -1 for
/// "all", N for "number N", else 1. The item is whatever words remain.</summary>
internal static class VoiceGrammar
{
    public static (string item, int amount) ParseQty(string phrase)
    {
        phrase = phrase.Trim();
        int idx = phrase.LastIndexOf(" number ", StringComparison.Ordinal);   // "<item> number <N>"
        if (idx >= 0 && int.TryParse(phrase[(idx + " number ".Length)..].Trim(), out var n) && n > 0)
            return (phrase[..idx].Trim(), n);
        if (phrase.StartsWith("all ")) return (phrase["all ".Length..].Trim(), -1);   // whole stack
        return (phrase, 1);
    }
}

/// <summary>The three "Misc" NexusTK voice commands that work near ANY NPC (nexusatlas Voice Commands list):
/// "what's your name?", "what do you sell?", "what do you buy?". Appended to every NPC's composition (see
/// <see cref="NpcScripts.For"/>) with no click-menu entry — it only answers these spoken questions out loud,
/// and falls through on anything else. Registered last so a shop/bank/quest handler that matches the same
/// speech wins.</summary>
public sealed class InfoAbility : INpcAbility, INpcSayHandler
{
    public static readonly InfoAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech.Contains("your name"))                     // "what's your name?", "what is your name?"
        {
            ctx.Bubble($"Hello, my name is {ctx.Def.Name}.");
            return Task.FromResult(true);
        }
        if (speech.StartsWith("what do you sell"))
        {
            var names = Catalogue(ctx);
            ctx.Bubble(names.Count == 0 ? "I don't sell anything" : "I sell " + Fit(names) + ".");
            return Task.FromResult(true);
        }
        if (speech.StartsWith("what do you buy"))
        {
            var names = BuyCatalogue(ctx);
            ctx.Bubble(names.Count == 0 ? "I don't buy anything" : "I buy " + Fit(names) + ".");
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    // The NPC's stocked item names, deduped — what it sells TO you.
    private static List<string> Catalogue(NpcContext ctx) =>
        Names((Shops.For(ctx.Def.Key) ?? System.Array.Empty<Shops.Category>()).SelectMany(c => c.Keys));

    // What it buys FROM you — a different list (Shops.BuysFrom). An NPC with no accept list buys anything
    // sellable, and there's no sane way to say that out loud, so it answers with its stock: the honest
    // approximation, and the same thing this question answered before the accept lists existed.
    private static List<string> BuyCatalogue(NpcContext ctx) =>
        Shops.BuysFrom(ctx.Def.Key) is { } buys ? Names(buys) : Catalogue(ctx);

    private static List<string> Names(IEnumerable<string> keys) =>
        keys.Select(Content.ItemByKey).OfType<ItemDef>().Select(d => d.Name).Distinct().ToList();

    // Join item names into one over-head line, capped so it can't overflow the 0x0D speech buffer (u8 length).
    private static string Fit(List<string> names)
    {
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var n in names)
        {
            if (sb.Length + n.Length + 2 > 180) break;
            if (shown++ > 0) sb.Append(", ");
            sb.Append(n);
        }
        if (shown < names.Count) sb.Append(", and more");
        return sb.ToString();
    }
}

/// <summary>Weapon/armour repair. Stub until the durability-repair flow is built.</summary>
public sealed class RepairAbility : INpcAbility
{
    public static readonly RepairAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Fix Item", c => c.Say("Bring me your worn gear — repairs aren't open yet."));
    }
}

/// <summary>Vault storage for coin + items (deposit / withdraw), persisted per character.
///
/// The three actions are their own top-level menu entries, so an inn keeper reads
/// Buy / Sell / Deposit Money / Deposit Item / Withdraw Item. RTK's inn_npc.lua instead shows a single
/// "Banking" button that opens a submenu (bank.show_main_menu) — that's its 7.x client's UI, not 4.95's, so
/// it isn't followed here. Taking coin back out is voice-only ("give my coins back"), matching the same
/// menu; <see cref="OnSay"/> handles it.</summary>
public sealed class BankAbility : INpcAbility, INpcSayHandler
{
    public static readonly BankAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Deposit Money", c => c.DepositMoney());
        yield return ("Deposit Item",  c => c.DepositItem());
        yield return ("Withdraw Item", c => c.WithdrawItem());
    }

    // The real NexusTK banking voice commands (nexusatlas Voice Commands list):
    //   deposit:  "i will deposit <item>" / "i will deposit all <item>" / "i will deposit <item> number <N>"
    //   withdraw: "give my <item> back"   / "give my all <item> back"   / "give my <item> number <N> back"
    //   query:    "what have i deposited?"
    // Coin uses the same verbs with the word "coin"/"coins" ("i will deposit coins number 500"). The legacy
    // "take my <item>" deposit alias is kept so older habits still work.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech.StartsWith("i will deposit "))
        {
            var (item, amount) = VoiceGrammar.ParseQty(speech["i will deposit ".Length..]);
            return item.Length != 0 && await ctx.Deposit(item, amount);
        }
        if (speech.StartsWith("give my ") && speech.EndsWith(" back"))
        {
            string mid = speech["give my ".Length..^" back".Length];
            var (item, amount) = VoiceGrammar.ParseQty(mid);
            return item.Length != 0 && await ctx.Withdraw(item, amount);
        }
        if (speech.StartsWith("what have i deposited"))
        {
            ctx.ShowVault();
            return true;
        }
        if (speech.StartsWith("take my "))   // legacy deposit alias
        {
            var (item, amount) = VoiceGrammar.ParseQty(speech["take my ".Length..]);
            return item.Length != 0 && await ctx.Deposit(item, amount);
        }
        return false;
    }
}

/// <summary>The kingdom messenger's parcel post (RTK MessengerNpc / messenger.lua + Parcel.lua): send gold
/// or an item to another player, and collect parcels others have sent you. Parcels are separate from n-mail
/// (see Parcel.cs) — the bottom-left HUD bag icon means "a parcel waits here". Buy/Sell come from ShopAbility,
/// wired alongside this in NpcScripts; this ability adds only the two parcel entries. "Receive Parcel" is
/// shown only when something is actually waiting (RTK gates its Mailbox option on getParcel()).</summary>
public sealed class MessengerAbility : INpcAbility
{
    public static readonly MessengerAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Send Parcel", c => c.SendParcel());
        if (ctx.HasParcels)
            yield return ("Receive Parcel", c => c.ReceiveParcel());
    }
}

/// <summary>Waypoint fast-travel. Stub — RTK's Waypoint.lua network didn't exist in 4.x/5.x NexusTK, so it
/// isn't ported; this only exists so InnNpc's composition (which has always offered "Transport") has
/// something to show until a period-accurate travel feature is identified.</summary>
public sealed class TransportAbility : INpcAbility
{
    public static readonly TransportAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Transport", c => c.Say("Transport isn't available yet."));
    }
}

/// <summary>Fishing (RTK fishnpc.lua / Bate &amp; Wim). Ports the beginner branch: a chance per cast at a
/// minnow + the <c>learned_to_fish</c> flag (the tutorial's stage-4 requirement). The level-15+ pole/bait/skill
/// system, magical fish, and stuck-line death aren't modelled. The 25% roll applies on every cast, tutorial
/// or not. No click menu — say "fish" instead.</summary>
public sealed class FishAbility : INpcAbility, INpcSayHandler
{
    public static readonly FishAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    // Spoken trigger (RTK: "i'd like to fish"). The tutorial tells the player to say it out loud.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech is not ("fish" or "i'd like to fish" or "id like to fish")) return false;
        await Fish(ctx);
        return true;
    }

    private static async Task Fish(NpcContext ctx)
    {
        await ctx.Say("You're still a youngin'! If you take up fishing now, you'll never amount to anything. " +
                      "Oh, why not? Here's some string and worms for you to try with, good luck!");

        // 25% catch rate, ALWAYS — including on the tutorial's fishing stage. That stage used to guarantee
        // the catch on the theory that a quest shouldn't hinge on a roll, but the quest is the only time
        // most players ever fish, so the guarantee meant the 25% was effectively never the rate anyone saw:
        // fishing read as 100%. Casting again is free and unthrottled, so the roll costs a few repeats, not
        // progress.
        bool caught = ctx.Random(100) <= 25;   // QuestRandom is 1..100 inclusive, so this is exactly 25/100

        if (caught)
        {
            ctx.SetReg("learned_to_fish", 1);
            ctx.GiveItem("minnow", 1);
            await ctx.Say("You caught a fish!");
        }
        else
        {
            await ctx.Say("You fish for quite a while, but with little success.");
        }
    }
}

/// <summary>Just the "Forget Secret" option (RTK forgetSpell): drop one spell/skill from the book. Class-
/// agnostic — it depends only on <see cref="NpcContext.KnownSpells"/> / <see cref="NpcContext.ForgetSpell"/>,
/// so it can be composed onto a non-trainer NPC (e.g. Blood) that only unlearns. <see cref="ClassTrainerAbility"/>
/// reuses the same <see cref="Forget"/> handler so the behavior stays identical everywhere it appears.</summary>
public sealed class ForgetSecretAbility : INpcAbility
{
    public static readonly ForgetSecretAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Forget Secret", Forget);
    }

    public static async Task Forget(NpcContext ctx)
    {
        var known = ctx.KnownSpells();
        if (known.Count == 0) { await ctx.Say("You know no secrets to forget."); return; }

        int pick = await ctx.Menu("Which secret do you wish to forget?", known.Select(s => s.Name).ToList());
        if (pick < 1 || pick > known.Count) return;

        var sp = known[pick - 1];
        ctx.ForgetSpell(sp.Id);
        await ctx.Say($"You have forgotten {sp.Name}.");
    }
}

/// <summary>A class-path trainer (RTK warrior_trainer.lua / rogue_trainer / mage_trainer / poet_trainer).
/// Ports the new-player core: <b>Become a &lt;Class&gt;</b> at level 5 (sex-specific starter kit + 500 gold +
/// path change), <b>Learn / Divine / Forget Secret</b> (the trainer's spell-teaching), and <b>Become Noble</b>
/// (the level-75 title grant). One instance per base path (1 Warrior / 2 Rogue / 3 Mage / 4 Poet). The
/// repeatable Minor Quest is a separate <see cref="MinorQuestAbility"/> composed alongside this one — it adds
/// no menu entry of its own; you get one by saying "quest" near the trainer. NOT ported:
/// the nagnang trials. The level-66+ star/moon/sun armor chains ARE ported, but not here — they are spoken,
/// not clicked, so they live in <see cref="ArmorQuestAbility"/> and are composed alongside this one.</summary>
public sealed class ClassTrainerAbility : INpcAbility
{
    private readonly int _path;                         // base path id, 1..4
    private readonly string _class;                     // "Warrior" / "Rogue" / "Mage" / "Poet"
    private readonly string _sanctuary;                 // "…, the sanctuary of X." (the become-intro flavor)
    private readonly string[] _pitch;                   // the "Tell me more" paragraphs
    private readonly string _weapon;                    // starter weapon item key
    private readonly (string male, string female) _armor, _helm;   // sex-specific starter armor + helm keys
    private readonly (string key, int qty) _food;       // starter consumable (bear's liver / herb pipe)
    private readonly string _foodBlurb;                 // the closing line describing that consumable

    private ClassTrainerAbility(int path, string cls, string sanctuary, string[] pitch, string weapon,
        (string, string) armor, (string, string) helm, (string, int) food, string foodBlurb)
    { _path = path; _class = cls; _sanctuary = sanctuary; _pitch = pitch; _weapon = weapon;
      _armor = armor; _helm = helm; _food = food; _foodBlurb = foodBlurb; }

    private const string BearBlurb =
        "I have also given you some Bear's livers, these will help you keep your strength up. Eat one when you " +
        "are feeling weak, and near death. Shop keepers around town sell them if you need more.";
    private const string PipeBlurb =
        "You also have herb pipes, these will replenish your mana. Once they are used up you should buy some " +
        "more, shop keepers around town sell them.";

    public static readonly ClassTrainerAbility Warrior = new(1, "Warrior", "the sanctuary of the mightiest of all fighters",
        new[] {
            "Tell you about warriors? Well, they are the greatest of the fighter classes. A one man army, so to speak. Warriors are fierce, and powerful, and can battle many foes at once.",
            "Warriors use little magic, instead we prefer to use skills, such as the ability to hit more than one creature at a time.",
            "We depend on the healing skills of other paths, like the poets, but they are always willing to group with a warrior for our awesome killing abilities." },
        "sword_of_power", ("jade_scale_mail", "summer_mail_dress"), ("merchant_helm", "spring_helmet"),
        ("bears_liver", 25), BearBlurb);

    public static readonly ClassTrainerAbility Rogue = new(2, "Rogue", "the sanctuary of the swiftest blades",
        new[] {
            "Tell you about rogues? Well, they are the deadliest of the fighter classes. Nimble, agile, fast, and unmatched one on one, a true assassin.",
            "Rogues use some magic during their battles, and many skills for attacking a foe. We only attack one at a time, but we kill quickly, and efficiently, moving too quick to be hit easily.",
            "We can solo single creatures with great skill, for larger battles we need a little help from a healer." },
        "swift_dagger", ("merchant_waistcoat", "summer_blouse"), ("merchant_helm", "spring_helmet"),
        ("bears_liver", 26), BearBlurb);

    public static readonly ClassTrainerAbility Mage = new(3, "Mage", "the sanctuary of the great magic users",
        new[] {
            "Tell you about mages? Well, mages are the magic users of the land, combining great offensive and defensive magic.",
            "We use magic to subdue our foes, and to conquer all who stand before us. We can also use our great powers defensively, to heal and save ourselves, or others.",
            "The mage is a self contained hunter, and can easily solo hunt without the aid of others, however it is always best to join others - safety in numbers!" },
        "staff_of_power", ("summer_garb", "summer_dress"), ("merchant_helm", "spring_helmet"),
        ("herb_pipe", 4), PipeBlurb);

    public static readonly ClassTrainerAbility Poet = new(4, "Poet", "the sanctuary of the healer",
        new[] {
            "Tell you about poets? Poets are the most sought after path, wanted by every other path to join them in adventures.",
            "Poets are masters of defense with the ability to heal and protect large numbers of people easily.",
            "Higher level poets gain the ability to charm animals, and can become an incredible power themselves if they have the skill." },
        "staff_of_defense", ("summer_robes", "summer_gown"), ("merchant_helm", "spring_helmet"),
        ("herb_pipe", 4), PipeBlurb);

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        int cls = ctx.ClassId;
        if (cls <= 0)                    // Peasant (0) or unknown -> can choose this path
            yield return ($"Become a {_class}", Become);
        else if (cls == _path)           // your own class's trainer -> teach/foresee its secrets
        {
            yield return ("Learn Secret", LearnSecret);
            yield return ("Divine Secret", DivineSecret);
        }
        yield return ("Forget Secret", ForgetSecretAbility.Forget);   // any book, any trainer (RTK shows this always)
        yield return ("Become Noble", BecomeNoble);     // level-75 title, any trainer
    }

    private async Task Become(NpcContext ctx)
    {
        if (ctx.Level < 5)
        { await ctx.Say("Hail, little one! Please return to me when you have reached the 5th insight."); return; }

        await ctx.Say(
            $"Hail, mighty one! Welcome to my sanctuary, {_sanctuary}.",
            $"Have you come to pick your path? I think you would make a great {_class.ToLower()}, and a great hero.");

        int c = await ctx.Menu($"Will you join the path of the {_class.ToLower()}?", new[] { "Yes", "Tell me more", "No" });
        if (c == 1) { await GrantKit(ctx); return; }
        if (c == 2)
        {
            await ctx.Say(_pitch);
            int c3 = await ctx.Menu("Will you join us now?", new[] { "Yes", "No" });
            if (c3 == 1) await GrantKit(ctx);
            else await ctx.Say("Very well, I will be waiting here if you change your mind. I am seeking great people all the time to join this great path.");
            return;
        }
        await ctx.Say("Very well, I will be waiting here if you change your mind. I am seeking great people all the time to join this great path.");
    }

    private async Task GrantKit(NpcContext ctx)
    {
        await ctx.Say("Great! You have made a great decision. I see you becoming a great hero in these lands. Now let me set you up with some supplies.");

        bool female = ctx.Sex == 1;   // 0 = male, 1 = female (confirmed for the tutorial sex-item)
        ctx.GiveItem(_weapon, 1);
        ctx.GiveItem(female ? _armor.female : _armor.male, 1);
        ctx.GiveItem(female ? _helm.female : _helm.male, 1);
        if (_food.qty > 0) ctx.GiveItem(_food.key, _food.qty);
        ctx.AwardGold(500);
        ctx.SetClass(_path);          // RTK updatePath(_path, 0) — changes the profile class line

        await ctx.Say(
            $"Here is some armor, and a weapon. These are specific to the {_class.ToLower()} path, and will help get you started.",
            "I have also given you some gold, it's all I can spare right now. It will help you with repairs, and getting some other equipment like rings.",
            _foodBlurb,
            "If you wish to learn some skills let me know, I can teach you many things to help you in battle.");
    }

    // The fee line both trainer flows show before anything is spent (RTK learnSpell/futureSpells build the
    // identical string): "The fee to learn X is: <item> (n), <item> (n), <gold> gold, All must be in good
    // condition." — or "FREE" when the spell has no Content.SpellCosts row for this class. RTK carries gold as
    // an item with id 0 inside the same list; our LearnCost keeps it in its own field, so it's appended last.
    // RTK breaks the line after "is:"; we keep it on one line because nothing else in this server's 4.95
    // dialog text relies on \n rendering and it has never been confirmed on this client.
    private static string FeeText(NpcContext ctx, SpellDef sp)
    {
        var cost = Content.LearnCostFor(sp, ctx.BasePathId);
        var parts = new List<string>();
        if (cost is not null)
        {
            foreach (var (item, amount) in cost.Items) parts.Add($"{ctx.ItemName(item)} ({amount})");
            if (cost.Gold > 0) parts.Add($"{cost.Gold} gold");
        }
        return parts.Count == 0
            ? $"The fee to learn {sp.Name} is: FREE"
            : $"The fee to learn {sp.Name} is: {string.Join(", ", parts)}, All must be in good condition.";
    }

    // "Learn Secret" (RTK learnSpell): pick from the spells this class can learn at or below your level, swear
    // the oath, see the full fee, then pay it. The menu lists BARE names — RTK builds a "<name> Lvl: <n>"
    // display string but never actually shows it in the picker.
    private static async Task LearnSecret(NpcContext ctx)
    {
        var learn = ctx.LearnableSpells();
        if (learn.Count == 0)
        {
            // Nothing left HERE isn't the same as nothing left: a city-locked secret the player already
            // qualifies for (a rogue's Maro's/Maso's/Dagger's Remedy) is taught by exactly one kingdom's
            // trainer, so send them to it by name rather than letting the spell look like it doesn't exist.
            var elsewhere = ctx.ElsewhereOnlySpells();
            if (elsewhere.Count > 0)
            {
                await ctx.Say(elsewhere
                    .Select(s => $"{s.Name} is not mine to teach — seek it from my counterpart in {Content.RegionCityName(Content.CityLockOf(s))}.")
                    .ToArray());
                return;
            }
            await ctx.Say("You have learned every secret I can teach you for now. Grow stronger, then return.");
            return;
        }

        int pick = await ctx.Menu("Which secret shall I teach you?", learn.Select(s => s.Name).ToList());
        if (pick < 1 || pick > learn.Count) return;

        var sp = learn[pick - 1];
        const string Humble = "The potential for learning is endless, be always humble and ready to learn.";

        int swear = await ctx.Menu(
            $"You are ready to learn {sp.Name}. Do you swear you will use this secret only for good causes?",
            new[] { "Yes", "No" });
        if (swear != 1) { await ctx.Say(Humble); return; }

        if (await ctx.Menu(FeeText(ctx, sp), new[] { "Yes", "No" }) != 1) { await ctx.Say(Humble); return; }

        // Real per-class item/gold cost (Content.SpellCosts); a spell with no row for this class stays free.
        // Everything is checked BEFORE anything is consumed, so a short fee costs the player nothing.
        if (Content.LearnCostFor(sp, ctx.BasePathId) is { } cost)
        {
            bool short_ = cost.Items.Any(i => !ctx.HasItem(i.Item, i.Amount)) || ctx.Coins < (uint)cost.Gold;
            if (short_)
            {
                await ctx.Say("Paying for what you want is a sign of devotion. Return when you have what is required for this.");
                return;
            }
            foreach (var (item, amount) in cost.Items) ctx.TakeItem(item, amount);
            if (cost.Gold > 0) ctx.SpendGold((uint)cost.Gold);
        }

        if (!ctx.LearnSpell(sp)) { await ctx.Say("Your mind cannot hold any more secrets right now."); return; }
        await ctx.Say($"You have learned {sp.Name}.");
    }

    // "Divine Secret" (RTK futureSpells): the SAME shape as Learn Secret — pick a secret from the next few
    // insights and read its level + full fee — except nothing is ever spent and nothing is learned. Purely a
    // "what should I be saving for" preview.
    private static async Task DivineSecret(NpcContext ctx)
    {
        var fut = ctx.FutureSpells();
        if (fut.Count == 0)
        {
            // Same pointer as Learn Secret's: "what should I be saving for" is exactly the question a
            // city-locked secret should answer, even though this trainer can't sell it.
            var elsewhere = ctx.ElsewhereOnlySpells();
            if (elsewhere.Count > 0)
            {
                await ctx.Say(elsewhere
                    .Select(s => $"{s.Name} awaits you in {Content.RegionCityName(Content.CityLockOf(s))}, not here.")
                    .ToArray());
                return;
            }
            await ctx.Say("There are no further secrets awaiting you."); return;
        }

        int pick = await ctx.Menu("Which secret would you like to learn more about?", fut.Select(s => s.Name).ToList());
        if (pick < 1 || pick > fut.Count) return;

        var sp = fut[pick - 1];
        await ctx.Say($"{sp.Name} can be learned at insight {sp.Level}.", FeeText(ctx, sp));
    }

    // "Forget Secret" (RTK forgetSpell) lives in ForgetSecretAbility.Forget — shared so Blood and the trainers
    // behave identically. Referenced from Entries above.

    // "Become Noble" (RTK general_npc_funcs.setTitle): a level-75 custom title, 200 gold per character.
    private static async Task BecomeNoble(NpcContext ctx)
    {
        if (ctx.Level < 75)
        { await ctx.Say("You are still young, and not ready for this yet. Return when you have gained your 75th level."); return; }

        string? title = await ctx.Input("Your heart is in the right place. Which title shall you take?");
        if (string.IsNullOrWhiteSpace(title)) return;
        title = title.Trim();
        if (title.Length > 12) { await ctx.Say("Your entered title must be no greater than 12 characters."); return; }

        uint cost = (uint)(200 * title.Length);
        int c = await ctx.Menu($"For that title, {cost} coins are required. You want to do that?", new[] { "Yes", "No" });
        if (c != 1) return;

        if (ctx.Coins < cost) { await ctx.Say($"You do not have the required {cost} gold to set this title."); return; }
        if (ctx.Title == title) { await ctx.Say("You would be wasting your money to set the same title twice."); return; }
        if (!ctx.SpendGold(cost)) { await ctx.Say($"You do not have the required {cost} gold to set this title."); return; }

        ctx.SetTitle(title);
        ctx.Notify($"Your title has been changed to: {title}");
    }
}

/// <summary>The kingdom librarian (RTK librarian.lua). Its tutorial role: when the player speaks the tutor's
/// name ("ironheart"/"jadespear") on tutorial stage 5, it welcomes them and sets <c>talked_to_tutor</c>. Also
/// offered as a "Talk to Librarian" click option so the interaction works without voice. The book-shop Buy/Sell
/// catalogue isn't ported yet.</summary>
public sealed class LibrarianAbility : INpcAbility, INpcSayHandler
{
    public static readonly LibrarianAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Talk to Librarian", Talk);
    }

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech is not ("ironheart" or "jadespear")) return false;
        await Talk(ctx);
        return true;
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Stage("tutorial_quest") == 5 && ctx.Reg("talked_to_tutor") != 1)
        {
            await ctx.Say(
                "Hello there, I see you have met my friend the Tutor. I hope he is doing well these days.",
                "This is the great library of the kingdom, here we store the knowledge of the ages.",
                "One of the prized items citizens come here for is the \"Legends\", a scroll that tells the great tales.",
                "Unfortunately, this item is very expensive, but perhaps when you are richer you will be able to get your own.");
            await ctx.Say(
                "... or better yet... make your own legend to be told in the scroll!",
                "Ah, what dreams, what wonders. Well, I must get back to work now. See you around, I hope to hear tales of your adventures soon.",
                "You should go back to the tutor now, and continue to learn more, he has so much to teach you.");
            ctx.SetReg("talked_to_tutor", 1);
        }
        else
        {
            await ctx.Say("Welcome to the great library of the kingdom, where the knowledge of the ages is stored.");
        }
    }
}

// Chu Rua the turtle (ChuRuaNpc) is now a Lua dialog script (game-data/npc_dialog.lua -> npcs.ChuRuaNpc),
// so its C# ability was deleted — Session.RunNpcAsync gives the Lua script exclusive ownership when present
// (see NpcScript.Has). Its speech-only companions below (rabbit/rock/tiger) stay in C# until the Lua NPC layer
// grows an OnSay/speech-trigger hook (Phase 3).

// Chu Rua's speech companions (rabbit / rock / tiger) are now Lua speech scripts (npc_dialog.lua ->
// npcs_say.ChuRua*Npc), driven by the OnSay hook in Session.RunNpcSayAsync (Lua wins per-NPC, C# falls back).
// Their C# abilities were deleted along with the turtle's.

/// <summary>Change Face / Change Gender (RTK rogue_guild_shaman.lua: <c>general_npc_funcs.changeFace</c> /
/// <c>changeGender</c> — the third option, Eyes, isn't ported). Both are genuinely visible on this client:
/// Face is a real byte in the 4.95 7-byte appearance form (§8 of the protocol doc). Face browsing
/// live-previews each candidate on the player's own screen (mirrors RTK's clone.equip preview loop) before
/// it's paid for and committed.</summary>
public sealed class AppearanceAbility : INpcAbility
{
    public static readonly AppearanceAbility Instance = new();
    private const uint FaceCost = 3000;
    private const uint GenderCost = 12000;

    // The 4.95 client has exactly 90 head sprites, ids 0..89 — read straight off the client's own asset
    // table (NexusTK.dat -> Head.tbl, whose first line is literally "NumFaces 90", then "ID n, Palette 0,
    // Starting n*100" for every n in 0..89; Head.epf holds the matching 9000 frames, 100 per head). All 90
    // were rendered and eyeballed: every one is a real, complete player head.
    //
    // DO NOT use RTK's list here. RTK's changeFace offers 200..216, which is a LATER client's id space —
    // on 4.95 every one of those is out of range, so the client draws no head at all and you get a headless
    // character (reported live 2026-08-06). Same trap as the spell/sound id spaces: an RTK constant is only
    // portable once it's been checked against the 4.95 assets.
    private const int FaceCount = 90;
    // Browsing 90 one at a time is 89 dialog round-trips, so the loop also offers a coarse +10 jump and
    // starts on the face you're already wearing (RTK started at its first entry, but it only had 17).
    private const int FaceJump = 10;

    private const uint HairDyeCost = 2000;   // RTK general_npc_funcs.hairdye

    // RTK's Kugnae Salon palette (general_npc_funcs.hairdye): ten named dyes, each an appearance[3] index.
    // These are RTK-client indices; on the 5.33 client the index->hue map can differ (same divergence the mob
    // palettes and armor dye hit), so the NAMES are the intent and the numbers are a starting point to VERIFY
    // live — browse them in-game and, for any that render wrong, sweep the real 5.33 index and correct it here.
    private static readonly (string Name, byte Color)[] HairDyes =
    {
        ("Black",      0), ("Silver",    1), ("Brown",      2), ("Sky blue",  8), ("Dark blue",  7),
        ("Royal blue", 24), ("Orange",   10), ("Red",       11), ("Green",     22), ("Scarlet",   21),
    };

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Change Face", ChangeFace);
        yield return ("Change Gender", ChangeGender);
        // Hair colour only renders on 5.33 (appearance[3]); don't sell an invisible change to a 4.95 client.
        if (ctx.RendersHairColor) yield return ("Change Hair Color", ChangeHairColor);
    }

    // Adapted from RTK salon.lua / general_npc_funcs.hairdye: 2,000 coins, browse named dyes with a LIVE
    // preview on your own sprite (RTK previews via gfxHairC), commit on confirm. Because the preview never
    // touches persisted state, backing out — or closing the dialog mid-browse — restores the real colour and
    // costs nothing. RTK gates the salon on no helmet worn; hair colour draws regardless of a helm on this
    // client, so that gate isn't reproduced.
    private static async Task ChangeHairColor(NpcContext ctx)
    {
        if (ctx.Coins < HairDyeCost) { await ctx.Say($"A new dye is {HairDyeCost:N0} coins. Come back when you have it, dearie."); return; }

        int index = 0;
        try
        {
            while (true)
            {
                var (name, color) = HairDyes[index];
                ctx.PreviewHairColor(color);   // show it on the player's own sprite now (transient, not persisted)
                int choice = await ctx.Menu($"Color: {name}  ({index + 1} of {HairDyes.Length}). Is this the one?",
                    new[] { "Yes, dye it", "Next color", "Previous color", "Nevermind" });
                if (choice == 1)
                {
                    if (ctx.Coins < HairDyeCost) { await ctx.Say($"A new dye is {HairDyeCost:N0} coins. Come back when you have it, dearie."); return; }
                    ctx.SpendGold(HairDyeCost);
                    ctx.SetHairColor(color);   // persists the new colour + redraws + clears the preview
                    await ctx.Say("There! Now don't you look so much better!");
                    return;
                }
                if (choice == 2) index = (index + 1) % HairDyes.Length;
                else if (choice == 3) index = (index + HairDyes.Length - 1) % HairDyes.Length;
                else return;   // Nevermind / dialog closed
            }
        }
        // Drop any un-committed preview and repaint the real colour. Correct in BOTH exits: a commit already
        // cleared the preview (so this is a no-op showing the new colour), and a cancel/close shows the old one.
        finally { ctx.ClearHairPreview(); }
    }

    private static async Task ChangeFace(NpcContext ctx)
    {
        int crime = await ctx.Menu("You're not wanted for a crime, are you?", new[] { "Yes", "No" });
        if (crime != 2) { await ctx.Say("Ah, I see. Appear as thou wilt."); return; }

        if (ctx.Coins < FaceCost) { await ctx.Say($"It will cost you {FaceCost:N0} coins. Come back when you have that."); return; }
        int pay = await ctx.Menu($"It will cost you {FaceCost:N0} coins. Do you wish to pay?", new[] { "Yes", "No" });
        if (pay != 1) { await ctx.Say("Ah, I see. Appear as thou wilt."); return; }

        await ctx.Say("Choose the face you like. Please be careful as the change is permanent. Use 'Next face'/'Previous face' to browse.");

        // The browse is a TRY-ON, not a live edit: each step draws the player's own paperdoll wearing the
        // candidate head in the dialog's portrait slot (MenuWithFace -> the 0x30 tag-0 player head). The
        // character is never touched, so backing out costs nothing to undo, a half-finished browse can't
        // leave a wrong face persisted, and nobody else in the room sees you flicker through 90 heads.
        int index = Math.Clamp(ctx.Face, 0, FaceCount - 1);   // start on the face they're wearing
        while (true)
        {
            int choice = await ctx.MenuWithFace($"Do you like this face? ({index + 1} of {FaceCount})",
                new[] { "I want this one", "Next face", "Previous face", $"Skip ahead {FaceJump}", "Nevermind" }, index);
            if (choice == 1)
            {
                // Money can have moved since the first check (a trade, another window) — re-check before taking it.
                if (ctx.Coins < FaceCost) { await ctx.Say($"It will cost you {FaceCost:N0} coins. Come back when you have that."); return; }
                ctx.SpendGold(FaceCost);
                ctx.CommitFace(index);
                await ctx.Say("It's tricky to mold this flesh. Let's see how it looks.");
                return;
            }
            // Wrap at both ends (RTK clamped, but it only had 17 entries — with 90 a clamp just strands you
            // at whichever end you walked to).
            if (choice == 2) index = (index + 1) % FaceCount;
            else if (choice == 3) index = (index + FaceCount - 1) % FaceCount;
            else if (choice == 4) index = (index + FaceJump) % FaceCount;
            else return;   // Nevermind, or the player closed the dialog — nothing to undo
        }
    }

    private static async Task ChangeGender(NpcContext ctx)
    {
        if (ctx.IsEquipped)
        {
            int strip = await ctx.Menu(
                "You must remove everything you are wearing before you can change your gender. Remove your items now?",
                new[] { "Yes, strip me", "No, I can strip myself" });
            if (strip != 1) { await ctx.Say("Come back once you've stripped down, then."); return; }
            if (!ctx.StripAllEquipment())
            { await ctx.Say("Your pack doesn't have room to hold everything you're wearing — make some space first."); return; }
        }

        if (ctx.Coins < GenderCost) { await ctx.Say($"You need {GenderCost:N0} gold to change your gender, come back when you have the cash."); return; }

        int confirm = await ctx.Menu("You realize you won't be able to wear the clothes that you normally do, do you not?", new[] { "Yes", "No" });
        if (confirm != 1) { await ctx.Say("Ok. Maybe you're better off as you are."); return; }

        string ask = ctx.Sex == 0 ? "Do you wish to become a woman?" : "Do you wish to become a man?";
        int confirmSex = await ctx.Menu(ask, new[] { "Yes", "No" });
        if (confirmSex != 1) { await ctx.Say("Ok. Maybe you're better off as you are."); return; }

        if (ctx.Coins < GenderCost) { await ctx.Say($"You need {GenderCost:N0} gold to change your gender, come back when you have the cash."); return; }
        ctx.SpendGold(GenderCost);
        ctx.CommitSexChange();
        await ctx.Say("There, wow that was hard work.");
    }
}

/// <summary>The Arena Master's war-paint dye (RTK NPCs/arena/arena_master.lua → general_npc_funcs.warPaint) —
/// this NPC's ONE and only service ("Mountain" at the Mountain Arena, "Tower" at the Kugnae one). Colors the
/// worn armor/coat via the 0x33 appearance[4] palette byte (<see cref="Character.ArmorColor"/>). Three
/// branches, exactly as RTK: already dyed → <b>Bleach</b> back to base (10 gold); not dyed → pick 1 of 8
/// <b>team-battle</b> colors (20 gold); and at <b>level 99</b> an optional "special" dye (Brown / Wasabi /
/// Super Wasabi, gated on base Vita/Mana) offered before the team menu.
/// <para>The color values are RTK's own palette indices. On the 4.95 client the index→visible-color map isn't
/// fully catalogued (the look-lab confirmed 16/32/64/128/255 recolor and 0..8 stay base; the 9..36 range these
/// live in was never swept), so some may need adjusting once swept with the <c>@dye &lt;n&gt;</c> GM command —
/// the numbers here are the faithful RTK starting point.</para></summary>
public sealed class WarPaintAbility : INpcAbility
{
    public static readonly WarPaintAbility Instance = new();

    // RTK team-battle dyes (general_npc_funcs.warPaint): 8 teams, 20 gold, one armorColor each. These are
    // CANONICAL colours — i.e. what they mean on the shared "seasonal" body palette. Session.ArmorDye() runs
    // them through Content.DyeRampFor so they render as the same colour on armors whose body sprite uses a
    // different Body.tbl palette (see ArmorDyeRamps.csv).
    //
    // DELIBERATE DEVIATION FROM RTK (2026-08-09): Ju jak was RTK's 21 and Fire RTK's 31. On this client's
    // palette 21 is the ramp at index 216 — mean (117,52,22), a dark RUST — so the Vermilion Bird rendered
    // brown on every armor in the game. The palette holds exactly one strong red, ramp 31 at index 40, mean
    // (173,45,0) with more than double the red-dominance of any other ramp, and Fire had it. So Ju jak takes
    // 31 and Fire moves to 20 (index 208, mean (213,134,65) — orange, arguably a better "Fire" anyway), which
    // keeps all eight teams distinguishable. Characters dyed before this keep their stored byte and simply
    // show the old colour until they re-dye; nothing migrates, nothing breaks.
    private static readonly (string Name, byte Color)[] Teams =
    {
        ("Hyun moo", 10), ("Ju jak", 31), ("Chung ryong", 24), ("Baekho", 11),
        ("Ash", 28), ("River", 17), ("Fire", 20), ("Snow", 29),
    };

    /// <summary>The eight team colours, for the content check that every one of them has a ramp defined for
    /// each body palette that disagrees with the seasonal one — retuning the table above without adding the
    /// matching ArmorDyeRamps.csv row is a silent wrong-colour bug, not a crash.</summary>
    public static IEnumerable<byte> TeamColors => Teams.Select(t => t.Color);

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("War paint", WarPaint);
    }

    private static async Task WarPaint(NpcContext ctx)
    {
        // RTK warns (but still lets you proceed) if there's no armor/coat to show the color on.
        if (!ctx.HasVisibleArmor)
            await ctx.Say("You need to have an armor or a coat equipped to see your war paint. You may continue but you will be unable to see your new colors until then.");

        // Already dyed → offer to bleach back to base (10 gold).
        if (ctx.ArmorColor != 0)
        {
            int c = await ctx.Menu("You wish me to bleach your war paint for 10 gold?", new[] { "Bleach me", "No" });
            if (c == 1)
            {
                if (!ctx.SpendGold(10)) { await ctx.Say("Return to me when you have enough gold."); return; }
                ctx.SetArmorColor(0);
                await ctx.Say("It is done.");
            }
            else await ctx.Say("As you wish.");
            return;
        }

        // Not dyed. The level-99 special dyes come first (optional); declining falls through to the team menu.
        if (ctx.Level >= 99 && await OfferSpecialDye(ctx)) return;

        // The everyone dye: pick a team color for 20 gold.
        int join = await ctx.Menu("To engage in team battles you need a dye. It will cost you 20 coins, you want to do it?",
            new[] { "Yes", "No" });
        if (join != 1)
        {
            await ctx.Say("You are not saying that 20 coins is too expensive, are you? I can't make it any less expensive than that.");
            return;
        }

        int pick = await ctx.Menu("Which team do you wish to join?", Teams.Select(t => t.Name).ToList());
        if (pick < 1 || pick > Teams.Length) return;
        if (!ctx.SpendGold(20)) { await ctx.Say("Return to me when you have enough gold."); return; }

        ctx.SetArmorColor(Teams[pick - 1].Color);
        await ctx.Say(
            "May the heavens favor a painless death.",
            "(Be sure to be able to group with your team. Press 'SHIFT G' to allow your Champion to group you.)",
            "(If you are the Champion, press 'g' to add or remove someone from your group.)");
    }

    // RTK level-99 "special dye" branch: Brown always; Wasabi if baseHealth ≥ 50000 OR baseMagic ≥ 25000;
    // Super Wasabi if baseHealth ≥ 160000 OR baseMagic ≥ 80000 (MaxHp/MaxMp are our baseHealth/baseMagic
    // analog). Returns true if the player bought one (flow ends); false if they declined — the caller then
    // falls through to the team menu, matching RTK.
    private static async Task<bool> OfferSpecialDye(NpcContext ctx)
    {
        var dyes = new List<(string Label, uint Cost, byte Color)> { ("Brown (1000 gold)", 1000, 12) };
        if (ctx.MaxHp >= 50000  || ctx.MaxMp >= 25000) dyes.Add(("Wasabi (5000 gold)", 5000, 16));
        if (ctx.MaxHp >= 160000 || ctx.MaxMp >= 80000) dyes.Add(("Super Wasabi (12000 gold)", 12000, 36));

        int consider = await ctx.Menu("Do you wish to consider a special dye, Great one?",
            new[] { "Yes, please", "No, I am special enough without such dyes." });
        if (consider != 1) return false;

        int pick = await ctx.Menu("Which dye would you like, Great one?", dyes.Select(d => d.Label).ToList());
        if (pick < 1 || pick > dyes.Count) return false;   // cancelled — RTK falls through to the team menu
        var dye = dyes[pick - 1];

        if (!ctx.SpendGold(dye.Cost))
        { await ctx.Say("If you cannot afford it, perhaps you are not so great afterall..."); return true; }

        ctx.SetArmorColor(dye.Color);
        await ctx.Say("It is done.");
        return true;
    }
}

/// <summary>Trade banked experience for permanent stat growth once you're too high-level for exp to matter
/// otherwise (RTK NPCs/Common/ExpSeller.lua — "Shady"/"Sunset"/"Midnight", the identical <c>ExpSeller</c>
/// vendors sitting at the "…Weaver" map camps). Gated at level 90. Three offers: Might/Grace/Will up to a
/// flat 130 base (10,000,000 exp each), or a permanent Vitality/Mana pool increase whose cost per purchase
/// climbs with your current pool (RTK's escalating cost curve, config defaults expSellFactor1=0/factor2=2 —
/// see config.lua). Below level 99 a lower interim cap applies to Vitality/Mana, same as RTK.
/// <para>The higher, mark-gated caps and 100M-per-point cost (RTK's <c>npcIsBonHwa</c> branch of
/// <c>showShadowStatsMenu</c>) live on the Bon-Hwa NPC instead — see <see cref="BonHwaAbility"/>, which reads
/// <see cref="NpcContext.Mark"/> and the per-class limit table.</para></summary>
public sealed class ShadowStatsAbility : INpcAbility
{
    public static readonly ShadowStatsAbility Instance = new();
    private const int LevelGate = 90;
    private const uint StatCost = 10_000_000;
    private const int  StatCap  = 130;
    private const uint MinPoolCost = 20_000_000;
    private const int  ExpSellFactor2 = 2;   // RTK config.lua default (factor1=0 drops out of the formula)

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ($"Talk to {ctx.Def.Name}", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Level < LevelGate)
        { await ctx.Say("There is nothing I can do for you, young one. Come back when you have achieved the 90th insight."); return; }

        int choice = await ctx.Menu("Welcome, great one. How may I be of service?",
            new[] { "Shadow Stats", "Shadow Vitality", "Shadow Mana" });

        if (choice == 1) await ShadowStats(ctx);
        else if (choice == 2) await ShadowPool(ctx, vitality: true);
        else if (choice == 3) await ShadowPool(ctx, vitality: false);
    }

    private static async Task ShadowStats(NpcContext ctx)
    {
        if (ctx.Exp < StatCost)
        { await ctx.Say($"You do not understand enough of your true nature to unleash your potential any further. Please return when you possess at least {StatCost:N0} experience."); return; }

        var opts = new List<(string Label, int Base, Action<int> Raise)>();
        if (ctx.Might < StatCap) opts.Add(("Might", ctx.Might, ctx.RaiseMight));
        if (ctx.Grace < StatCap) opts.Add(("Grace", ctx.Grace, ctx.RaiseGrace));
        if (ctx.Will  < StatCap) opts.Add(("Will",  ctx.Will,  ctx.RaiseWill));

        if (opts.Count == 0)
        { await ctx.Say("There is nothing more I can do for you. Perhaps you can find another who can guide you further."); return; }

        int pick = await ctx.Menu("Which aspect of your potential do you seek to unleash?", opts.Select(o => o.Label).ToList());
        if (pick == 0) return;
        var (label, baseVal, raise) = opts[pick - 1];

        int maxShadows = Math.Min((int)(ctx.Exp / StatCost), StatCap - baseVal);
        if (maxShadows <= 0)
        { await ctx.Say("It is impossible to exceed one's own potential."); return; }

        string? input = await ctx.Input(
            $"Your natural {label} is {baseVal}.\n\nYou can unleash your shadow potential up to {maxShadows} times.\n\nHow many times do you choose?");
        if (!int.TryParse(input, out int count) || count <= 0) return;
        if (count > maxShadows) { await ctx.Say("It is impossible to exceed one's own potential."); return; }

        int newVal = baseVal + count;
        uint cost = (uint)count * StatCost;
        int confirm = await ctx.Menu(
            $"Your {label} will permanently increase to {newVal}.\n\n{cost:N0} experience will be irrevocably sacrificed.\n\nAre you sure?",
            new[] { "Yes", "No" });
        if (confirm != 1) return;

        if (!ctx.SpendExp(cost)) { await ctx.Say("It is impossible to exceed one's own potential."); return; }
        raise(count);
        await ctx.Say("It is done.");
    }

    /// <summary>RTK <c>_getVitaOrManaCost</c>: cost of the NEXT point of pool starting from <paramref name="currentValue"/>,
    /// statIndex 1=Vitality/2=Mana (folded into the interval elsewhere — the divisor here just mirrors the source).</summary>
    private static uint PoolCost(uint currentValue, int statIndex)
    {
        long calculated = (long)currentValue * statIndex / 20_000 * 2_000_000 * ExpSellFactor2 + MinPoolCost;
        return (uint)Math.Max(MinPoolCost, calculated);
    }

    private static async Task ShadowPool(NpcContext ctx, bool vitality)
    {
        int statIndex = vitality ? 1 : 2;
        uint interval = (uint)(100 / statIndex);              // 100 per step for Vitality, 50 for Mana
        uint current = vitality ? ctx.MaxHp : ctx.MaxMp;
        string label = vitality ? "Vitality" : "Mana";
        bool minor = ctx.Level < 99;
        uint cap = (uint)(10000 / statIndex);                 // interim cap while not yet level 99

        // Walk forward from the current pool, pricing each successive point, to find how many the player's
        // CURRENT banked exp can afford right now (escalating marginal cost — RTK's own simulation loop).
        long exp = ctx.Exp;
        uint val = current;
        int possible = 0;
        while (exp > 0)
        {
            uint next = val + interval;
            if (minor && next > cap) break;
            uint cost = PoolCost(val, statIndex);
            if (exp >= cost) possible++;
            exp -= cost;
            val = next;
        }

        if (possible < 1)
        {
            if (minor && cap - current < interval)
            { await ctx.Say("You have reached your limit for now, young one. Return to me when you have achieved the final insight."); return; }
            await ctx.Say($"You do not understand enough of your true nature to unleash your potential any further. Please return when you possess at least {PoolCost(current, statIndex):N0} experience.");
            return;
        }

        string? input = await ctx.Input(
            $"Your natural {label} is {current:N0}.\n\nYou can unleash your shadow potential up to {possible} times.\n\nHow many times do you choose?");
        if (!int.TryParse(input, out int count) || count <= 0) return;
        if (count > possible) { await ctx.Say("It is impossible to exceed one's own potential."); return; }

        uint expCost = 0; uint newVal = current;
        for (int i = 0; i < count; i++) { expCost += PoolCost(newVal, statIndex); newVal += interval; }

        int confirm = await ctx.Menu(
            $"Your {label} will permanently increase to {newVal:N0}.\n\n{expCost:N0} experience will be irrevocably sacrificed.\n\nAre you sure?",
            new[] { "Yes", "No" });
        if (confirm != 1) return;

        if (!ctx.SpendExp(expCost)) { await ctx.Say("It is impossible to exceed one's own potential."); return; }
        if (vitality) ctx.RaiseMaxHp(newVal - current); else ctx.RaiseMaxMp(newVal - current);
        await ctx.Say("It is done.");
    }
}

/// <summary>The Chapel (RTK NPCs/Common/chapel_npc.lua — "Lotus"/"Peach"/"Fen" in Kugnae/Buya/Nagnang): Buy/Sell
/// (its <see cref="Shops"/> catalogue — love/cooked_fish/rose_petals, matching RTK's own buyItems) plus the
/// marriage feature set. <b>Buy Engagement Ring</b> grants the companion spell "propose" (see
/// <c>Session.CastPropose</c> — cast it near your beloved, who must already be holding a ring you gave them,
/// to send the accept/decline prompt). <b>Break Off Engagement</b>/<b>Marriage</b>/<b>Divorce</b> are
/// conditionally shown per the player's own engagement/marriage state, mirroring the lua's own menu gating
/// (both Break/Marriage show for EITHER side of an engagement — Marriage itself then blocks the proposer
/// with a message, matching RTK's "only the proposee starts the ceremony" rule). NOT ported: RTK's
/// <c>Config.shotgunWeddingEnabled</c> (no config system here — the 3-day wait always applies) and
/// <c>Config.bossDropSalesEnabled</c> (Sell always shows nothing extra, matching the lua's own "else return
/// {}" branch).</summary>
public sealed class ChapelAbility : INpcAbility
{
    public static readonly ChapelAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Buy Engagement Ring", BuyRing);
        if (ctx.IsEngaged)
        {
            yield return ("Break Off Engagement", BreakOffEngagement);
            yield return ("Marriage", RunCeremony);
        }
        if (!string.IsNullOrEmpty(ctx.SpouseName)) yield return ("Divorce", Divorce);
    }

    private static async Task BuyRing(NpcContext ctx)
    {
        if (ctx.RingWaitSeconds > 0)
        { await ctx.Say("Whoa! Weren't you just here? Let your heart cool a bit from your last love."); return; }
        if (ctx.IsEngaged || !string.IsNullOrEmpty(ctx.SpouseName))
        { await ctx.Say("Whoa! Your heart is already committed to someone else."); return; }

        int c1 = await ctx.Menu("Have you met one you hope to one day marry?",
            new[] { "Yes, I am very much in love!", "You mean I'm expected to LOVE them?" });
        if (c1 != 1) { await ctx.Say("Come back when your heart is ready."); return; }

        int price = Content.ItemByKey("engagement_ring")?.BuyPrice ?? 4000;
        int c2 = await ctx.Menu($"The engagement ring will cost you {price} gold. Do you wish to buy one?",
            new[] { "No price is too high for my love.", "That much?!? Forget it!" });
        if (c2 != 1) { await ctx.Say("Come back when your heart is ready."); return; }

        if (ctx.Coins < (uint)price)
        { await ctx.Say("Come back when you can afford to make the commitment."); return; }

        ctx.SpendGold((uint)price);
        ctx.GiveItem("engagement_ring", 1);
        var propose = Content.SpellByKey("propose");
        if (propose is not null) ctx.LearnSpell(propose);
        ctx.SetRingCooldown(86400);   // RTK: 24h before another ring
        await ctx.Say("To propose, cast this spell near your beloved. Then follow the directions. Make sure you have your ring with you!");
    }

    private static async Task BreakOffEngagement(NpcContext ctx)
    {
        await ctx.Say("How sad this is necessary. At least you reached this decision before marriage.");
        int c = await ctx.Menu("Are you sure you want to end the engagement?",
            new[] { "Yes, it is necessary (You will lose some XP)", "No, I need to consider further." });
        if (c != 1) { await ctx.Say("I hope you can salvage your relationship."); return; }

        uint penalty = ctx.MaxMp * 1000;   // RTK: player.baseMagic * 1000
        ctx.SpendExp(Math.Min(ctx.Exp, penalty));
        ctx.BreakEngagement();
        await ctx.Say("It is done.");
    }

    private static async Task RunCeremony(NpcContext ctx)
    {
        if (ctx.MarriageWaitSeconds > 0)
        { await ctx.Say("You have engaged too recently. Let your hearts settle a while longer."); return; }
        if (!ctx.IsProposee)
        { await ctx.Say("The proposee should start the marriage ceremony."); return; }

        int confirm = await ctx.Menu("Are you certain you wish to devote yourself to this man or woman for life?",
            new[] { "Yes", "No" });
        if (confirm != 1) { await ctx.Say("Come back when you are firm in your resolve to marry."); return; }

        string? result = await ctx.Marry();
        if (!string.IsNullOrEmpty(result)) await ctx.Say(result);
    }

    private static async Task Divorce(NpcContext ctx)
    {
        await ctx.Say("Oh no! You made a horrible mistake!", "However, I can help you get that divorce you want.");
        uint expCost = ctx.MaxHp * 2550;   // RTK: player.baseHealth * 2550
        int choice = await ctx.Menu($"It will cost {expCost:N0} experience. Are you sure you want this divorce?",
            new[] { "Yes", "No" });
        if (choice != 1)
        { await ctx.Say("Patience and love will save your marriage.\n\nDivorce is not something to take lightly."); return; }

        if (ctx.Exp >= expCost)
        {
            ctx.SpendExp(expCost);
            ctx.Divorce();
            await ctx.Say("You are now divorced.");
            return;
        }

        await ctx.Say("Hmmm.. you don't have the experience to divorce, but there is something else you can offer.");
        const uint vitaPenalty = 8000, manaPenalty = 4000;
        int c2 = await ctx.Menu("Perhaps some physical suffering would be sufficient?",
            new[] { $"Sacrifice {vitaPenalty} Vita", $"Sacrifice {manaPenalty} Mana", "I'd rather not." });
        if (c2 != 1 && c2 != 2) return;

        uint penalty = c2 == 1 ? vitaPenalty : manaPenalty;
        string stat = c2 == 1 ? "Vita" : "Mana";
        int confirm = await ctx.Menu($"It will cost you {penalty} base {stat} as a penalty. Continue?",
            new[] { "Yes, do it", "No, nevermind" });
        if (confirm != 1) return;

        if (c2 == 1 && ctx.MaxHp < vitaPenalty)
        { await ctx.Say("You need to gain more experience in your health before you can make this sacrifice."); return; }
        if (c2 == 2 && ctx.MaxMp < manaPenalty)
        { await ctx.Say("You need to gain more experience in your magic before you can make this sacrifice."); return; }

        if (c2 == 1) ctx.LowerMaxHp(penalty); else ctx.LowerMaxMp(penalty);
        ctx.Divorce();
        await ctx.Say("You are now divorced.");
    }
}

/// <summary>Tells the current server date + time (a real, self-contained feature many NPCs share).</summary>
/// <summary>Raise the dead — the one thing every Shaman does, and the reason ghosts walk to one at all
/// (RTK <c>NPCs/Common/shaman.lua</c>'s <c>click</c>, and the identical <c>_resurrect</c> helper the four
/// Wilderness totem priests call in <c>NPCs/Common/totem_npc.lua</c>; both scripts are quoted verbatim below).
///
/// Contributes NOTHING to a living player's menu: RTK wraps the whole thing in <c>if player.state == 1</c>
/// with no else branch, so clicking a Shaman while alive is a no-op there and here. The revival is IN PLACE
/// (<see cref="NpcContext.Revive"/>) — the ghost walked here under its own power, which is exactly what F1's
/// "Silver Thread" passage is for (<c>Session.SilverThread</c> warps the ghost to a Shaman; this ability is
/// what actually un-ghosts them once they arrive).
///
/// Wired to ShamanNpc + the four totem priests in NpcAbilities.csv. NOT to RogueGuildShamanNpc — despite the
/// name, <c>rogue_guild_shaman.lua</c> is a face/gender/eyes changer (the `appearance` ability) and has no
/// revival branch at all.</summary>
public sealed class ReviveAbility : INpcAbility
{
    public static readonly ReviveAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (!ctx.IsDead) yield break;   // RTK's `if player.state ~= 1 then return end`
        yield return ("Return to the world of the living", Resurrect);
    }

    // The prompt, the Yes/No, and the closing line are RTK's own strings. A Shaman with nothing else to offer
    // has exactly one menu entry, and RunNpcAsync dives straight into a single entry — so a ghost clicking a
    // Shaman lands on this question immediately, matching RTK's script-on-click flow with no wrapper menu.
    //
    // internal, not private: the tutorial area has no Shaman of its own, so every NPC there answers a ghost
    // with this exact dialog (Session.RunNpcAsync). Sharing the method rather than copying the strings is
    // what keeps the two paths from drifting apart.
    internal static async Task Resurrect(NpcContext c)
    {
        int pick = await c.Menu("Ah, another of the fallen come for my aid. Are you ready to return to the world of the living?",
                                new[] { "Yes", "No" });
        if (pick != 1) return;   // "No", or the player closed the dialog
        c.Revive("Your spirit returns to your body.");
        await c.Say("So shall it be! Keep yourself safe, and free from harm.");
    }
}

public sealed class TimeAbility : INpcAbility
{
    public static readonly TimeAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Date & Time", c => c.Say($"It is {DateTime.Now:dddd, MMMM d} — {DateTime.Now:h:mm tt}."));
    }
}

/// <summary>Surfaces this NPC's quests (from <see cref="Quests.ForNpc"/>) as menu entries — one per quest,
/// its label reflecting the player's progress — and runs the quest's <see cref="QuestDef.Talk"/> script when
/// picked. Added automatically to any NPC that has quests (see <see cref="NpcScripts.For"/>), so a quest is
/// wired end to end just by listing it under a giver in <see cref="Quests.ByNpc"/>.</summary>
public sealed class QuestAbility : INpcAbility
{
    public static readonly QuestAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        foreach (var q in Quests.ForNpc(ctx.Def.Id))
        {
            var quest = q;   // capture per-iteration for the closure
            // Label is just the quest name — a quest owns its own stage meaning (the tutorial runs 0..14, not
            // the 0/1/2 convention), so a generic "in progress/done" suffix here would be wrong.
            yield return (quest.Name, c => quest.Talk(c));
        }
    }
}

/// <summary>Ad-hoc entries unique to one NPC (a quest option, a one-off line), so a bespoke NPC can add
/// its own menu items without needing a whole ability class.</summary>
public sealed class InlineAbility : INpcAbility
{
    private readonly (string, Func<NpcContext, Task>)[] _entries;
    public InlineAbility(params (string label, Func<NpcContext, Task> run)[] entries) { _entries = entries; }
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => _entries;
}
