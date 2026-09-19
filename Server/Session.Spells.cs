using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ===== spells / skills ======================================================================
    // Spellbook wire = RTK 7.x clif_sendmagic, opcode 0x17: slot(u8=idx+1) type(u8) [name u8len+txt]
    // [question u8len+txt]. This is the same "no-op in the main world dispatcher (remap 0x2a), handled by
    // the client's SECONDARY dispatcher" pattern already proven for the item opcodes (0x0F/0x10/0x37/0x38):
    // 0x17 add-spell resolves to 0x2a in remap[0x17-3], exactly like 0x0F/0x10 which work live. The client
    // sorts type 1/2 into the Spell book and type 5 into the Skill book (one 0x17 packet, keyed on type).
    // Casting comes back client->server as 0x0F (clif_parsemagic) -> HandleCast. The 906 spell definitions
    // (name/class/level/type/prompt) come from the RTK Spells table (Content.Spells) — real NexusTK data.

    // The client's spellbook array size is unconfirmed for 4.95; RTK 7.x uses 52 (MAX_SPELLS). Cap
    // conservatively so an over-long teach can't overrun the client array; raise the SpellBookCap row in
    // game-data/ServerTuning.csv once a live test confirms the real limit.
    private static int SpellBookCap => Content.SpellBookCap;

    // Re-send every learned spell/skill on world entry (the client's book starts empty each login). Slot =
    // list index, matching the 0x0F cast "pos" the client sends back.
    private void RefreshSpells()
    {
        // Drop anything an era gate has since switched off (the 8 individual trap spells — see
        // Content.IsOutOfEraSplitTrap) BEFORE numbering slots, so a book saved under the old rules doesn't
        // hand the client a spell it can no longer cast. Compacting first keeps slot == list index, which is
        // exactly what the client sends back on 0x0F.
        if (_char.Spells.RemoveAll(id => Content.SpellById(id) is { } gated && Content.IsOutOfEraSplitTrap(gated)) > 0)
            StoreSave();

        for (int i = 0; i < _char.Spells.Count; i++)
        {
            var sp = Content.SpellById(_char.Spells[i]);
            if (sp is not null) SendAddSpell(i, sp);
        }
    }

    // 0x17 add-spell-to-book: slot(u8=idx+1) type(u8) [name u8len+txt] [question u8len+txt].
    private void SendAddSpell(int slot, SpellDef sp)
    {
        var d = new List<byte> { (byte)(slot + 1), sp.Type };
        var nm = Ascii(sp.Name);     d.Add((byte)nm.Length); d.AddRange(nm);
        var q  = Ascii(sp.Question); d.Add((byte)q.Length);  d.AddRange(q);
        SendMap(ServerOp.AddSpell, _gameInc++, d.ToArray(), $"addspell(0x17) slot={slot} '{sp.Name}' t{sp.Type}");
    }

    /// <summary>Replace the whole book with EXACTLY the abilities this character's class, level, mark and
    /// alignment entitle it to — the spell half of <see cref="RespecTo"/>, run by @lvl, @class, @mark and
    /// @align. This is a rebuild, not a top-up: everything is forgotten first, so the book can shrink (drop
    /// to level 10 and the level-90 secrets go with it) and can never accumulate leftovers from a previous
    /// class or alignment. That is the whole reason the old "@spells" (learn-everything, additive, never
    /// forgets) and "@forgetspells" are gone — between them they could only ever grow the book, and a
    /// character who had been three classes carried all three books around.
    ///
    /// <para>Returns false with the class complaint already sent if the character has no known path.</para></summary>
    private bool SyncSpellbook(bool announce = true)
    {
        int path = Content.PathIdForClass(_char.ClassName);
        if (path < 0)
        {
            SendLog($"'{_char.ClassName}' isn't a known class — set one first, e.g.  {Prefix}class Mage  " +
                    $"({string.Join(" / ", Content.PlayablePathNames())}).");
            return false;
        }
        // Dog spells ride along only for a finished linguist (Content.DogFlagReg, set by the Spotted dog or
        // by @dog) whose path may hold them at all — base classes and NPC subpaths, never a PC subpath. Both
        // halves matter: a Barbarian can finish the chain and still be refused, which is exactly what the Dog
        // itself says when you ask it for the secret.
        bool dogFlag = HasDogFlag && Content.CanLearnDogSpells(path);
        // ...and the Sage rung they paid for, which is path-independent (every class buys the same ladder
        // from the same NPC), so unlike the Dog spells there is nothing to check but the registry.
        var want = Content.RespecSpellSet(path, _char.Level, _char.Alignment, _char.Mark, dogFlag,
                                          QuestCounter(Content.SageRungReg));

        ClearSpellbook();
        bool capped = want.Count > SpellBookCap;
        foreach (var sp in want.Take(SpellBookCap))
        {
            _char.Spells.Add(sp.Id);
            SendAddSpell(_char.Spells.Count - 1, sp);
        }
        if (_enteredWorld) StoreSave();

        int spells = want.Take(SpellBookCap).Count(s => s.IsSpell);
        int skills = _char.Spells.Count - spells;
        if (announce)
            SendLog($"Spellbook rebuilt for {ClassTitle} lvl {_char.Level} " +
                    $"({Character.AlignmentName(_char.Alignment)}): {_char.Spells.Count} ability(ies) — {spells} spell / {skills} skill." +
                    (capped ? $"  Hit the {SpellBookCap}-slot cap (raise SpellBookCap in ServerTuning.csv)." : ""));
        Log.Info($"   -> spellbook resync: {ClassTitle} ({Content.PathName(path)}/{path}) align {_char.Alignment} " +
                 $"mark {_char.Mark} lvl {_char.Level} dog {(dogFlag ? "yes" : "no")} -> " +
                 $"{_char.Spells.Count}{(capped ? " (CAPPED)" : "")}");
        return true;
    }

    // "@align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>" — set the sub-alignment the book is drawn from. A
    // character holds only universal spells + this alignment's set, never the other sub-alignments' parallel
    // spells (which share display names and showed up as duplicates). Rebuilds the book on the spot, so the
    // old two-step "@forgetspells then @spells to get a clean single-alignment set" is no longer a thing you
    // can forget to do.
    private void SetAlignment(string text)
    {
        string a = text.Trim();
        if (a.Length == 0)
        {
            SendLog($"alignment is {Character.AlignmentName(_char.Alignment)} ({_char.Alignment}). usage: {Prefix}align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>");
            return;
        }
        int val = int.TryParse(a, out var n) && n >= 0 && n < Character.Alignments.Length
            ? n
            : Array.FindIndex(Character.Alignments, s => string.Equals(s, a, StringComparison.OrdinalIgnoreCase));
        if (val < 0) { SendLog($"unknown alignment \"{a}\" — use Unaligned / Kwisin / Mingken / Ohaeng (or 0-3)."); return; }
        SwapAlignment(val, announce: false);
        SendLog($"Alignment set to {Character.AlignmentName(_char.Alignment)}.");
        Log.Info($"   -> ALIGN set to {_char.Alignment} ({Character.AlignmentName(_char.Alignment)})");
    }

    // The internal legend name for an alignment devotion — "<align>_<class>_since" (RTK swapAlignment's
    // legend name), stable across the class it was earned in so a later swap can find and replace it. Index
    // is Character.Alignment (0 unaligned has no legend); class index is the base path id 1-4.
    private static readonly string[] AlignLegendPrefix  = { "", "kwisin",  "mingken",  "ohaeng" };
    private static readonly string[] AlignLegendDisplay = { "", "Kwi-Sin", "Ming-Ken", "Ohaeng" };
    private static readonly string[] BaseClassName      = { "Peasant", "Warrior", "Rogue", "Mage", "Poet" };

    /// <summary>Devote to (or renounce, with 0) a sub-alignment — the ONE code path shared by @align, the
    /// three alignment shrines (<see cref="AlignmentAbility"/>) and the Tiger Palace summit
    /// (<see cref="SummitAbility"/>). Mirrors RTK's <c>Player.swapAlignment</c>: set the field, manage the
    /// "&lt;Align&gt; &lt;Class&gt; since" legend mark (drop the old one, stamp the new — never for unaligned),
    /// persist, and rebuild the spellbook to the new alignment's entitlement (our <see cref="SyncSpellbook"/>
    /// replaces RTK's position-for-position spell array swap; the result is the same book for a 0↔N swap,
    /// which is all the shrines and the summit ever do).</summary>
    internal void SwapAlignment(int newAlignment, bool announce = true)
    {
        byte na = (byte)Math.Clamp(newAlignment, 0, 3);
        int bp = CharBasePathId;

        // Drop any existing alignment legend, earned under ANY alignment/class (RTK loops every combo).
        for (int a = 1; a <= 3; a++)
            for (int c = 1; c <= 4; c++)
                RemoveLegend($"{AlignLegendPrefix[a]}_{BaseClassName[c].ToLowerInvariant()}_since");

        _char.Alignment = na;
        if (na != 0 && bp >= 1 && bp <= 4)
            AddLegend($"{AlignLegendDisplay[na]} {BaseClassName[bp]} since ({Character.GameDate})",
                      $"{AlignLegendPrefix[na]}_{BaseClassName[bp].ToLowerInvariant()}_since", (byte)bp, 0x80);

        if (_enteredWorld) StoreSave();
        SyncSpellbook(announce);
    }

    /// <summary>"@spell &lt;name or id&gt;" — teach ONE ability outright, ignoring class, level, mark and
    /// alignment. Matches by display name, then by identifier, then by raw Spells row id (Content.FindSpell),
    /// and appends to the book rather than rebuilding it.
    ///
    /// <para>This is deliberately the ONLY additive grant path, and it is the narrow one: a single named
    /// ability, one at a time. The old learn-everything "@spells" is not coming back — it could only grow the
    /// book, so a character who had been three classes carried all three books at once, which is exactly what
    /// <see cref="SyncSpellbook"/> exists to prevent. Note the corollary: because @lvl / @class / @mark /
    /// @align each REBUILD the book from the entitlement set, anything taught here is wiped by the next one
    /// of those. Teach after the rebuild, not before.</para></summary>
    private void TeachSpellCmd(string text)
    {
        string q = text.Trim();
        if (q.Length == 0)
        {
            SendLog($"usage: {Prefix}spell <name or id>   (learn one ability, any class — {Content.Spells.Count} exist)");
            return;
        }

        var sp = Content.FindSpell(q);
        if (sp is null)
        {
            SendLog($"no spell matches \"{q}\".");
            var near = Content.SearchSpells(q, 8);
            if (near.Count > 0) SendLog("  closest: " + string.Join(", ", near.Select(s => s.Name)));
            return;
        }

        // The 8 post-2003 individual trap spells when the era gate is off. Teaching one would LOOK like it
        // worked and then vanish on the next login, because RefreshSpells prunes them out of a saved book —
        // so refuse here and name the toggle instead of handing over a spell that quietly evaporates.
        if (Content.IsOutOfEraSplitTrap(sp))
        {
            SendLog($"{sp.Name} is post-4.95 content and is switched off — Set Trap still sets that trap. " +
                    $"To enable it, set SplitTrapSpells=1 in game-data/ServerTuning.csv and {Prefix}reload.");
            return;
        }
        if (_char.Spells.Contains(sp.Id)) { SendLog($"You already know {sp.Name} (slot {_char.Spells.IndexOf(sp.Id) + 1})."); return; }
        if (_char.Spells.Count >= SpellBookCap)
        {
            SendLog($"Spellbook is full at {SpellBookCap} slots — rebuild it ({Prefix}lvl {_char.Level}) to clear " +
                    $"room, or raise SpellBookCap in game-data/ServerTuning.csv.");
            return;
        }

        _char.Spells.Add(sp.Id);
        SendAddSpell(_char.Spells.Count - 1, sp);
        if (_enteredWorld) StoreSave();

        SendLog($"Learned {sp.Name} — {(sp.IsSkill ? "skill" : "spell")} slot {_char.Spells.Count}, " +
                $"{Content.PathName(sp.PathId)} lvl {sp.Level}. (A {Prefix}lvl/{Prefix}class/{Prefix}mark/{Prefix}align rebuild will forget it.)");
        Log.Info($"   -> @spell taught '{sp.Name}' (id {sp.Id} key {sp.Key} type {sp.Type} path {sp.PathId}) " +
                 $"to '{_char.Name}' -> slot {_char.Spells.Count - 1}");
    }

    /// <summary>Empty the book: one 0x18 remove per slot (highest first, so the client's array shifts under
    /// us predictably), then drop the ids. Not a command any more — the only caller is
    /// <see cref="SyncSpellbook"/>, which immediately refills it.</summary>
    private void ClearSpellbook()
    {
        for (int slot = _char.Spells.Count - 1; slot >= 0; slot--)
            SendMap(ServerOp.RemoveSpell, _gameInc++, new byte[] { (byte)(slot + 1) }, $"removespell(0x18) slot={slot}");
        _char.Spells.Clear();
    }

    /// <summary>0x30 spell-pane swap (Shift+C over the spellbook — RTK clif_parsechangespell, clif.c:10521).
    /// The book is a DENSE list here (slot == list index, which is the "pos" the client casts back on 0x0F),
    /// so only two OCCUPIED slots can trade places; dragging a spell onto an empty trailing slot would need a
    /// gap the model doesn't carry, and is ignored. Re-send both slots (0x17 overwrites a book slot in place),
    /// so the swap is silent — no removespell needed. Persisted via MarkDirty like every other book change.</summary>
    private void SwapSpellSlots(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= _char.Spells.Count || b >= _char.Spells.Count) return;
        (_char.Spells[a], _char.Spells[b]) = (_char.Spells[b], _char.Spells[a]);
        if (Content.SpellById(_char.Spells[a]) is { } spA) SendAddSpell(a, spA);
        if (Content.SpellById(_char.Spells[b]) is { } spB) SendAddSpell(b, spB);
        MarkDirty();
    }

    // 0x0F cast (RTK clif_parsemagic): body[0]=book slot+1; then per the learned spell's type: type 1 -> a
    // typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. We play the cast animation
    // (0x1A type 6 = magic) for us + peers, spend a little mana, and apply a GENERIC effect: targeted (type 2)
    // spells damage the target world mob with a magic-power hit (reusing the world damage/exp path, so a spell
    // kill rewards exp like a melee kill); self/prompt spells just animate. Per-spell bespoke effects (heals,
    // buffs, teleports, summons) are a follow-up — RTK implements those as ~900 Lua scripts.
    private void HandleCast(byte[] dec)
    {
        // Hyun Moo Revival is the ONE spell exempt from the dead guard - nexusatlas: "Will return poet's own
        // life if dead." Everything else keeps RTK's "Spirits can't cast spells." The slot is resolved first
        // so we know WHICH spell is being cast before deciding.
        if (dec.Length < 1) return;
        // SLEEP GATE (the Doze family's PvP branch — see ReceiveSleep). Checked alongside the mount gate,
        // before the slot is resolved: no spell is exempt, and there is nothing to be gained from letting a
        // sleeping caster pick which one they can't cast.
        if (Asleep) { SendMiniText("You are asleep."); return; }
        // NO-CASTING MAP GATE (RTK clif.c:11427 — the whole 0x0F opcode is wrapped in
        // `if (map[sd->bl.m].spell || sd->status.gm_level)`, else "That doesn't work here."). This is the
        // rule that keeps magic out of the towns' interiors: taverns, shops, the Gathering halls, the class
        // trainers' buildings. Blanket, before the slot is even resolved — no spell is exempt, including
        // Hyun Moo Revival, and staff bypass it exactly as RTK's gm_level does (so a GM can still work in a
        // locked room). See Content.SpellsAllowed for why MapIndoor is NOT the flag to key this off.
        if (!Content.SpellsAllowed(_char.Map) && !IsGm)
        { SendMiniText("That doesn't work here."); return; }
        // MOUNT GATE. You can't cast from horseback — same state gate the equip/use paths already apply
        // (Session.Items.cs), and the same clif_sendminitext wording. Checked before the slot is even
        // resolved: no spell is exempt, not even Hyun Moo Revival (you can't be dead AND mounted anyway).
        if (BlockedByMount()) return;
        int slot = dec[0] - 1;
        if (_char.Hp == 0)
        {
            var dyingPick = slot >= 0 && slot < _char.Spells.Count ? Content.SpellById(_char.Spells[slot]) : null;
            if (dyingPick is null || !Content.IsHyunMooRevival(dyingPick))
            { SendMiniText("Spirits can't cast spells."); return; }
        }
        if (slot < 0 || slot >= _char.Spells.Count)
        { Log.Info($"   ?? cast slot {slot} out of range ({_char.Spells.Count} known)"); return; }
        var sp = Content.SpellById(_char.Spells[slot]);
        if (sp is null) return;

        // Era gate (Content.IsOutOfEraSplitTrap): the individual trap spells are a 2003-07-01 addition, so in
        // 4.95 the only route is the Set Trap dispatcher's typed prompt. RefreshSpells already prunes them
        // from the book on world entry — this catches the mid-session cases (a GM grant, a live @reload that
        // flips the toggle back off) before any Lua/C# cast path can run. The dispatcher is NOT affected: it
        // resolves the same SpellDef internally, downstream of this check.
        if (Content.IsOutOfEraSplitTrap(sp))
        { SendMiniText("You must cast Set Trap and name the trap you wish to set."); return; }

        // Per spell type, the body carries different args after the slot byte. Type 1 ("Which …?") = a typed
        // answer string — e.g. Gateway's N/E/S/W. RTK's clif_parsemagic (clif.c:8857) strcpy's it straight from
        // the byte after the slot, so it is a NUL-terminated ASCII string (NOT length-prefixed). Type 2 ("Which
        // target?") = a u32BE entity id; if absent the client cast with just the slot (log: `0f 14 00`), so we
        // fall back to the faced tile.
        string? answer = null;
        if (sp.Type == 1 && dec.Length >= 2)
        {
            int end = 1;
            while (end < dec.Length && dec[end] != 0) end++;
            answer = Encoding.ASCII.GetString(dec, 1, end - 1);
        }
        uint? targetId = sp.Type != 1 && dec.Length >= 5
            ? (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]) : null;
        Log.Info($"   -> CAST '{sp.Name}' slot {slot} type {sp.Type} base '{Content.BaseKey(sp)}'" +
                 $"{(answer is null ? "" : $" answer '{answer}'")} by {_char.Name}");

        // CAST DELAY — the shared cast/swing slot (Session.Combat.cs _nextActionTick, Content.CastDelayMs).
        // Placed here, ahead of ApplyCast, so a held cast spends NO mana, fires no animation and prints no
        // "You cast X." until it actually goes off. Spells with no delay (every heal/buff, Ambush, and the
        // dog 5-way) skip this entirely — they neither wait on the slot nor arm it, so they stay castable
        // mid-swing at the ordinary 3/sec action budget.
        //
        // HELD, NOT DROPPED. Per the user: swing then cast Invisible too slowly to stack, and the cast is not
        // lost — "the invis animation change will apply as soon as the swing is finished: full attack
        // animation, become invisible animation". So a slot-blocked cast goes on the SAME queue the
        // over-budget path already uses and fires when the slot frees (drained at the top of every inbound
        // packet, and the client's ~31ms repeat means that lands within ~31ms of the boundary).
        //
        // The queue's 250ms expiry is what keeps this from contradicting the live capture, where 138 held
        // spark casts produced only 36 confirmations: a 333ms swing frees the slot inside that window so the
        // cast survives to fire, whereas a 1000ms cast delay outlives it and the queued cast is discarded.
        // One mechanism, both behaviours, split by how long the block lasts.
        int castDelay = Content.CastDelayMs(sp);
        if (castDelay > 0)
        {
            if (!ActionSlotReady)
            {
                Log.Info($"   -- cast held: {ActionSlotLeft}ms left of the shared cast/swing slot");
                if (CastQueueEnabled) QueueCast(dec);
                return;
            }
            ArmActionSlot(castDelay);
        }

        _castNarrated = false;   // reset each cast; a self-narrating spell sets it to skip the generic line below
        if (!ApplyCast(sp, targetId, answer)) return;   // couldn't cast (no mana / too weak) — a message was already sent

        // The ONE caster-facing line for every successful cast: just "You cast <name>." (live NexusTK style — no
        // flavor). EXCEPTION: spells that narrate their own outcome (a teleport's "You have arrived...", an
        // inspect's result) set _castNarrated so we don't tack a redundant "You cast X" onto them (e.g. Gateway).
        // Any per-spell TARGET flavor (Content.SpellTexts) was already sent to the target INSIDE ApplyCast, so on
        // a self-cast it prints first and this generic line second, matching live ordering.
        if (!_castNarrated) SendMiniText($"You cast {sp.Name}.");

        // The cast's magic animation (0x1A type 6). Sound is NOT carried here — the client picks an action's sound
        // from a fixed type->sound table (magic/type 6 has none), so the 4th byte is ignored. The spell's sound is
        // sent separately over 0x19 by BroadcastFx (RTK clif_playsound), which the static RE shows IS wired to the
        // audio player. param stays 0.
        // Cast POSE LENGTH (Content.CastAnimFrames). This used to be a hardcoded 8, which matches nothing in
        // RTK and is why a held cast key rendered as three short flickers instead of one sustained pose: at
        // ~133ms the pose expires between the client's key repeats, whereas at 35 (~583ms) each repeat
        // re-asserts a pose that hasn't finished, so the three casts allowed per action-budget window read as
        // one continuous cast.
        // Physical melee strikes (Berserk/Whirlwind/Assault/Desperate Attack/Lethal Strike & the rest of the
        // sacrifice + Chin-Baek warrior-strike family, Content.ShowsSwingAnim) are swings, not spells: show the
        // attack pose (0x1A type 1) with the swing's own timing, not the magic cast pose. Everything else casts.
        bool swingAnim = Content.ShowsSwingAnim(sp);
        // CastActionType stays a public byte API (see its doc comment); the cast is ours, at our end, and
        // accepts any byte it returns rather than narrowing to ActionType's named members.
        ActionType animType = (ActionType)Content.CastActionType(sp);   // swing, an emote-range override (furies = Rage), else the magic pose
        ushort animTime = swingAnim ? (ushort)AttackSpeed : Content.CastAnimFrames;
        SendAction(_char.Id, animType, animTime, param: 0);                                                     // strike swing / cast pose
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, animType, animTime, 0), except: this);          // peers see it
        SendStats();                                                                        // push HP/MP to the HUD
    }

    // Apply a spell's effect. Returns false if the cast couldn't happen (a message was already sent). The engine
    // is DATA-DRIVEN: Content.FxFor(sp) supplies the archetype + real RTK formulas (extracted from the Lua by
    // re/extract_spell_formulas.py). We dispatch on the archetype — Damage/Heal evaluate the actual per-spell
    // formula, Buff applies a timed stat mod, Debuff freezes a mob, ManaBattery trades HP↔MP, Cure clears our
    // debuffs; Utility/Summon/Teleport/Dialog degrade to "spend mana + acknowledge". A spell with no export row
    // falls back to the keyword classifier (ApplyCastGeneric).
    private bool ApplyCast(SpellDef sp, uint? targetId, string? answer = null)
    {
        // Data-driven Lua verb path (game-data/SpellParams.csv + spell_verbs.lua): if this spell has a
        // params row naming a loaded Lua verb, run it and we're done. STRICTLY ADDITIVE — any spell without a
        // row (the other ~600) falls straight through to the C# dispatch below, unchanged. A Lua error falls
        // through too, so a broken verb can never take a spell offline. Both files hot-reload via @reload.
        if (Content.SpellParams.TryGetValue(sp.Key, out var prow))
        {
            var verb = prow.GetValueOrDefault("verb", "");
            // Tri-state (SpellScript.Run): null = no such verb -> fall through to C# below; true = cast
            // succeeded; false = the verb ran but declined (no mana / blocked / no target) OR errored, and has
            // already messaged/logged — return that verdict straight up, since a false must NOT trigger the
            // central "You cast X." and must NOT re-run the spell through the C# path.
            var r = SpellScript.Run(verb, new SpellContext(this, sp, targetId, answer), prow);
            if (r.HasValue) return r.Value;
        }

        // Gateway (common/gateway.lua): a teleport with its own bespoke logic — warp to a gate of the caster's
        // kingdom picked by the N/E/S/W answer. Intercept before the fx dispatch (a teleport has no damage/heal
        // archetype and would otherwise degrade to CastMisc's "spend mana + acknowledge" no-op).
        if (Content.BaseKey(sp) == "gateway") return Lua(CastWorldArch("gateway", sp, targetId, answer), sp);

        // (Return / bladestorm / spot_traps / filch / divine used to be dispatched from this method by a
        // hardcoded key set each. They are now bound by their SpellParams row and handled by the block at the
        // top — see the "Group A" note on SpellScript.Run. Their C# handlers are gone with them.)

        // Propose (common/propose.lua): a skill-type spell (SplType 5 — no native typed-answer/target wire
        // arg at all) whose real interaction is entirely a scripted dialog (RTK inputSeq/menuSeq), same class
        // of primitive as an NPC's. Intercept before the fx dispatch — it has no export row and would
        // otherwise silently no-op via CastMisc.
        if (sp.Key.Equals("propose", StringComparison.OrdinalIgnoreCase)) return Lua(CastWorldArch("propose", sp, targetId, answer), sp);
        if (sp.Key.Equals("mentor", StringComparison.OrdinalIgnoreCase)) return Lua(CastWorldArch("mentor", sp, targetId, answer), sp);

        // set_trap dispatcher (RTK rogue/set_trap.lua, SplQuestion "What trap? >"): re-runs the SAME level
        // gate + mana cost as casting the specific set_X_trap spell directly (see Content.TrapSpellFor),
        // keyed off the typed answer. Costs no mana itself — CastTrap below spends the real per-kind amount.
        // The dispatcher and the eight individual set_X_trap spells take the SAME route: LuaPlaceTrap already
        // resolves the typed answer to a trap kind, enforces the level gate and debits the per-kind mana (it
        // has to — the dispatcher spell carries none of those itself). The C# tail that used to repeat all of
        // that here was a second copy of the same resolution.
        if (Content.IsTrapDispatcher(sp))
            return Lua(CastWorldArch("set_trap", sp, targetId, answer), sp);
        if (Content.TrapSpellFor(sp) is (Content.TrapKind directKind, int _, int directMana))
            return Lua(CastWorldArch("set_trap", sp, targetId, answer), sp);
        if (Content.PetSpellFor(sp) is (string petMobKey, int _, int petMana, int petCooldown))
            return Lua(CastWorldArch("pet_summon", sp, targetId, answer), sp);

        var fx = Content.FxFor(sp);
        string arch = fx?.Archetype ?? "";

        if (Content.IsMendEquipment(sp))  return Lua(CastUtilArch("mend_equipment", sp, null), sp);
        if (Content.IsJuJakEvocation(sp)) return Lua(CastUtilArch("jujak_evocation", sp, null), sp);
        if (Content.IsHyunMooRevival(sp)) return Lua(CastUtilArch("hyunmoo_revival", sp, null), sp);

        // Mana-battery family (Invoke / Spirit's Power / …) always runs the verbatim RTK formula, whether the
        // export tagged it ManaBattery or we recognise its base identifier (belt-and-suspenders for export gaps).
        if (arch == "ManaBattery" || Content.BaseKey(sp) is "invoke" or "spirits_power" or "life_force" or "gather_magic")
            return Lua(CastUtilArch("mana_battery", sp, null), sp);

        if (fx is null) return Lua(CastUtilArch("generic", sp, targetId), sp);   // no export row — keyword classifier fallback

        // Cooldown (RTK "aether"), if this spell has one and it's still ticking.
        if (fx.Aether > 0 && OnCooldown(sp.Key, out int wait))
        { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }

        int mana = fx.Mana > 0 ? fx.Mana : 5;   // real per-spell cost; 5 only if the export had none

        // Backstab/Flank (Warrior-only skills — RTK Spells.csv has both at SplPthId=1, no other class learns
        // either; user: "Flank and Backstab come from the warrior spells that provide those abilities. I
        // think only warriors ever get them"): a boolean combat STANCE (RTK player.backstab/player.flank),
        // not a numeric stat — CastBuff's generic BuffStat/BuffAmt loop can't express it (this spell's export
        // row has neither set), so it silently no-opped before. RTK tags every alignment variant of both
        // (backstab_warrior/back_battle_warrior/back_attack_warrior/back_damage_warrior, and the flank
        // equivalents) with CureCat "backstabs"/"flanks" — a clean, data-driven way to catch all 4 aliases of
        // each without hardcoding 8 identifiers.
        if (fx.CureCat == "backstabs") return Lua(CastStanceArch("stance_backstab", sp, fx, mana, 0), sp);
        if (fx.CureCat == "flanks")    return Lua(CastStanceArch("stance_flank",    sp, fx, mana, 0), sp);

        // Rage tiers (Wolf's/Tiger's/Dragon's Fury, Baekho's Rage — Warrior AND Rogue; user: "they get rage
        // spells until 99 I think") and Rogue's Invisible/Spirit's Form/Life's Cloak/Glass Form (sneak-attack
        // multiplier — user: "rogue invis also has a damage multiplier"): same class of gap as Backstab/Flank
        // above — neither has a numeric BuffStat/BuffAmt the generic CastBuff loop can express, so both
        // silently no-opped (spent mana, printed a message, did nothing) before this pass.
        // (Chung Ryong's Rage — an INCREMENTAL fury that climbs tier 1→6 on recast — used to be intercepted
        // here by a hardcoded key check. It is the same shape as Baekho's Cunning, which reaches its verb
        // through a SpellParams row, so it now does too: both bind at the top of ApplyCast and never reach
        // this dispatch at all. That also keeps it away from the flat CastRage path below, whose "already
        // benefiting from a fury" block would forbid the climb — the reason for the old interception.)
        if (Content.RageAmountFor(sp) is int rageAmt) return Lua(CastStanceArch("stance_rage", sp, fx, mana, rageAmt), sp);
        if (Content.IsStealthSpell(sp))
        {
            // The guard lives HERE (not in CastStealth) because the dispatch prefers the Lua verb
            // `stance_stealth`, which bypasses CastStealth entirely — so anything only CastStealth did would
            // silently not happen. Already invisible? Re-casting is a no-op (matches morph), no mana spent.
            //
            // NO COOLDOWN ARM. There used to be a `SetCooldown(sp.Key, fx.Aether)` here, fed by a 1000ms
            // aether on all four stealth rows; both are gone (the CSV column is now blank). Live gives no
            // cooldown warning for Invisible at all — its only three lines are "You cast Invisible.",
            // "You already cast that spell." and "You are no longer invisible." — because the 1s wait is not
            // a cooldown, it is the shared cast/swing slot (Content.CastDelayMs), which drops silently. The
            // aether column was where our extractor happened to record that delay, and keeping it produced a
            // bogus fourth message, "Invisible isn't ready yet (0s).".
            // TRANSFORMED GATE. While morphed (Feral and the rest of the animal forms — see
            // Content.MorphSpells/MorphDispatchSpells) the disguise IS a 0x07 creature sprite, so the faded
            // stealth form can't co-exist with it; live refuses the cast rather than silently dropping it.
            // Placed before the "already cast" no-op so a morphed rogue mashing Invisible always hears why.
            if (IsMorphed) { SendMiniText("You can't cast that spell now."); return false; }
            if (Stealthed) { SendMiniText("You already cast that spell."); return false; }
            _stealthName = sp.Name;
            return Lua(CastStanceArch("stance_stealth", sp, fx, mana, 0), sp);
        }
        if (Content.EnchantFor(sp) is (double enchantAmt, int enchantMana)) return Lua(CastStanceArch("stance_enchant", sp, fx, enchantMana, enchantAmt), sp);

        // The rest of this pass (self-sacrifice strikes, mana steal/gift, cleanse, revive, short leap): none
        // of these have a numeric BuffStat/BuffAmt or a damage amountExpr the generic archetypes can express
        // either (their CSV rows are all bare "Utility"), and each manages its own real RTK mana cost/cooldown
        // internally rather than trusting the generic `mana`/`fx.Aether` values above (which are blank for
        // all of them in the export — see each method's own hardcoded RTK constant).
        if (Content.SacrificeFamilyFor(sp) is Content.SacrificeFamily)
        {
            bool okSac = Lua(CastWorldArch("sacrifice", sp, targetId, answer), sp);
            if (okSac && Content.OverheadShoutFor(sp) is string sacShout) Shout(sacShout);   // "K'YA~!" / "Sa-AAA~~!" / "Ka~!" / "Ka~~!"
            return okSac;
        }
        if (Content.IsManaStealSpell(sp)) return Lua(CastUtilArch("mana_steal", sp, targetId), sp);
        if (Content.IsManaGiftSpell(sp)) return Lua(CastUtilArch("mana_gift", sp, targetId), sp);
        if (Content.IsCleanseSpell(sp)) return Lua(CastUtilArch("cleanse", sp, targetId), sp);
        if (Content.IsReviveSpell(sp)) return Lua(CastUtilArch("revive", sp, targetId), sp);
        if (Content.IsLeapSpell(sp)) return Lua(CastUtilArch("leap", sp, null), sp);
        if (Content.IsAmbushSpell(sp)) return Lua(CastWorldArch("ambush", sp, targetId, answer), sp);


        // Morph family (see Content.MorphSpells/MorphDispatchSpells): question-dispatched ones (feral_rogue,
        // gangrel_rogue/mage, rodent_rogue, beast_rogue/mage, druids_rodent, wilderness_guise) resolve their
        // look from the typed answer; the rest are fixed alignment reskins.
        if (Content.MorphDispatchFor(sp) is (Dictionary<string, ushort> morphAnswers, int mdMana, int mdDur))
        {
            var morphChoice = (answer ?? "").Trim();
            if (!morphAnswers.TryGetValue(morphChoice.ToLowerInvariant(), out var mLook))
            {
                // Nothing typed -> just re-prompt (client also shows its own SplQuestion). A wrong choice was
                // typed -> list the acceptable forms in the minitext box, one per line (RTK feral.lua itself
                // just silently return()s on a bad answer; this is a friendlier server-side addition).
                if (morphChoice.Length == 0) SendMiniText("Become what?");
                else
                {
                    SendMiniText("Selectable animals...");
                    var ti = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                    foreach (var name in morphAnswers.Keys) SendMiniText(ti.ToTitleCase(name));
                }
                return false;
            }
            return Lua(CastMorphArch(sp, mLook, 0, mdMana, mdDur, targetId), sp);
        }
        if (Content.MorphFor(sp) is (ushort morphLook, ushort morphLookF, int morphMana, int morphDur))
            return Lua(CastMorphArch(sp, morphLook, morphLookF, morphMana, morphDur, targetId), sp);

        // AREA (4-way) spells — the mage zap ladder and the poet heal ladder. They carry a perfectly good
        // Damage/Heal export row (the formula and the per-alignment graphics are all correct), so they reach
        // the archetype path below and get aimed at a single target that doesn't exist. Intercept here and run
        // the area verb against the SAME pre-evaluated amount, with the real per-family mana the export
        // couldn't see (Content.AreaSpellFor explains why it reads 0).
        if (Content.AreaSpellFor(sp) is (string areaVerb, int areaMana))
            return Lua(CastArch(areaVerb, sp, fx, null, areaMana), sp);

        // The dog 5-way (Fissure / Lava Surge). Same reason for intercepting here as the 4-way above: their
        // export rows are perfectly good Damage rows, so they reached the single-target archetype and hit
        // exactly one thing instead of five. Their mana IS correct in the export (120 / 210), so unlike the
        // 4-way family there is no side table — `mana` as computed above is right.
        if (Content.IsTargetAreaZap(sp))
            return Lua(CastArch("target_area_zap", sp, fx, targetId, mana), sp);

        // Read the pool BEFORE the archetype spends anything: the post-cast drain below is a fraction of what
        // you were holding when you cast, not of what's left after the row's own mana cost came out (RTK
        // hellfire.lua computes its manaTaken on the line above its global_zap call, for exactly that reason).
        uint preCastMp = _char.Mp;

        // Archetype dispatch — every archetype now runs its `arch_<name>` verb in spell_verbs.lua. There is no
        // C# handler behind any of them any more: the whole spell system is scriptable and hot-reloadable, and
        // Lua() turns "no such verb" into a visible failed cast rather than a silent fallthrough.
        bool ok = arch switch
        {
            "Damage"     => Lua(CastArch("arch_damage", sp, fx, targetId, mana), sp),
            "Heal"       => Lua(CastArch("arch_heal", sp, fx, targetId, mana), sp),
            "Buff"       => Lua(CastArch("arch_buff", sp, fx, null, mana), sp),
            "TargetBuff" => Lua(CastArch("arch_targetbuff", sp, fx, targetId, mana), sp),
            "Debuff"     => Lua(CastArch("arch_debuff", sp, fx, targetId, mana), sp),
            "Cure"       => Lua(CastArch("arch_cure", sp, fx, null, mana), sp),
            // Utility / Summon / Teleport / Dialog — no numeric effect to express, so the whole handler is
            // "debit the mana"; the caster line comes from HandleCast. 137 of the 640 exported spells land here.
            _            => Lua(CastArch("misc", sp, fx, targetId, mana), sp),
        };
        // Overhead cast shout for the strikes that run the generic Damage archetype rather than the sacrifice
        // verb — Assault and its reskins ("Assault~!"). The sacrifice four shout at their own dispatch above.
        if (ok && Content.OverheadShoutFor(sp) is string archShout) Shout(archShout);
        // Pool-fraction spells (Content.PostCastManaDrainFor — the whole pool for Inferno/Dooms Fire and the
        // Retribution family, 70% for Hellfire's, a third for Restore) spend their share AFTER the damage or
        // heal is computed, from the SAME pre-cast reading the amount came from. Which is the point: these
        // scale off the pool at both ends, so taking the cost first would quietly halve the effect. Floors at
        // zero rather than underflowing, matching RTK's own guard.
        var (drainPct, gateOnly) = Content.PostCastManaDrainFor(sp);
        if (ok && drainPct > 0)
        {
            // Restore's 1000 was a bar to clear, not a bill — the archetype charged it anyway, so put it back
            // before taking the real share (never above what the cast started with).
            if (gateOnly) _char.Mp = Math.Min(preCastMp, _char.Mp + (uint)Math.Max(0, mana));
            uint drain = (uint)Math.Floor(preCastMp * drainPct);
            _char.Mp = drain >= _char.Mp ? 0 : _char.Mp - drain;
            MarkDirty(); SendStats();
        }
        // Same shape on the vita side (Content.PostCastVitaKeepFor): Slash keeps 90% of your health, the
        // Assault family half. Only on a cast that landed — `ok` is false for a swing that found nothing, which
        // is exactly the branch RTK guards these with. Ceiling so it can never take the last point and kill you.
        double vitaKeep = Content.PostCastVitaKeepFor(sp);
        if (ok && vitaKeep < 1.0)
        {
            _char.Hp = (uint)Math.Max(1, Math.Ceiling(_char.Hp * vitaKeep));
            MarkDirty(); SendStats();
        }
        if (ok && fx.Aether > 0) SetCooldown(sp.Key, fx.Aether);
        return ok;
    }

    // Archetype Lua hook: if spell_verbs.lua defines `verb` (e.g. arch_damage), evaluate this spell's real
    // formula (spell_effects.csv amountExpr — no target term exists in any formula, so SpellVars(null) matches
    // what the C# archetype computed) and run the verb with the amount + mana pre-supplied. Returns the verb's
    // success bool, or null if the verb isn't loaded, which Lua() below fails cleanly (there is no C# handler
    // left to fall back to). A verb that errors mid-run returns false (not null) so a failed retry can't double-apply.
    private bool? CastArch(string verb, SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (!SpellScript.HasVerb(verb)) return null;
        double amount = Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(null)));
        // Chin-Baek-Ho-Ryung (Black Potion, 10s): x1.5 on Slash / Assault / Feral Berserk, applied to the
        // evaluated amount exactly where RTK applies it — right after the damage is computed and before
        // anything is charged. Berserk and Whirlwind take the same bonus inside the sacrifice verb.
        if (Content.TakesChinBaekHoRyung(sp) && HasStatusFlag(Content.ChinBaekHoRyung))
            amount = Math.Ceiling(amount * 1.5);
        return SpellScript.Run(verb, new SpellContext(this, sp, targetId, null, amount, mana, fx));
    }

    // Stance Lua hook (Tier-2 migration): rage / enchant / stealth / backstab / flank all just ARM a timed melee
    // modifier on the caster, so — like CastArch — try the Lua verb first; a null result (verb not loaded) fails
    // the cast via Lua() below, same as CastArch — no C# handler remains. The C# classifier has already picked
    // the spell + its RTK numbers, passed via ctx: `amount`
    // carries the rage/enchant multiplier (0 for the flag-only stealth/backstab/flank), `mana` the resolved cost,
    // and `fx` the export row (ctx.durationMs). No per-spell formula to evaluate (unlike CastArch's amountExpr).
    private bool? CastStanceArch(string verb, SpellDef sp, SpellFx fx, int mana, double amount)
    {
        if (!SpellScript.HasVerb(verb)) return null;
        return SpellScript.Run(verb, new SpellContext(this, sp, null, null, amount, mana, fx));
    }

    // Tier-3 utility Lua hook (mana_steal/mana_gift/cleanse/revive/leap/mana_battery): these spells are classified
    // in C# (Content.IsManaStealSpell etc.), not by a SpellParams row, so the verb runs against an empty row and
    // reads its RTK constants as `row.x or <default>` (fully tunable later by adding a row). targetId reaches the
    // verb through ctx (its target primitives). Returns null only if the verb isn't loaded, which Lua() below
    // fails cleanly (no C# handler remains); a Lua error returns false so a half-applied mana transfer can't
    // be re-run and duplicated.
    private bool? CastUtilArch(string verb, SpellDef sp, uint? targetId)
    {
        if (!SpellScript.HasVerb(verb)) return null;
        return SpellScript.Run(verb, new SpellContext(this, sp, targetId, null, 0, 0, null));
    }

    // Tier-4 world-effecting Lua hook (gateway/return_home/divine/spot_traps/filch/set_trap/bladestorm/pet_summon/
    // propose): like CastUtilArch but carries the typed `answer` (Gateway's N/E/S/W, the set_trap dispatcher's
    // trap name). Data-bound constants reach the verb via ctx.spellMana/petMana/etc., not a CSV row. Returns null
    // only if the verb isn't loaded, which Lua() below fails cleanly (no C# handler remains); a Lua error
    // returns false, so a world mutation that already ran (a warp, a spawn) can't be re-applied.
    /// <summary>Collapse a verb result to a plain success bool. Every spell dispatch now runs through Lua, so
    /// there is no C# handler left to fall back to and a null (= the verb isn't defined) can only mean
    /// spell_verbs.lua never loaded at all - <see cref="LuaVerbHost.Load"/> keeps the last good copy across a
    /// bad @reload, so this is a startup/deploy failure, not a scripting typo. Fail the cast with a visible
    /// notice and a log line rather than silently doing nothing and charging no mana.</summary>
    private bool Lua(bool? result, SpellDef sp)
    {
        if (result.HasValue) return result.Value;
        SendMiniText($"{sp.Name} is unavailable right now.");
        Log.Warn($"no Lua verb for spell '{sp.Key}' - is spell_verbs.lua loaded?");
        return false;
    }

    private bool? CastWorldArch(string verb, SpellDef sp, uint? targetId, string? answer)
        => SpellScript.Run(verb, new SpellContext(this, sp, targetId, answer));

    // Morph hook: the C# dispatch has already resolved this cast's look/female-look/mana/duration (answer-picked
    // forms), so stage that plan on the session and run the `morph` verb against it; the verb owns the guards.
    private bool? CastMorphArch(SpellDef sp, ushort look, ushort lookF, int mana, int dur, uint? targetId)
    {
        if (!SpellScript.HasVerb("morph")) return null;   // stage nothing if Lua isn't going to run
        LuaSetPendingMorph(look, lookF, mana, dur);
        return SpellScript.Run("morph", new SpellContext(this, sp, targetId, null));
    }

    // ---- Lua spell-verb primitives (called by SpellContext; see Server/SpellScript.cs) --------------------
    // Thin internal wrappers over the SAME plumbing the C# CastX methods use, so the Lua route can't drift into
    // a second combat/heal implementation. Effective (base+gear+buff) stats, matching CastDamage/CastHeal.
    internal int  LuaLevel => _char.Level;
    internal int  LuaWill  => _char.Will + Totals().will;
    internal int  LuaGrace => _char.Grace + Totals().grace;
    internal int  LuaMight => EffMight;
    internal uint LuaHp    => _char.Hp;
    internal uint LuaMaxHp => EffMaxHp;
    internal uint LuaMp    => _char.Mp;
    /// <summary>A cast that resolves to nothing says <b>NOTHING</b> to the player — it only logs.
    /// <para>Aiming at empty air is a miss, not an error. The refusal already suppresses the "You cast X."
    /// line and the cast animation, and that absence IS the feedback; a "&lt;spell&gt; finds no target."
    /// notice on top of it was our invention, fired constantly during ordinary play (every zap thrown a beat
    /// after something died), and read as a malfunction rather than a miss.</para>
    /// The log line stays: "nothing happened and nothing was said" is the hardest state to diagnose from a
    /// bug report, so the server side keeps saying which spell found nothing and where.</summary>
    private void LogNoTarget(SpellDef sp) =>
        Log.Info($"      -x {sp.Name}: no target at ({_char.X},{_char.Y}) facing {_facing} — silent refusal");

    internal bool LuaHasTarget(uint? targetId) => ResolveCastTarget(targetId) is not null;

    /// <summary>Is there anything a DAMAGE spell could land on — a mob, a peer, or (in a PvP map, unaimed)
    /// yourself? Distinct from <see cref="LuaHasTarget"/>, which resolves mobs only and would therefore
    /// refuse a legal self-zap before it ever reached the damage path.</summary>
    internal bool LuaHasDamageTarget(uint? targetId)
    {
        var (m, p) = ResolveDamageTarget(targetId);
        return m is not null || p is not null;
    }

    internal bool LuaSpendMana(int amt, SpellDef sp)
    {
        if (amt < 0) amt = 0;
        if (_char.Mp < (uint)amt) { SendMiniText($"Not enough mana to cast {sp.Name}."); return false; }
        _char.Mp -= (uint)amt;
        SendStats();
        return true;
    }

    // ---- ARMOR ON SPELL DAMAGE ------------------------------------------------------------------------------
    // A damage spell takes the target's physical AC, exactly like a swing does. LIVE-MEASURED: the Spark probe
    // solves 19/19 readings with ZERO residual as floor((50 + level/2) * (1 + ac/100)) — across 9 mobs spanning
    // AC 40-100, and on 10 self-casts with the caster's own AC varied by swapping gear. So `1 + ac/100` is an
    // exact reproduction of live behaviour for MAGIC, not only for melee. Positive AC amplifies (a rabbit at
    // +100 takes double); negative resists (a marsh ogre at -65 takes 35%).
    //
    // This was missing entirely until 2026-08-21: Combat.ApplyArmor's callers were melee, mob-on-player hits,
    // the sacrifice strikes and overflow — never a damage spell — so every nuke in the game dealt face value
    // to any mob. Applied at the mob-side sites because World.TryDamage deliberately knows nothing about
    // armor; the PLAYER side is centralised one layer down in ReceiveSpellDamage. Exactly once either way.
    //
    // DELIBERATELY NOT APPLIED to: the sacrifice strikes and overflow (they net their own, see LuaSacApply);
    // ambush and melee (PlayerSwingDamage nets attacker-side); traps and pet mob-on-mob hits (no measurement
    // exists for either, and guessing would move balance with nothing behind it).
    private static int SpellNet(int amt, Mob mob) => Combat.ApplyArmor(amt, mob.Ac, floor: -95);

    internal bool LuaDamageTarget(int amt, SpellDef sp, uint? targetId)
    {
        var (mob, pc) = ResolveDamageTarget(targetId);
        if (mob is null && pc is null) { LogNoTarget(sp); return false; }   // silent — see LogNoTarget
        if (pc is not null) return HitPlayerWithSpell(pc, amt, 0, sp);   // PvP / self-cast (verb already spent mana)
        if (sp.CanFail && RollDeflect(mob!)) { SendMiniText("Your magic has been deflected."); return true; }
        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        int net = SpellNet(amt, mob!);
        if (_world.TryDamage(_char.Map, mob!, net, out bool died, _char.Id))
        {
            if (fx is not null) BroadcastFx(mob!.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            ShowDamageResult(mob!.Id, mob, died);
            if (died)
            {
                uint reward = (uint)(mob!.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardKillExp(reward, _char.Map, mob!.X, mob.Y, mob.Key);   // AwardExp shows "+N experience"; no separate caster flavor
            }
            Log.Info($"      (lua) {sp.Name} -> mob {mob!.Id} '{mob.Name}' for {net} (raw {amt}, ac {mob.Ac}, died={died})");
        }
        return true;
    }

    // Full magic-attack sequence for the archetype Lua path (arch_damage) — a faithful port of CastDamage's body:
    // mana check FIRST, then resolve target, then the deflect roll (which spends NO mana, RTK-correct), then debit
    // mana, then apply. Takes a pre-evaluated amount (the engine already ran the spell_effects.csv formula) so the
    // verb stays pure logic. Returns false only when the cast can't happen (no mana / no target); a deflect still
    // returns true (the cast "happened", just resisted). Byte-identical to CastDamage so the Lua route can't drift.
    internal bool LuaMagicDamage(int amt, int mana, SpellDef sp, uint? targetId)
    {
        if (mana < 0) mana = 0;
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        var (mob, pc) = ResolveDamageTarget(targetId);
        if (mob is null && pc is null) { LogNoTarget(sp); return false; }   // silent — see LogNoTarget
        if (pc is not null)   // PvP / self-cast
        {
            // Read position and HP BEFORE the blow: a kill drops them to ghost form, and any overflow is
            // centred on where the strike landed.
            uint prePcHp = pc._char.Hp;
            int px = pc._char.X, py = pc._char.Y;
            if (!HitPlayerWithSpell(pc, amt, mana, sp, out int landedPvp)) return false;
            TryArchetypeOverflow(sp, landedPvp - (int)prePcHp, px, py);
            return true;
        }
        // A DEFLECT STILL COSTS THE MANA. The spell was cast and the power left you; the target resisting it
        // is their achievement, not a refund. (RTK returns before its debit here, but a free deflect means a
        // resistant target costs nothing to keep hammering, which drains the mechanic of any meaning.)
        _char.Mp -= (uint)mana;
        if (sp.CanFail && RollDeflect(mob!)) { SendStats(); SendMiniText("Your magic has been deflected."); return true; }
        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        int preMobHp = (int)mob!.Hp;
        int mx = mob.X, my = mob.Y;
        int net = SpellNet(amt, mob!);
        if (_world.TryDamage(_char.Map, mob!, net, out bool died, _char.Id))
        {
            if (fx is not null) BroadcastFx(mob!.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            ShowDamageResult(mob!.Id, mob, died);
            if (died)
            {
                uint reward = (uint)(mob!.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardKillExp(reward, _char.Map, mob!.X, mob.Y, mob.Key);   // AwardExp shows "+N experience"; no separate caster flavor
            }
            Log.Info($"      (lua-arch) {sp.Name} -> mob {mob!.Id} '{mob.Name}' for {net} (raw {amt}, ac {mob.Ac}, died={died})");
            // Overkill off the NET figure, which is what the FAQ's own worked example uses.
            TryArchetypeOverflow(sp, net - preMobHp, mx, my);
        }
        return true;
    }

    /// <summary>Overflow for the two strikes that splash but do NOT run <c>verbs.sacrifice</c> — Slash and
    /// Feral Berserk (<see cref="Content.OverflowsFromDamageArchetype"/>). Era-gated identically to the
    /// sacrifice families' <see cref="LuaOverflow"/>, so it is inert at the default 2001-07-09 date, and a
    /// no-op for every other spell on this path.</summary>
    ///
    /// <remarks>ON UNITS: the caller must pass POST-ARMOR overkill, which is what the FAQ's worked example
    /// uses — it nets a Whirlwind to 787,500 against a -50 AC target and calls 287,500 of that excess. That
    /// used to be impossible here, because the Damage archetype applied no armor at all; since
    /// <see cref="SpellNet"/> it does, and both call sites hand over a netted figure. (One simplification
    /// shared with <see cref="LuaSacApply"/>: neither accounts for <c>TryDamage</c> amplifying the blow
    /// afterwards against a sleeping target, so overkill slightly understates in that case.)</remarks>
    private void TryArchetypeOverflow(SpellDef sp, int overkill, int x, int y)
    {
        if (overkill < 1) return;
        if (!Content.OverflowsFromDamageArchetype(sp)) return;
        if (!Era.Has(Era.WarriorOverflow)) return;
        ApplyOverflow(overkill, x, y, sp);
    }

    // ---- AREA (4-way) spells: the mage zap ladder and the poet heal ladder --------------------------------
    // RTK Spells/mage/{erupt,ion_charge,explode,electrocute,tempest}.lua and Spells/poet/{vital_spark,anoint,
    // remedy,heavens_kiss}.lua, each with four alignment reskins — 36 spells that share one shape:
    //
    //     local x = {-1, 0, 1, 0}; local y = {0, -1, 0, 1}
    //     ...spend the mana up front...
    //     for i = 1, 4 do <whatever stands on that cell gets hit/healed> end
    //     player:sendMinitext("You cast <name>.")
    //
    // Three things fall straight out of that and are the whole reason they used to answer "finds no target":
    //  * they are SplType 5 — no target argument exists, so the single-target archetype had nothing to aim at
    //    and bailed before spending anything;
    //  * the mana is spent BEFORE the loop and the cast line prints AFTER it unconditionally, so casting at
    //    empty air is a legal (if wasteful) cast, not a failure;
    //  * the caster's OWN cell is never scanned, so these can't hit you.
    //
    // Cells are scanned in RTK's own order (W, N, E, S) and only the FIRST occupant of each is taken, matching
    // its `target[1]`. Returns how many were affected purely for the log.

    private static readonly (int dx, int dy)[] AreaCells = { (-1, 0), (0, -1), (1, 0), (0, 1) };

    /// <summary>The 4-way zap: damage every creature on a cardinally-adjacent cell. Mobs always; a PLAYER only
    /// on a PvP map (RTK gates its PC branch on <c>canPK</c>) — off one they are simply skipped, silently,
    /// exactly as an empty tile is. Each victim gets the spell's own animation over it, an HP bar, and, if it
    /// dies, its exp.</summary>
    internal int LuaAreaZap(int amt, SpellDef sp)
    {
        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;
        bool pvp = Content.IsPvpMap(_char.Map);
        int hitCount = 0;
        foreach (var (dx, dy) in AreaCells)
        {
            int cx = _char.X + dx, cy = _char.Y + dy;
            if (cx < 0 || cy < 0) continue;
            var mob = _world.MobAt(_char.Map, (ushort)cx, (ushort)cy);
            if (mob is not null)
            {
                if (mob.IsNpc) continue;                       // NPCs are indestructible — don't waste the beat on them
                if (sp.CanFail && RollDeflect(mob)) continue;  // resisted this one; the others still land
                // Per-victim armor (SpellNet) — each cell's occupant nets the same raw amount against its OWN
                // AC, so a 4-way can land four different numbers.
                if (_world.TryDamage(_char.Map, mob, SpellNet(amt, mob), out bool died, _char.Id))
                {
                    BroadcastFx(mob.Id, anim, snd);
                    ShowDamageResult(mob.Id, mob, died);
                    if (died) AwardKillExp((uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp), _char.Map, mob.X, mob.Y, mob.Key);
                    hitCount++;
                }
                continue;
            }
            var peer = _world.PeerAt(_char.Map, (ushort)cx, (ushort)cy);
            if (peer is null || ReferenceEquals(peer, this) || !pvp) continue;
            if (sp.CanFail && RollDeflectPvp(peer)) continue;
            BroadcastFx(peer._char.Id, anim, snd);
            peer.ReceiveSpellDamage(amt, this, sp.Name);
            hitCount++;
        }
        Log.Info($"      (lua) {sp.Name} -> 4-way zap {amt} dmg, {hitCount} target(s)");
        return hitCount;
    }

    // The dog 5-way (Fissure / Lava Surge). RTK's offsets, in RTK's order — note the leading {0,0}, which is
    // what makes this five cells and not four: the target's own tile is hit as well as its neighbours.
    private static readonly (int dx, int dy)[] TargetAreaCells = { (0, 0), (-1, 0), (0, -1), (1, 0), (0, 1) };

    // Fissure/Lava Surge miss ONLY on range. Head Tutor Nussan's board entry is the whole specification:
    // "Ranged, targetable 5 way attack. Cast on yourself or monsters. Misses sometimes if you're too far
    // away. When cast on the target, anything on the 4 sides gets hit as well, full damage. Can be cast
    // extremely fast." — so no flat deflect roll (an earlier draft of this method used sp.CanFail/RollDeflect,
    // which is the wrong failure mode), no damage falloff on the neighbours, and no cast delay.
    //
    // THE RAMP BELOW IS INFERRED. The corpus gives no percentage anywhere, only "sometimes if you're too far
    // away", so the shape is ours: certain within FissureTrueRange, then climbing linearly to FissureMaxMiss
    // at the engine's own reach limit. That limit is NOT invented — RTK's global_zap enforces
    // `distanceSquare(player, target, 10)`, a 10-tile square, on every zap.
    private const int FissureTrueRange = 3;      // never misses this close
    private const int FissureMaxRange  = 10;     // RTK global_zap's own cap
    private const double FissureMaxMiss = 0.50;  // miss chance at the cap

    /// <summary>The dog 5-way fire: damage everything on the target's tile and its four neighbours, at FULL
    /// damage on every cell. Unlike <see cref="LuaAreaZap"/> the sweep is centred on the TARGET, so it reaches
    /// across the room; aimed at yourself (or at nothing) it centres on you, which the tutor explicitly allows
    /// ("Cast on yourself or monsters"). Aimed at nothing it is still a legal cast that costs full price — RTK
    /// spends the mana before the loop and prints the cast line after it, unconditionally.</summary>
    internal int LuaTargetAreaZap(int amt, SpellDef sp, uint? targetId)
    {
        var (epMob, epPc) = ResolveDamageTarget(targetId);
        // Self-cast is a documented mode, not a fallback: centre the blast on us and let the four sides catch
        // whatever has closed to melee range.
        int ox = epMob?.X ?? epPc?._char.X ?? _char.X;
        int oy = epMob?.Y ?? epPc?._char.Y ?? _char.Y;

        // One range roll for the whole cast — "misses" is about the spell not reaching, so it can't sensibly
        // land on some cells of the blast and not others. Square distance, matching RTK's own reach test.
        // DOG FAMILY ONLY: the Inferno and Earthquake ladders share this 5-way shape but nothing documents a
        // range miss for them (Inferno is gated by a 70s aether instead), so they get the reach cap and no roll.
        int dist = Math.Max(Math.Abs(ox - _char.X), Math.Abs(oy - _char.Y));
        if (dist > FissureMaxRange)
        { SendMiniText($"{sp.Name} cannot reach that far."); return 0; }
        if (Content.IsDogFireSpell(sp) && dist > FissureTrueRange)
        {
            double miss = FissureMaxMiss * (dist - FissureTrueRange) / (double)(FissureMaxRange - FissureTrueRange);
            if (Random.Shared.NextDouble() < miss)
            { SendMiniText($"{sp.Name} misses."); Log.Info($"      (lua) {sp.Name} -> missed at range {dist} (p={miss:P0})"); return 0; }
        }

        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;
        bool pvp = Content.IsPvpMap(_char.Map);
        int hitCount = 0;
        foreach (var (dx, dy) in TargetAreaCells)
        {
            int cx = ox + dx, cy = oy + dy;
            if (cx < 0 || cy < 0) continue;
            var mob = _world.MobAt(_char.Map, (ushort)cx, (ushort)cy);
            if (mob is not null)
            {
                if (mob.IsNpc) continue;                       // NPCs are indestructible
                // Per-victim armor, same as the 4-way — "full damage" on the neighbours means the same RAW
                // amount, which each still nets against its own AC.
                if (_world.TryDamage(_char.Map, mob, SpellNet(amt, mob), out bool died, _char.Id))
                {
                    BroadcastFx(mob.Id, anim, snd);
                    ShowDamageResult(mob.Id, mob, died);
                    if (died) AwardKillExp((uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp), _char.Map, mob.X, mob.Y, mob.Key);
                    hitCount++;
                }
                continue;
            }
            var peer = _world.PeerAt(_char.Map, (ushort)cx, (ushort)cy);
            if (peer is null || ReferenceEquals(peer, this) || !pvp) continue;
            // No RollDeflectPvp here either: the range roll above is this spell's ONLY failure mode.
            BroadcastFx(peer._char.Id, anim, snd);
            // THIS victim, not the primary target — RTK sends every one of these lines to `target` instead
            // (fissure.lua:36-38), so the centre player collected a line per bystander and the bystanders
            // were told nothing. See Content.TargetAreaZapSpells.
            peer.ReceiveSpellDamage(amt, this, sp.Name);
            hitCount++;
        }
        Log.Info($"      (lua) {sp.Name} -> 5-way zap {amt} dmg around ({ox},{oy}), {hitCount} target(s)");
        return hitCount;
    }

    /// <summary>The 4-way heal: restore HP to every PLAYER on a cardinally-adjacent cell (RTK's poet ladder
    /// scans <c>BL_PC</c> only — these do not heal your pet, and they do not heal you). Each gets the spell's
    /// animation, the "&lt;caster&gt; casts X on you." line, and a refreshed over-head bar.</summary>
    internal int LuaAreaHeal(int amt, SpellDef sp)
    {
        if (amt <= 0) return 0;
        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;
        int n = 0;
        foreach (var (dx, dy) in AreaCells)
        {
            int cx = _char.X + dx, cy = _char.Y + dy;
            if (cx < 0 || cy < 0) continue;
            var peer = _world.PeerAt(_char.Map, (ushort)cx, (ushort)cy);
            if (peer is null || ReferenceEquals(peer, this)) continue;
            peer.ReceiveHeal(amt);
            BroadcastFx(peer._char.Id, anim, snd);
            TellTarget(peer, sp);
            n++;
        }
        Log.Info($"      (lua) {sp.Name} -> 4-way heal {amt}, {n} target(s)");
        return n;
    }

    /// <summary>The Sage ladder's world channel (RTK common/sage.lua
    /// <c>broadcast(-1, "[" .. player.name .. "]: " .. text)</c>): one line to EVERY player on the server, in
    /// the format RTK uses verbatim. This is the whole point of the Share Wisdom ladder — a paid,
    /// cooldown-gated world channel — so it deliberately reaches every map, unlike every other chat path we
    /// have. Empty text is a no-op (RTK guards the same way).
    ///
    /// <para><b>Channel fixed 2026-08-23.</b> This was sending on <see cref="SendMessage"/> — RTK's
    /// <c>0x02</c> LOGIN-BOX packet, reserved for the pre-world flow. It is the same mislabeling the
    /// 2026-07-26 <c>SendMessage</c>→<c>SendMiniText</c> audit swept out of the rest of the server (see
    /// docs §11g); this one arrived after that pass and was missed. Live on 5.33 the shout went out, was
    /// logged, and rendered NOTHING — the client has no in-world widget listening on the login box.
    /// In-world text is <c>0x0A</c> (<c>clif_sendmsg(sd, type, buf)</c>), dispatched by
    /// <see cref="WorldShoutType"/>.</para></summary>
    internal bool LuaWorldShout(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;
        string line = $"[{_char.Name}]: {text}";
        if (line.Length > 250) line = line[..250];
        // Each recipient is entered on their own monitor for their one line (#29 rule 2,
        // Server/Session.State.cs), one at a time — the guard is disposed before the next player, so two peer
        // monitors are never held at once and rule 2 resolves each acquisition on its own.
        //
        // THE LUA GATE IS HELD HERE, and the order is the proven one. This runs from spell_verbs.lua through
        // SpellContext.worldShout, i.e. inside LuaVerbHost.Invoke's `using (Session.EnterScriptGate())`
        // (LuaVerbHost.cs:135), on the caster's read-loop thread which already holds the caster's own monitor.
        // Own monitor -> gate -> a peer's monitor is exactly the shape Tests/SessionActorTests.cs's
        // LuaGateAgainstAPeerMonitorCannotDeadlock pins (b.WithState -> EnterScriptGate -> a.ReceiveHeal), and
        // it cannot cycle because the gate's slow path drops every session monitor this thread holds BEFORE
        // waiting (Session.State.cs:205-208): a thread waiting for the gate holds no monitor, so whoever holds
        // the gate can always take any monitor it needs and finish. That also covers rule 2's descending
        // branch dropping and retaking our own monitor while we hold the gate.
        //
        // Cost at a few hundred players: one UNCONTENDED Monitor.Enter/Exit per recipient, plus one extra
        // Exit/Enter pair on our own monitor for each recipient ranked below us. Tens of nanoseconds each
        // against a packet build and an encrypt per player — this loop was already O(online) and stays so.
        // Rule 1 holds: Online.All() snapshots under World._lock and returns with it released
        // (World.OnlineRegistry.cs:77-81).
        foreach (var p in _world.Online.All()) p.WithState(() => p.SendMiniText(line, WorldShoutType));
        Log.Info($"   -> world shout by {_char.Name}: {text} (0x0A type {WorldShoutType})");
        return true;
    }

    /// <summary>Which <c>0x0A</c> pane/colour the world shout lands in: <b>4, red in the main chat window</b>.
    ///
    /// <para>Sage is red in the real game, and type 4 is the red one — LIVE-SWEPT on the 5.33 client with
    /// <c>@text</c> (2026-08-23) rather than reasoned, because it could not be reasoned: RTK's own
    /// <c>broadcast()</c> takes a type it does not document, and the RTK-Server tree here is Lua/SQL/maps
    /// with no C# side to read it out of. The sweep also found 4 to be entirely absent from our documented
    /// set (0 wisp · 3 mini-status · 5 system · 11 group · 12 clan), which is why the first attempt at this
    /// guessed 5 — that is the LIGHT blue our restart warnings already use (<see cref="SystemAnnounce"/>).
    /// Full observed table: docs/5.x/Wire-Divergences.md §6.11.</para>
    ///
    /// <para><b>Unverified on 4.95.</b> The shout goes to every player regardless of their client, so a 4.95
    /// player may see this land somewhere else. <c>@text</c> works on both — sweep a 4.95 client to settle
    /// it, and if the two disagree this has to become a per-version choice like the rest of §9 of the
    /// divergences doc.</para></summary>
    private const ushort WorldShoutType = 4;

    internal void LuaHeal(int amt, SpellDef sp)
    {
        if (amt > 0)
        {
            _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amt);
            var fx = Content.FxFor(sp);
            if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            byte selfPct = PlayerHpPercent();   // over-head bar, same as taking a hit — see ReceiveHeal
            _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, selfPct, HealBarCritByte));
        }
        SendStats();   // caster message is the central "You cast <name>." — no "restores N HP" flavor
    }

    /// <summary>Heal whoever this cast is AIMED at — another player, yourself, or a MOB (healing your own pet or
    /// a charmed creature is the point of the mob branch). Falls back to the caster when nothing resolves.
    /// <para>Only <b>SplType 2</b> spells ("Which target? >" — Fleshspeak, Heal Others, Mend Wounds, …) are
    /// targeted; a Type-5 self-skill like Touch of Health must NOT be redirected onto whatever you happen to be
    /// facing, so it routes straight to the caster.</para></summary>
    internal void LuaHealTarget(int amt, SpellDef sp, uint? targetId)
    {
        if (sp.Type != 2) { LuaHeal(amt, sp); return; }          // self-skill — never redirect
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (pc is null && mob is null) { LuaHeal(amt, sp); return; }   // aimed at nothing — heal yourself

        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;

        if (pc is not null)
        {
            if (ReferenceEquals(pc, this)) { LuaHeal(amt, sp); return; }
            pc.ReceiveHeal(amt);
            BroadcastFx(pc._char.Id, anim, snd);
            TellTarget(pc, sp);
            Log.Info($"      (lua) {sp.Name} -> healed player {pc._char.Id} '{pc._char.Name}' for {amt}");
            return;
        }

        var targetMob = mob!;
        if (_world.HealMob(targetMob, amt))
        {
            _world.BroadcastWideArea(_char.Map, targetMob.X, targetMob.Y, p => p.DamageOver(targetMob.Id, HpPercent(targetMob), 0));   // refresh its over-head bar
        }
        BroadcastFx(targetMob.Id, anim, snd);
        Log.Info($"      (lua) {sp.Name} -> healed mob {targetMob.Id} '{targetMob.Name}' for {amt} -> {targetMob.Hp}/{targetMob.MaxHp}");
    }

    /// <summary>Raise this player's HP (capped at their effective max), refresh their HUD, and redraw the
    /// over-head bar for the whole map. Cross-session — called on the TARGET's own Session by a healer's cast.
    /// <para>The bar is the same <c>0x13</c> packet a hit sends, with <c>critical = 0</c>. RTK does exactly
    /// this: <c>addHealthExtend</c> reaches <c>clif_send_pc_healthscript(sd, -damage, 0)</c>, i.e. the heal is
    /// a NEGATIVE damage through the identical builder, and 0 is the critical byte it passes. Without it the
    /// healer and the bystanders saw nothing at all — only the healed player's own HUD number moved.</para></summary>
    internal void ReceiveHeal(int amt)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (amt <= 0 || IsDead) return;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amt);
        SendStats();
        byte pct = PlayerHpPercent();
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, pct, HealBarCritByte));
    }

    /// <summary>The <c>0x13</c> critical byte a HEAL carries. RTK passes 0 (see <see cref="ReceiveHeal"/>);
    /// the byte still selects an overlay animation (<c>0x8f − critical</c>), so it is exposed here in case the
    /// 4.95 client draws something unwanted for that id and it needs re-picking live — the HealCrit row in
    /// <c>game-data/ServerTuning.csv</c>.</summary>
    private static byte HealBarCritByte => Content.HealCritByte;

    /// <summary>Current HP of the mob this cast is aimed at (0 if the target isn't a living mob) — Drain reads it
    /// to decide whether the creature is weak enough to absorb, and how much life that yields.</summary>
    internal int LuaTargetMobHp(uint? targetId) => ResolveCastTarget(targetId) is { Alive: true } m ? m.Hp : 0;

    /// <summary>Play THIS spell's own animation/sound on the resolved target rather than on the caster — for
    /// spells whose documented effect is drawn over the victim (Drain's atlas gif is the target's).</summary>
    internal void LuaFxTarget(SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob);
        uint? id = pc?._char.Id ?? mob?.Id;
        if (id is null) return;
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(id.Value, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
    }

    internal void LuaRestoreMana(int amt)
    {
        if (amt <= 0) return;
        _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)amt);
        SendStats();
    }

    internal void LuaSay(string msg)     => SendMiniText(msg);
    internal void LuaMessage(string msg) => SendMessage(msg);

    // ---- extra primitives for COMPOSED spell verbs (e.g. Baekho's Cunning: a stateful multi-tier stance that
    // combines a rage multiplier + deduction + positional stances) — a verb holds its own int state without
    // any bespoke C# handler.
    //
    // THIS IS RTK's `player.registry` AND IT IS PERSISTED, in the same _char.Quests store the NPC side calls
    // registry. It used to be a session-only dictionary on the premise that spell/combat state should reset on
    // relog. That premise is wrong twice over. RTK's registry is character state — chung_ryongs_rage and
    // baekhos_cunning both write it, and both carry a `recast` hook whose entire job is rebuilding the tier
    // from it on relog. And it does not even reset consistently here: the run's DURATION and every effect it
    // armed (rage, deduction, stances) already survive a relog through Session.TimedEffects, so dropping only
    // the tier left Cunning at full Cunning-5 power reading as tier 0 — the next cast charged 3000 for
    // "Cunning 1" and DOWNGRADED the rage multiplier from 8 back to 4.
    internal int  LuaReg(string key)                 => QuestCounter(key);
    internal void LuaSetReg(string key, int v)       => SetQuestStage(key, v);

    // setDuration/hasDuration are RTK's ONE named-timer namespace, and the spell side and the item side both
    // live in it: black_potion sets `chin_baek_ho_ryung` and five warrior strike SCRIPTS read it. This used to
    // be a second dictionary private to the spell system, which is precisely why nothing a potion set was ever
    // visible to a spell (or vice versa) — three separate stores for one RTK concept. It is now the same
    // _statusFlags map the item verbs write, so the two halves see each other exactly as RTK's do.
    internal bool LuaHasDuration(string key)         => HasStatusFlag(key);
    internal void LuaSetDuration(string key, int ms) => SetStatusFlag(key, ms);
    /// <summary>Milliseconds left on a named duration, 0 if it isn't running. What a tiered fury needs to
    /// re-arm its effects onto the run ALREADY BURNING rather than starting a fresh window — RTK's tiered
    /// spells set their duration in the first-cast branch only, so a climb has to inherit what remains.</summary>
    internal int LuaDurationLeft(string key)
        => _statusFlags.TryGetValue(key, out var exp)
           ? (int)Math.Max(0, exp - Environment.TickCount64) : 0;
    internal bool LuaOnCooldown(string key)          => OnCooldown(key, out _);
    internal void LuaSetCooldown(string key, int ms) => SetCooldown(key, ms);
    // Directly arm the rage multiplier (bypasses CastRage's "already raging" guard — Cunning sets its own tier).
    internal void LuaSetRage(int amount, int durMs, string name)  { _rageAmount = amount; _rageUntil = Environment.TickCount64 + durMs; _rageName = name ?? ""; SendStats(); }
    // Arm (on) or clear (off) a positional stance timer (backstab/flank) for durMs.
    internal void LuaStance(string name, bool on, int durMs)
    {
        long exp = on ? Environment.TickCount64 + durMs : 0;
        if (name == "backstab") _backstabUntil = exp;
        else if (name == "flank") _flankUntil = exp;
        else if (name == "fourway") _fourWayUntil = exp;
    }
    internal void LuaFx(int anim, int sound) => BroadcastFx(_char.Id, anim, sound);

    // ---- Tier-2 stance primitives (rage/enchant/stealth verbs) — thin wrappers over the SAME timer fields the
    // C# CastRage/CastEnchant/CastStealth handlers arm, so the Lua route can't diverge. LuaSetRage/LuaStance
    // (above) cover rage + backstab/flank; these add the "already active" guards + the stealth/enchant setters.
    internal bool LuaRageActive    => EffRage > 1;      // RTK blocks casting a fury while one is up (checkIfCast(lesserFuries))
    internal bool LuaEnchantActive => EffEnchant > 1;   // RTK blocks re-casting an enchant while one is up
    internal void LuaSetStealth(int durMs)                 { _stealthUntil = Environment.TickCount64 + durMs; _stealthShown = true; SendStats(); RefreshAppearance(); }
    internal void LuaSetEnchant(double amount)  { _enchantAmount = amount; SendStats(); }

    // Venom DoT (the `venom` verb): resolve the faced/targeted MOB (venoms are mob-only in RTK — a PC/no target
    // gets "It doesn't work") and hand it to World.PoisonMob (the shared poison engine). False if no mob or the
    // mob is already venomed (checkIfCast(venoms)); the verb then spends no mana. Plays the spell's debuff fx.
    // flatTick > 0 makes each tick a FIXED amount instead of MaxHp*1% — RTK's Burn is the odd one out in this
    // family (a hardcoded 1000 per tick, see burn.lua while_cast).
    internal bool LuaApplyVenom(int tickCap, int lowMs, int highMs, SpellDef sp, uint? targetId, int flatTick = 0,
                                int pcDps = 1000, int pcDurMs = 0, int pcPerTick = 0)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        var fx = Content.FxFor(sp);
        int vAnim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int vSnd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;

        // PLAYER target — a FIXED window and a flat rate, both different from the creature side.
        //
        // Two sources, and they agree once you do the arithmetic:
        //   tswolf.com 2001-02-23 (IN ERA — 4.83/4.95, the closest source we have): "A Poisoned Person
        //     receives a 160 SECOND poison that will bring them down to low health, but not kill them."
        //     A person's window is fixed at 160s, NOT the creature's random roll.
        //   NexusAtlas 2002-12 (later, but the only one that quotes a number): "Does 1000 damage a second,
        //     disregarding armor class, on other players."
        //
        // 160s x 1000/s = 160,000 total, against a never-kill clamp. So the RATE only sets how fast you
        // reach the floor, and tswolf's "brought down to low health" is just the steady state of being
        // there. That also answers the "1000/s is brutal until 99" worry — it is self-scaling, not despite
        // the flat rate but because of it: anyone whose pool exceeds ~160k rides the whole window out
        // without ever flooring, so the spell decides fights at low level and merely hurts at cap.
        // Both numbers are `pcDps` / `pcDurMs` in SpellParams.csv — @reload retunes them after a live test.
        if (pc is not null)
        {
            // PvP-gated for ANY player target, self included (see LuaCanCurseTarget for the reasoning).
            if (!Content.IsPvpMap(_char.Map))
            { SendMiniText("You can't attack that target."); return false; }
            if (pc.Poisoned || pc.HasStatusCategory("venoms"))
            { SendMiniText(BlockedStatusMsg(pc.HasStatusFromSpell(sp.Key))); return false; }
            int durMs = pcDurMs > 0 ? pcDurMs : 1 + Random.Shared.Next(lowMs, highMs + 1);
            pc.ReceivePoison(pcDps, durMs, _char.Id, vAnim, sp.Key, sp.Name, pcPerTick);
            if (fx is not null) BroadcastFx(pc._char.Id, vAnim, vSnd);
            if (!ReferenceEquals(pc, this)) TellTarget(pc, sp);
            Log.Info($"      (lua) {sp.Name} -> venom player {pc._char.Id} '{pc._char.Name}' " +
                     $"{(pcPerTick > 0 ? $"{pcPerTick}/tick" : $"{pcDps}/s")} for {durMs}ms");
            return true;
        }

        if (mob is null) { SendMiniText("It doesn't work."); return false; }
        // The animation is handed to the poison engine as well as played once here: RTK's while_cast_1500
        // re-sends it on EVERY damage tick, so the venom keeps flashing for its whole window. Sound is
        // deliberately not repeated — a zap sfx every 1.5s for 30s is unbearable; only the graphic loops.
        if (!_world.PoisonMob(mob, tickCap, lowMs, highMs, _char.Id, flatTick, vAnim, spellKey: sp.Key))
        { SendMiniText(BlockedStatusMsg(_world.MobStatusKey(mob, "venoms") == sp.Key)); return false; }
        if (fx is not null) BroadcastFx(mob.Id, vAnim, vSnd);
        Log.Info($"      (lua) {sp.Name} -> venom mob {mob.Id} '{mob.Name}' tick {(flatTick > 0 ? $"flat {flatTick}" : $"MaxHp*1% cap {tickCap}")}");
        return true;
    }

    // (Blind used to have its own primitive here. It is now one `debuff` kind among four, all of them routed
    // through LuaHoldTarget above — same exclusivity slot, same boss cap, same messages. The old version's PC
    // branch is gone with it: RTK's blind/paralyze/doze all answer "It doesn't work." to a BL_PC, and a
    // player-side slot that had no mechanical effect at all was only ever bookkeeping.)

    // Endear / mind control (the `endear` verb — RTK poet endear.lua + its possess_soul/charm_life/align_follower
    // clones, and the NPCs/endear.lua the Charm weapon procs). Takes a mob that is ALREADY in the world and makes
    // it yours for a while: same OwnerId plumbing a CotW summon uses, so it counts against your pet cap and stops
    // being a valid target for you. Deliberately leaves Mob.Summoned false, so when the timer lapses World.Tick
    // hands it back to the world (RTK's uncast: `mob.owner = 0; mob.target = 0`) instead of despawning it.
    // RTK's guards, in order: must be a mob, not a boss ("Your will is too weak."), not already owned by anyone.
    internal bool LuaCharmTarget(int durMs, SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (mob is null || pc is not null) { LogNoTarget(sp); return false; }
        if (mob.IsNpc) { SendMiniText("It doesn't work."); return false; }
        if (mob.IsBoss) { SendMiniText("Your will is too weak."); return false; }
        if (!_world.CharmMob(mob, _char.Id, durMs))
        { SendMiniText("A spell of this type is already cast."); return false; }
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        Log.Info($"      (lua) {sp.Name} -> charmed mob {mob.Id} '{mob.Name}' for {durMs}ms (owner {_char.Id})");
        return true;
    }

    /// <summary>Amnesia (RTK Spells/rogue/amnesia.lua): the mob FORGETS you — your threat on it is wiped and
    /// it will not pick you as a target again until the spell lapses, though it keeps fighting everyone else.
    /// A boss shrugs it off in five seconds (RTK's own <c>if target.isBoss == 1 then duration = 5000</c>).
    /// Hitting the creature again breaks it (World.TryDamage).
    /// <para>This is the spell's REAL effect. Before the threat table existed there was nothing for it to act
    /// on, so it fell through to its spell_effects archetype row — which the extractor had classified as
    /// Debuff/"slow", i.e. it quietly applied a slow to the mob and nothing else.</para></summary>
    internal bool LuaAmnesia(int durMs, int bossDurMs, int chance, SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (mob is null || pc is not null) { LogNoTarget(sp); return false; }
        if (mob.IsNpc) { SendMiniText("It doesn't work."); return false; }

        // Fail rate: the cast happened (the verb debits mana on this `true`), it just didn't take hold. No
        // status and no re-cast lock, so a miss just means cast it again.
        if (chance < 100 && Random.Shared.Next(100) >= chance)
        { SendMiniText($"{mob.Name} shakes off the spell."); return true; }

        int dur = mob.IsBoss ? bossDurMs : durMs;
        _world.ForgetPlayer(mob, _char.Id, dur);

        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"You cast {sp.Name} on {mob.Name}");   // RTK's own wording, no trailing full stop
        Log.Info($"      (lua) {sp.Name} -> mob {mob.Id} '{mob.Name}' forgot player {_char.Id} for {dur}ms");
        return true;
    }

    /// <summary>Confuse (RTK mage/confuse.lua): a FAIL-RATE aggro RESET, distinct from Amnesia's per-caster
    /// peel. On success the mob's whole threat table is wiped and it forgets everyone (World.ConfuseMob); if
    /// another creature is on an adjacent tile the confused mob turns on IT, so blinding two mobs side by side
    /// and spamming Confuse sets them fighting each other. Nothing is applied on a miss. No status, no timer,
    /// so it never reports "already cast". Mob-only.
    /// <para>Returns true on BOTH a hit and a fizzle (the verb debits mana either way — re-casting until it
    /// lands must cost something); false only when there is no legal target, in which case no mana is spent.</para></summary>
    internal bool LuaConfuse(int chance, SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (mob is null || pc is not null) { LogNoTarget(sp); return false; }
        if (mob.IsNpc) { SendMiniText("It doesn't work."); return false; }

        if (chance < 100 && Random.Shared.Next(100) >= chance)
        { SendMiniText($"{mob.Name} resists the confusion."); return true; }   // fizzle — still cast, no effect

        _world.ConfuseMob(_char.Map, mob);

        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"You cast {sp.Name} on {mob.Name}");
        Log.Info($"      (lua) {sp.Name} -> mob {mob.Id} '{mob.Name}' confused (aggro reset, mobTarget={mob.TargetMobId})");
        return true;
    }

    // Public speech from the caster (RTK player:talk(2, "…")) — Kamikaze shouts before it detonates. Same 0x0D
    // over-head bubble a typed chat line uses, broadcast INCLUDING us so it reads as the caster actually
    // speaking rather than as a private status line. RTK's :talk is proximity-gated (bll_talk ->
    // map_foreachinarea(..., AREA, ...)), so despite the "! " prefix this carries only to nearby players.
    internal void LuaTalk(string msg)
    {
        string formatted = $"{_char.Name}! {msg}";
        if (formatted.Length > 250) formatted = formatted[..250];
        var bytes = AsciiBytes(formatted);
        _world.BroadcastArea(_char.Map, _char.X, _char.Y, SayHalfW, SayHalfH,
            p => p.SpeakEntity(2, _char.Id, bytes));   // RTK talk's own chatType 2
    }

    // The per-spell cast shout (Berserk's "K'YA~!", Whirlwind's "Sa-AAA~~!", …). Same blue chatType-2 over-head
    // bubble as LuaTalk, but WITHOUT the "{name}! " prefix — the live game shows just the bare word over the
    // head. Proximity-gated around the caster like every other RTK :talk. See Content.OverheadShoutFor.
    internal void Shout(string msg)
    {
        if (msg.Length > 250) msg = msg[..250];
        var bytes = AsciiBytes(msg);
        _world.BroadcastArea(_char.Map, _char.X, _char.Y, SayHalfW, SayHalfH,
            p => p.SpeakEntity(2, _char.Id, bytes));
    }

    // Apply one timed stat buff (might/hit/dam/hp/mp/…) for durationMs, folded live into Totals() -> HUD/melee.
    // Re-casting the SAME spell refreshes rather than stacks (matches C# CastBuff / RTK removeDuras-then-set).
    // Buffs flow through BuffTotals() (never cached), so no equip-cache invalidation is needed. Shares the exact
    // ActiveBuff plumbing the C# archetype uses.
    internal void LuaBuff(string stat, int amount, int durationMs, SpellDef sp)
    {
        if (string.IsNullOrEmpty(stat) || amount == 0 || durationMs <= 0) return;
        BuffRemoveAll(b => b.Key == sp.Key);   // refresh, don't stack
        BuffAdd(new ActiveBuff { Stat = stat, Amount = amount, Expires = Environment.TickCount64 + durationMs, Key = sp.Key, Name = sp.Name });
        SendStats();
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
    }

    // ---- primitives for the Buff / TargetBuff / Debuff / Cure archetype verbs (Tier-1 Lua migration) --------
    // Each is a thin wrapper over the SAME plumbing the C# CastBuff/CastTargetBuff/CastDebuff/CastCure use, so the
    // Lua route can't diverge. The verb (spell_verbs.lua arch_buff/arch_targetbuff/arch_debuff/arch_cure) owns the
    // LOGIC — mana ordering, the multi-stat loop, self/player/mob routing, the deflect+chance sequence — while
    // these do the mechanical apply. See CastBuff etc. for the faithful reference each mirrors.

    // Mana check WITHOUT debit (message matches CastBuff/CastDamage) — the verb debits later via LuaDebitMana, so
    // a no-target/no-effect abort spends nothing (RTK-correct: mana is only spent once the cast commits).
    internal bool LuaEnoughMana(int amt)
    {
        if (amt < 0) amt = 0;
        if (_char.Mp < (uint)amt) { SendMiniText("You do not have enough mana."); return false; }
        return true;
    }
    internal void LuaDebitMana(int amt)
    {
        if (amt < 0) amt = 0;
        _char.Mp = _char.Mp > (uint)amt ? _char.Mp - (uint)amt : 0;
        SendStats();
    }

    // ---- BUFF primitives (the single `apply_buff` body behind arch_buff AND arch_targetbuff) ------------------
    // Deliberately shaped like the WARD set below (LuaWardTarget/LuaWardHasStatus/LuaWardAlreadyCast/
    // LuaApplyWard), which has always covered its own self-cast and ally-cast halves through ONE resolved-target
    // primitive set. The two buff archetypes differ only in WHO the buff lands on — the caster, or whatever the
    // cast is aimed at — and keeping that as two parallel code paths is exactly how the exclusivity slot came to
    // be enforced on one and not the other (RTK has the same split: rogue/might.lua refuses, mage/might.lua
    // refreshes). Resolved once per cast, then every step reads the resolution.
    private Session? _buffPc;    // the buff's resolved PC target (the caster for a self-cast, else a peer)
    private Mob?     _buffMob;   // …or a mob (a stat buff on your pet); mobs carry no exclusivity categories

    /// <summary>Resolve + validate the buff target. <c>"self"</c> → the caster (the Buff archetype, which has no
    /// target arg on the wire at all). <c>"target"</c> → an explicit id or the faced tile: a PC (incl. yourself)
    /// or a mob. False when nothing resolves, and SILENTLY — a cast that finds nothing says nothing, which is
    /// the rule the TargetBuff path has always followed.</summary>
    internal bool LuaBuffTarget(string mode, uint? targetId)
    {
        _buffPc = null; _buffMob = null;
        if (mode == "self") { _buffPc = this; return true; }
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (pc is null && mob is null) return false;
        _buffPc = pc; _buffMob = mob;
        return true;
    }

    // The RTK checkIfCast guard, over the resolved target. Buffs were the one status family that skipped it:
    // arch_buff just cleared its own key and re-applied, so Might could be spammed indefinitely and — worse —
    // Might + Spirit Strength (RTK's SAME `mights` slot, different keys) stacked. Player-only, matching the
    // curse/ward side: a mob carries no categories, so a buff on a pet is never refused.
    internal bool LuaBuffHasStatus(string category) => _buffPc?.HasStatusCategory(category) ?? false;
    internal bool LuaBuffAlreadyCast(SpellDef sp)   => _buffPc?.HasStatusFromSpell(sp.Key) ?? false;

    /// <summary>Apply the buff to whatever <see cref="LuaBuffTarget"/> resolved, then play the fx and the
    /// target's flavor line once. <paramref name="stats"/>/<paramref name="amounts"/> are the export row's raw
    /// <c>'|'</c>-separated fields ("might" / "might|hit"), split here rather than in Lua so one call covers a
    /// multi-stat buff without the verb having to sequence clear-then-add-then-fx by hand.
    ///
    /// <para><paramref name="category"/> is the RTK exclusivity slot, and passing it is what makes
    /// <see cref="HasStatusCategory"/> see the buff at all — an uncategorised entry is invisible to every guard.
    /// A categorised buff lands even with no stat of its own, because then the slot IS the effect (the same rule
    /// <see cref="ReceiveCurse"/> follows for a protection).</para></summary>
    internal void LuaApplyBuff(string stats, string amounts, int durMs, SpellDef sp, string category)
    {
        if (durMs <= 0) return;
        var statList = SplitBar(stats);
        var amtList  = SplitBar(amounts);
        category ??= "";

        if (_buffPc is not null)
        {
            _buffPc.ReceiveTimedBuff(statList, amtList, durMs, sp.Key, sp.Name, category);
        }
        else if (_buffMob is not null)
        {
            // Mobs carry no exclusivity category (matching the curse/ward side) — just the stat deltas.
            for (int i = 0; i < statList.Count; i++)
            {
                int amt = i < amtList.Count && double.TryParse(amtList[i], out var d) ? (int)Math.Floor(d) : 0;
                if (statList[i].Length == 0 || amt == 0) continue;
                _world.ApplyMobBuff(_buffMob, statList[i], amt, durMs, sp.Key);   // under World._lock (races Tick revert)
            }
        }

        var fx = Content.FxFor(sp);
        uint fxId = _buffPc?._char.Id ?? _buffMob!.Id;
        if (fx is not null) BroadcastFx(fxId, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        if (_buffPc is not null) TellTarget(_buffPc, sp);   // self: flavor, then HandleCast's "You cast X."; ally: "<caster> casts X on you."
        Log.Info($"      (lua) {sp.Name} -> buff [{(category.Length > 0 ? category : "-")}] {stats}={amounts} {durMs}ms on " +
                 (_buffPc is not null ? $"player {_buffPc._char.Id} '{_buffPc._char.Name}'" : $"mob {_buffMob!.Id} '{_buffMob.Name}'"));
        SendStats();
    }

    /// <summary>Apply a damage-reduction multiplier (Sanctuary &amp;c) to the resolved target — its own scalar
    /// slot, not a stat delta, and PLAYERS ONLY. False (having done nothing, spent nothing) if the cast resolved
    /// to a mob, so the verb can say "<c>&lt;spell&gt; has no effect on that.</c>" and abort.</summary>
    internal bool LuaApplyDeduction(double mult, int durMs, SpellDef sp)
    {
        if (_buffPc is null) return false;
        _buffPc.ApplySanctuaryDeduction(mult, durMs, sp.Name);
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_buffPc._char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        TellTarget(_buffPc, sp);
        SendStats();
        Log.Info($"      (lua) {sp.Name} -> deduction x{mult} on player {_buffPc._char.Id} '{_buffPc._char.Name}' {durMs}ms");
        return true;
    }

    /// <summary>Split an export row's <c>'|'</c>-separated field ("might|hit" → [might, hit]; "" → []).</summary>
    private static List<string> SplitBar(string? s) =>
        string.IsNullOrEmpty(s) ? new List<string>()
                                : s.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

    internal void LuaFxSelf(SpellDef sp)
    {
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
    }
    // TargetBuff resolution + apply (mirror CastTargetBuff): resolve the explicit target (id -> player else mob;
    // no id -> faced tile: peer else mob), classify for the verb, and apply the buff/deduction the verb chose.
    /// <param name="selfIfUnaimedInPvp">Land on the CASTER when nothing else resolves, <b>on a PvP map only</b> —
    /// the rule that makes a hostile self-cast possible at all.
    /// <para>4.95 gives a spell no way to say "me": the client casts with the slot alone, so every targeted
    /// spell resolves off the faced tile, and you are never on your own faced tile. Every self-cast in the
    /// game is therefore this fallback. <see cref="LuaHealTarget"/> has always had it ("aimed at nothing —
    /// heal yourself"), which is why healing yourself works; the hostile-status paths did not, which is why
    /// self-Doze and the documented self-pestilence defence resolved to nothing and did nothing.</para>
    /// <para>Enabled for hostile STATUS spells (curse · hold · venom) and NOT for damage. A curse or a doze
    /// aimed at nothing landing on you is the intended play — occupying your own exclusivity slot with a mild
    /// curse so a worse one bounces off is a real PvP tactic. A NUKE aimed at nothing landing on you is just a
    /// way to kill yourself when a mob dies mid-cast, so the damage paths keep refusing (silently).</para>
    /// <para><b>Why the PvP gate is on the FALLBACK and not on the self-cast itself.</b> The tactic it exists
    /// for — parking a mild curse in your own exclusivity slot — is only worth anything where someone can
    /// curse you, i.e. exactly where <see cref="Content.IsPvpMap"/> is true. Off one, this fallback had no
    /// upside left and one sharp edge: a type-2 cast whose target died (or that the client sent with no id at
    /// all, log `0f 14 00`) silently landed a 7-minute −30 armor Vex on the caster, in a town. So the fallback
    /// now stops at the map boundary, matching <see cref="ResolveDamageTarget"/> beat for beat: aimed at
    /// nothing, off a PvP map, a hostile cast resolves to nobody and fails quietly. Inside an arena every
    /// self-status play still works exactly as before.</para></param>
    private void ResolveTargetBuff(uint? targetId, out Session? pc, out Mob? mob, bool selfIfUnaimedInPvp = false)
    {
        pc = null; mob = null;
        if (targetId is uint tid && tid != 0)
        {
            pc = _world.Online.ById(tid);
            if (pc is null) mob = _world.MobById(_char.Map, tid);
        }
        else
        {
            var (fxT, fyT) = FrontTile();
            pc = _world.PeerAt(_char.Map, fxT, fyT);
            if (pc is null) mob = _world.MobAt(_char.Map, fxT, fyT);
        }
        if (selfIfUnaimedInPvp && pc is null && mob is null && Content.IsPvpMap(_char.Map)) pc = this;
    }
    internal string LuaTargetBuffKind(uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob);
        return pc is not null ? "player" : mob is not null ? "mob" : "none";
    }
    // (The buff apply + deduction live up with the rest of the BUFF primitives — LuaApplyBuff /
    // LuaApplyDeduction — since both archetypes now share one resolved target.)

    // Debuff pieces (mirror CastDebuff): deflect roll, chance-to-hold, freeze.
    internal bool LuaDeflected(SpellDef sp, uint? targetId)
    {
        var mob = ResolveCastTarget(targetId);
        return mob is not null && sp.CanFail && RollDeflect(mob);
    }
    internal bool LuaRoll(double pct) => Random.Shared.Next(100) < pct;
    /// <summary>Uniform integer in [lo, hi] INCLUSIVE. Same Random.Shared stream as LuaRoll so all spell
    /// randomness stays on one sequence — don't reach for Lua's math.random, which is a separate generator.</summary>
    internal int LuaRollRange(int lo, int hi) => hi <= lo ? lo : Random.Shared.Next(lo, hi + 1);
    internal double LuaDebuffChance(SpellDef sp, uint? targetId)
    {
        var fx = Content.FxFor(sp);
        if (fx is null || string.IsNullOrEmpty(fx.Chance)) return 100;
        var mob = ResolveCastTarget(targetId);
        return Formula.Eval(fx.Chance, SpellVars(mob));
    }
    /// <summary>Which kind of hold this Debuff spell is, straight off its export row's <c>debuff</c> column:
    /// "blind" · "paralyze" · "sleep" · "slow". The archetype verb branches on it — before this, every Debuff
    /// spell in the game ran the same generic freeze, so Blind froze instead of blinding and Doze was
    /// indistinguishable from Paralyze.</summary>
    internal string LuaDebuffKind(SpellDef sp) => Content.FxFor(sp)?.Debuff ?? "";

    /// <summary>Apply one of the hostile categorised holds to the faced/targeted MOB (RTK's
    /// paralyze/doze/sleep/blind family). <paramref name="category"/> is the exclusivity slot ("paras" ·
    /// "sleeps" · "blinds" · "slows"); <paramref name="hold"/> freezes it in place, <paramref name="blind"/>
    /// takes its sight. <paramref name="repeatFxMs"/> &gt; 0 keeps re-drawing the spell's own animation on
    /// that cadence for as long as the status runs (RTK <c>while_cast</c> — doze/sleep do this, paralyze
    /// doesn't).
    /// <para>Mostly mob-only: RTK's paralyze, static, blind and Sleep all reject <c>BL_PC</c>. <b>Doze is the
    /// exception</b> (<see cref="Content.HoldHitsPlayers"/>) and lands on another player in a PvP map — see
    /// <see cref="ReceiveSleep"/> for what a hold can and can't mean against a 4.95 client.</para>
    /// False (with the RTK notice, no mana spent) if there's no legal target, or the slot is already occupied —
    /// which is what stops the same hold being chain-cast to keep something locked down forever.</summary>
    internal bool LuaHoldTarget(string category, int durMs, bool hold, bool blind, int repeatFxMs,
                                SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);

        // PLAYER target — legal for the Doze family only.
        if (pc is not null)
        {
            bool onSelf = ReferenceEquals(pc, this);
            if (!Content.HoldHitsPlayers(sp))
            { SendMiniText("It doesn't work."); Log.Info($"      -x {sp.Name}: holds don't work on players (only the Doze family does)"); return false; }
            // PvP is required to hold ANY player (RTK's `canPK`), yourself included — the same boundary the
            // curse and damage paths draw. A self-doze is harmless in isolation, but off a PvP map it has no
            // opponent to be a tactic against, so every one that landed there was an unaimed cast falling
            // through onto its caster rather than something anybody meant to do.
            if (!Content.IsPvpMap(_char.Map))
            { SendMiniText("You can't attack that target."); Log.Info($"      -x {sp.Name}: not a PvP map ({_char.Map}) — can't hold a player here"); return false; }
            if (pc.HasStatusCategory(category))
            { SendMiniText(BlockedStatusMsg(pc.HasStatusFromSpell(sp.Key))); Log.Info($"      -x {sp.Name}: '{pc._char.Name}' already carries a [{category}]"); return false; }
            var pfx  = Content.FxFor(sp);
            int pAnim = pfx is not null ? Content.EffectAnim(pfx, sp.PathId) : 0;
            int pSnd  = pfx is not null ? Content.EffectSound(pfx, sp.PathId) : 0;
            pc.ReceiveSleep(category, durMs, sp.Key, sp.Name, pAnim, repeatFxMs);
            if (pfx is not null) BroadcastFx(pc._char.Id, pAnim, pSnd);
            if (!onSelf) TellTarget(pc, sp);   // on yourself, ReceiveSleep's own "You fall asleep." is the line
            Log.Info($"      (lua) {sp.Name} -> player {pc._char.Id} '{pc._char.Name}'{(onSelf ? " (SELF)" : "")} " +
                     $"[{category}] {durMs}ms anim={pAnim} repeat={repeatFxMs}ms");
            return true;
        }

        // "It doesn't work." for an NPC too — it has no AI to take away in the first place.
        if (mob is null || mob.IsNpc)
        {
            if (mob is null) LogNoTarget(sp); else SendMiniText("It doesn't work.");
            Log.Info($"      -x {sp.Name}: {(mob is null ? "no target resolved (no id sent and nothing on the faced tile)" : "target is an NPC")}");
            return false;
        }
        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;
        // RTK shortens a hold on a boss to a token 2s (doze/sleep both do `if target.isBoss ~= 0 then
        // duration = 2000`) — you can interrupt one, you can't lock it down.
        if (mob.IsBoss && durMs > BossHoldMs) durMs = BossHoldMs;
        if (!_world.ApplyMobStatus(mob, category, durMs, hold, blind, anim, snd, repeatFxMs, sp.Key))
        { SendMiniText(BlockedStatusMsg(_world.MobStatusKey(mob, category) == sp.Key)); Log.Info($"      -x {sp.Name}: mob {mob.Id} already carries a [{category}]"); return false; }
        if (fx is not null) BroadcastFx(mob.Id, anim, snd);
        Log.Info($"      (lua) {sp.Name} -> mob {mob.Id} '{mob.Name}' [{category}] {durMs}ms hold={hold} blind={blind} anim={anim} repeat={repeatFxMs}ms");
        return true;
    }

    /// <summary>RTK's boss cap on a hold, shared by doze and sleep (<c>duration = 2000</c>).</summary>
    private const int BossHoldMs = 2000;

    // ---- CURSE / categorized-status primitives (the `curse` verb + arch_cure category removal) ---------------
    // A curse is a mutually-exclusive categorized status (RTK spellTables.lua): applying one is blocked if the
    // target already carries a status of that category (checkIfCast) — which is exactly why self-pestilence in a
    // PvP map is a real defense (occupy your own 'curses' slot with a harmless curse). Cures remove by category.
    // See nexustk-495-curse-status-system. Curse statuses ride the same _buffs list (with Category set) so they
    // fold into Totals()/AC, expire+fade via ExpireBuffs, and revert automatically — no separate bookkeeping.

    // The ONE category containment in RTK spellTables.lua: minorcurses ⊂ curses. It has two asymmetric views:
    //  - EXCLUSIVITY (checkIfCast): SYMMETRIC — a minor curse (vex) and a full curse (pestilence) block each
    //    other, because every curse spell's cast() guards on the BROAD `curses` table. CatFamily collapses
    //    minorcurses→curses so both sides compare equal.
    //  - CURE (removeDuras): ONE-WAY — atone (cureCat "curses") clears minor curses too, but remove_curse
    //    (cureCat "minorcurses") does NOT clear a full curse. CureMatches encodes that direction only.
    // Every other category (venoms/paras/blinds/disheartens/…) is disjoint, so both helpers reduce to equality.
    private static string CatFamily(string cat) => cat == "minorcurses" ? "curses" : cat;
    private static bool CureMatches(string statusCat, string cureCat) =>
        statusCat == cureCat || (cureCat == "curses" && statusCat == "minorcurses");

    // Is a status of this category (or its broader family) active on THIS player? Used for the checkIfCast
    // exclusivity guard, so it uses the SYMMETRIC family collapse (a minor curse blocks a full curse and vice
    // versa). (Reused across sessions to check any curse target.)
    internal bool HasStatusCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return false;
        long now = Environment.TickCount64;
        string fam = CatFamily(category);
        return _buffs.Any(b => b.Category.Length > 0 && CatFamily(b.Category) == fam && b.Expires > now);
    }

    // ---- "You already cast that spell." vs "Another spell of this type is in effect." -------------------------
    // Both are RTK lines, and RTK picks between them by WHAT IS IN THE SLOT: paralyze.lua answers "You already
    // cast that spell." off its own `target.paralyzed`, static.lua answers "A more powerful spell is in effect."
    // off the same flag. It could only ever manage that by hand, per script, because a mob carried one boolean
    // per mechanic and not the identity of what set it. Now that a slot remembers its spell key on both sides
    // (Mob.MobStatus.Key / ActiveBuff.Key), the distinction is general: re-casting YOUR OWN running spell reads
    // as "you already cast that", anything else in the slot reads as "another spell".

    /// <summary>The refusal line for a blocked categorised status. <paramref name="sameSpell"/> = the slot is
    /// held by the very spell being cast again.</summary>
    internal static string BlockedStatusMsg(bool sameSpell) =>
        sameSpell ? "You already cast that spell." : "Another spell of this type is in effect.";

    /// <summary>Is a still-running categorised status on THIS player the work of <paramref name="key"/>? Only
    /// categorised entries count: an ordinary buff (Might &amp;c) refreshes rather than blocks, so it never
    /// produces a refusal that would need this wording.</summary>
    internal bool HasStatusFromSpell(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        long now = Environment.TickCount64;
        return _buffs.Any(b => b.Category.Length > 0 && b.Key == key && b.Expires > now);
    }

    /// <summary>Was the slot that just blocked this cast filled by this same spell? <paramref name="category"/>
    /// is the category that actually did the blocking (which for a curse may be a broader one than the spell's
    /// own — a protection bouncing a curse, say — in which case this is simply false).</summary>
    internal bool LuaAlreadyCastOnTarget(string category, SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        if (pc is not null) return pc.HasStatusFromSpell(sp.Key);
        if (mob is not null) return _world.MobStatusKey(mob, CatFamily(category)) == sp.Key;
        return false;
    }

    // Validate a curse target the way RTK pestilence.lua does: a PC (incl. YOURSELF) is only a legal curse target
    // in a PvP map (approximates RTK canPK); a mob is always fair game; nothing faced -> a silent refusal. NPCs
    // aren't distinguished from mobs on 4.95 (stationary mobs), so curse-on-NPC isn't specifically blocked yet.
    internal bool LuaCanCurseTarget(SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        if (pc is null && mob is null) { LogNoTarget(sp); return false; }
        // The PvP-map gate covers YOURSELF too — same rule, and for the same reason, as HitPlayerWithSpell's.
        // Self-pestilence IS a real defence (occupy your own exclusivity slot with a mild curse so a worse one
        // can't land), but it is only ever worth anything where someone can curse you, so keeping it PvP-only
        // costs the tactic nothing. Off a PvP map it bought no defence and only ever fired by accident — the
        // client sending a type-2 cast with a stale or absent target id, landing a 7-minute self-Vex in town.
        if (pc is not null && !Content.IsPvpMap(_char.Map))
        { SendMiniText("You can't attack that target."); return false; }
        return true;
    }

    // Does the resolved curse target already carry a status of this category? Checks BOTH sides: a player via
    // the categorised _buffs list, a mob via Mob.Statuses. The mob half used to be missing entirely, and that
    // was the whole of the "a weak curse doesn't block a strong one" bug — the exclusivity that makes Vex
    // (minorcurses) bar Scourge (curses) only ever ran against players, so on a monster every curse simply
    // overwrote the last one and the checkIfCast rule may as well not have existed.
    internal bool LuaCurseHasCategory(string category, uint? targetId)
    {
        if (string.IsNullOrEmpty(category)) return false;
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        if (pc is not null) return pc.HasStatusCategory(category);
        // CatFamily on BOTH sides, same as HasStatusCategory: minorcurses collapses into curses so the two
        // block each other symmetrically (RTK's curse scripts all guard on the broad `curses` table).
        if (mob is null) return false;
        string fam = CatFamily(category);
        return _world.MobHasStatus(mob, fam) || (fam == "curses" && _world.MobHasStatus(mob, "minorcurses"));
    }

    // Apply a categorized status to the resolved curse target (PC via _buffs+Category; mob via the timed-buff
    // path). stat/amount is the mechanical effect (e.g. armor -5 -> raises effective AC -> victim takes MORE
    // damage, our inverted-AC equivalent of RTK's cursing "armor += 5"); amount may be 0 for a pure blocker.
    internal void LuaApplyCurse(string category, string stat, int amount, int durMs, SpellDef sp, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        var fx = Content.FxFor(sp);
        int anim = fx is not null ? Content.EffectAnim(fx, sp.PathId) : 0;
        int snd  = fx is not null ? Content.EffectSound(fx, sp.PathId) : 0;
        if (pc is not null)
        {
            pc.ReceiveCurse(stat, amount, durMs, sp.Key, sp.Name, category);
            if (fx is not null) BroadcastFx(pc._char.Id, anim, snd);
            TellTarget(pc, sp);   // other PC: "<caster> casts <X> on you."; self: any flavor line (else nothing)
            Log.Info($"      (lua) {sp.Name} -> curse [{category}] {stat}{amount:+0;-0} on player {pc._char.Id} '{pc._char.Name}' {durMs}ms");
        }
        else if (mob is not null)
        {
            // Occupy the category slot as well as applying the stat, so the next curse's checkIfCast can see
            // it. Neither a hold nor a blind — a curse only weakens, it doesn't stop the creature.
            _world.ApplyMobStatus(mob, CatFamily(category), durMs, hold: false, blind: false, spellKey: sp.Key);
            if (!string.IsNullOrEmpty(stat) && amount != 0) _world.ApplyMobBuff(mob, stat, amount, durMs, sp.Key);
            if (fx is not null) BroadcastFx(mob.Id, anim, snd);
            Log.Info($"      (lua) {sp.Name} -> curse [{category}] {stat}{amount:+0;-0} on mob {mob.Id} '{mob.Name}' {durMs}ms");
        }
        SendStats();
    }

    /// <summary>Put THIS player to sleep (the Doze family's PvP branch). Cross-session — called on the
    /// TARGET's own Session.
    /// <para><b>What a hold can and cannot be on a 4.95 client.</b> It cannot stop you walking: self-walk is
    /// CLIENT-LOCAL (the client moves itself and the server only paces it), so a movement freeze would just
    /// desync — you'd keep walking on your own screen. What the server DOES own is every action you take
    /// through it, so sleep takes your <b>attacks and your casts</b> (see the guards in HandleAttack /
    /// HandleCast). You can still run; you can't fight back. Against Doze's 10 seconds that makes it an
    /// opener, not a lockdown.</para>
    /// <para>It ends the moment you take damage (RTK <c>on_takedamage_while_cast</c>), so the caster gets one
    /// free move out of it rather than a chain of them — the same rule the mob side follows.</para></summary>
    internal void ReceiveSleep(string category, int durMs, string key, string name, int anim, int repeatFxMs)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (durMs <= 0) { Log.Info($"      -x {name}: zero duration, nothing applied"); return; }
        ReceiveCurse("", 0, durMs, key, name, category);   // no stat effect — the slot IS the effect
        _sleepUntil = Environment.TickCount64 + durMs;
        // The drowse keeps redrawing over your head for as long as it holds (RTK doze's `while_cast`), which
        // is also what tells everyone watching that you're out of the fight.
        if (anim > 0 && repeatFxMs > 0)
        {
            _sleepFxAnim = anim; _sleepFxEvery = repeatFxMs;
            _sleepFxNext = Environment.TickCount64 + repeatFxMs;
        }
        SendMiniText("You fall asleep.");
    }

    private long _sleepUntil, _sleepFxNext;
    private int  _sleepFxAnim, _sleepFxEvery;

    // ---- player-side venom (the PC half of the poison DoT) ---------------------------------------------------
    // NexusAtlas, Venom: "Poisons monster targets for a random amount of time. Does 1000 damage a second,
    // DISREGARDING ARMOR CLASS, on other players. Does more damage per second to animals. Poison will not kill
    // a target but rather bring them to the lowest possible health."
    //
    // So this is not a mob-only spell, which is what we had built. RTK's venom.lua is self-contradictory on the
    // point — its `cast` refuses `BL_PC` outright ("It doesn't work.") while its `while_cast_1500` carries a
    // fully-written PC branch, gated on canPK, that the cast can never reach. The atlas describes the PC
    // behaviour in detail, and the archive outranks the Lua for what the real game did.
    private long _poisonUntil, _poisonNextTick;
    private int  _poisonPerTick, _poisonFxAnim;
    private uint _poisonBy;
    // Whole-second bounds for a RAGGED tick cadence, when the venom that applied this one asked for it
    // (game-data/MobSpells.csv TickMinMs/TickMaxMs). 0 = the fixed World.PoisonTickMs beat.
    private int  _poisonTickMin, _poisonTickMax;

    /// <summary>The line the status box gets on every tick that actually takes health. Venom is not a hit —
    /// there is no attacker to name and no swing to describe — so this is the only thing that tells a player
    /// what is happening to them between one tick and the next. Shared by every source of poison: a rogue's
    /// venom reads the same to its victim as a cavern rat's does.</summary>
    private const string PoisonTickText = "Poison is spreading through your veins.";

    /// <summary>Milliseconds until this venom's next tick. Fixed at <see cref="World.PoisonTickMs"/> unless
    /// the applying row set a min/max, in which case the gap is a WHOLE NUMBER OF SECONDS drawn uniformly
    /// from that band — the Buya Library Caverns venom is observed ticking on 1s, 2s, 3s and 4s gaps, never
    /// on a fraction of one. Drawn fresh every tick, so the rhythm never settles into a pattern.</summary>
    private int NextPoisonGap()
    {
        if (_poisonTickMin <= 0 || _poisonTickMax < _poisonTickMin) return World.PoisonTickMs;
        int steps = (_poisonTickMax - _poisonTickMin) / 1000;
        return _poisonTickMin + 1000 * Random.Shared.Next(steps + 1);
    }

    /// <summary>Is this player currently venomed?</summary>
    internal bool Poisoned => _poisonUntil > Environment.TickCount64;

    /// <summary>Apply the venom DoT to THIS player. Cross-session — called on the VICTIM's own Session.
    /// <para><b>Two readings of the atlas's "1000 damage a second", and it is a real coin-flip:</b>
    /// <list type="number">
    /// <item><b>a RATE</b> — 1000/s against the 1.5s cadence = <b>1500 per tick</b>. Reads the sentence
    ///   literally. This is <paramref name="dps"/>.</item>
    /// <item><b>a per-TICK amount</b> — 1000 per tick = 667/s, with "a second" being loose wording for the
    ///   ~1.5s tick. This is what RTK's code shape suggests: its <c>while_cast_1500</c> deals
    ///   <c>MaxHp*1%</c> capped at <c>_maxDamage = 2000</c> per TICK, and a typical target's 1% lands right
    ///   about 1000 — so the description may simply be someone eyeballing the per-tick number. This is
    ///   <paramref name="perTick"/>, which WINS when set.</item>
    /// </list>
    /// Both satisfy the atlas's "more damage per second to animals" comparison, so neither is ruled out.
    /// The practical difference is only where the never-kill floor bites: ~160k max HP under (1), ~107k
    /// under (2). Set <c>pcPerTick</c> in SpellParams.csv to switch readings without doing the conversion
    /// in your head.</para>
    /// Also occupies the <c>venoms</c> category, which is what blocks a second venom and lets the eight
    /// <c>cureCat = venoms</c> cures clear it.</summary>
    internal void ReceivePoison(int dps, int durMs, uint by, int anim, string key, string name, int perTick = 0,
                                int tickMinMs = 0, int tickMaxMs = 0)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (durMs <= 0 || IsDead) return;
        _poisonPerTick = perTick > 0
            ? perTick
            : Math.Max(1, (int)Math.Round(dps * (World.PoisonTickMs / 1000.0)));
        _poisonTickMin = tickMinMs;
        _poisonTickMax = tickMaxMs;
        _poisonUntil   = Environment.TickCount64 + durMs;
        _poisonNextTick = Environment.TickCount64 + NextPoisonGap();
        _poisonFxAnim  = anim;
        _poisonBy      = by;
        ReceiveCurse("", 0, durMs, key, name, "venoms");   // the exclusivity slot + the profile duration line
        SendMiniText("Poison courses through you.");
    }

    /// <summary>Clear the venom (a cure of category <c>venoms</c>, or the timer lapsing).</summary>
    internal void CurePoison()
    {
        if (_poisonUntil == 0) return;
        bool was = Poisoned;
        _poisonUntil = 0; _poisonFxAnim = 0;
        _poisonTickMin = 0; _poisonTickMax = 0;
        BuffRemoveAll(b => b.Category == "venoms");
        if (was) { SendMiniText("The poison passes."); SendStats(); }
    }

    /// <summary>Driven by the world heartbeat. Deals the tick, redraws the venom animation, and lapses the
    /// status. <b>It can never kill</b> — the atlas is explicit that poison brings you to the lowest possible
    /// health and stops, so the damage is clamped to leave 1 HP and <see cref="Die"/> is never reached.
    /// Armour is deliberately not consulted ("disregarding armor class"), and neither is the deduction
    /// multiplier — this is not a hit, it is the poison already inside you.
    /// <para><b>Why the field is read before the monitor.</b> The world heartbeat calls this on EVERY online
    /// session every beat, and almost none of them are venomed — so taking the monitor first meant the tick
    /// thread entered 400 session monitors a beat to find nothing to do, and waited behind whichever of them
    /// a session thread was holding mid-send. <c>_poisonUntil</c> is a <c>long</c>, written only under
    /// <see cref="EnterState"/> (by <see cref="ReceivePoison"/> and <see cref="CurePoison"/>), and 64-bit
    /// reads are atomic on every runtime we target; <see cref="Poisoned"/> has always read it unguarded from
    /// the attack and cast gates. <c>Volatile.Read</c> keeps the JIT from caching or
    /// reordering it. The one race is benign and deliberate: a venom applied between this read and the
    /// return makes the tick skip that player for ONE beat (333 ms), never lose them, because
    /// <c>_poisonNextTick</c> is already a whole gap away and the next beat reads the new value. The
    /// decision this method acts on is still the guarded one — the test below the monitor is unchanged.</para></summary>
    internal void TickPoison()
    {
        if (Volatile.Read(ref _poisonUntil) == 0) return;   // the common case: no monitor entered at all
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (_poisonUntil == 0) return;
        if (!Poisoned) { CurePoison(); return; }
        if (IsDead) { CurePoison(); return; }
        if (Environment.TickCount64 < _poisonNextTick) return;
        _poisonNextTick = Environment.TickCount64 + NextPoisonGap();

        int dam = Math.Min(_poisonPerTick, Math.Max(0, (int)_char.Hp - 1));   // never the killing blow
        if (_poisonFxAnim > 0)
        {
            int a = _poisonFxAnim;
            _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.EffectOver(_char.Id, a));
        }
        if (dam <= 0) return;                       // already at 1 HP: keep flashing, stop hurting
        _char.Hp -= (uint)dam;
        WakeUp(byDamage: true);                     // poison counts as damage for the sleep-breaks-on-hit rule
        SendStats();
        SendMiniText(PoisonTickText);               // only on a tick that TOOK health — see the const
        byte pct = PlayerHpPercent();
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, pct, HitCritByte));
        if (_poisonBy != 0 && _poisonBy != _char.Id) MarkPvpFoe(_poisonBy);
    }

    /// <summary>"@doze [secs]" — put YOURSELF to sleep, to audition the hold without a second character.
    /// <para>This exists because <b>Doze cannot be self-targeted over the wire.</b> The 4.95 client casts with
    /// the slot alone — live-confirmed, `0f | slot+1 00`, no target id even for a "Which target? &gt;" spell
    /// (protocol doc §14) — so every targeted spell resolves off the FACED TILE, and you are never on your own
    /// faced tile. Casting Doze at yourself therefore resolves to whatever you happen to be looking at, and
    /// to nothing at all in an empty room. To hold another player, face them in a PvP map and cast normally;
    /// that path works. This command is the only way to be your own target.</para></summary>
    private void DozeSelfCmd(string text)
    {
        string arg = text.Trim();
        // "@doze off" / "@doze 0" wakes you now. Needed, not a nicety: the only other way out is taking a
        // hit, so dozing yourself in an empty room would otherwise mean sitting out the whole timer. Chat
        // isn't gated by the hold, so the command still reaches the server while you're under it.
        if (arg is "off" or "0" or "wake") { WakeUp(byDamage: false); SendLog("Awake."); return; }
        int secs = int.TryParse(arg, out var n) && n > 0 ? Math.Min(n, 300) : 10;
        // Borrow the real spell's row so the audition uses the same animation and cadence the cast would.
        var sp = Content.FindSpell("doze_mage");
        var fx = sp is not null ? Content.FxFor(sp) : null;
        int anim = sp is not null && fx is not null ? Content.EffectAnim(fx, sp.PathId) : 2;
        ReceiveSleep("sleeps", secs * 1000, "doze_mage", sp?.Name ?? "Doze", anim, 1000);
        BroadcastFx(_char.Id, anim, sp is not null && fx is not null ? Content.EffectSound(fx, sp.PathId) : 0);
        SendLog($"Asleep for {secs}s: no walking, turning, attacking or casting; anim {anim} replays every 1s; " +
                "taking damage wakes you. Open your profile to see the duration.");
    }

    /// <summary>Is this player currently held asleep? Read by the attack/cast gates.</summary>
    internal bool Asleep => _sleepUntil > Environment.TickCount64;

    // Damage amplifier left by a sleep-family hold (see Mob.DamageAmp for the full note): the NEXT hit on a
    // dozed/slept player is multiplied, then it's spent. Read + consumed by ApplyMobHit / ReceiveSpellDamage.
    private double _dmgAmp;
    private long   _dmgAmpUntil;
    internal void ArmDamageAmp(double mult, int durMs)
    {
        using var _ = EnterState();   // #29: applied to a PEER by the caster's thread
        _dmgAmp = mult;
        _dmgAmpUntil = Environment.TickCount64 + durMs;
    }
    internal double TakeDamageAmp()
    {
        if (_dmgAmp <= 1.0 || _dmgAmpUntil <= Environment.TickCount64) return 1.0;
        double a = _dmgAmp; _dmgAmp = 0; _dmgAmpUntil = 0;
        return a;
    }

    /// <summary>Arm the sleep-family damage amplifier on whatever this cast just held (player or mob).</summary>
    internal void LuaAmplify(double mult, int durMs, uint? targetId)
    {
        ResolveTargetBuff(targetId, out var pc, out var mob, selfIfUnaimedInPvp: true);
        if (pc is not null) pc.ArmDamageAmp(mult, durMs);
        else if (mob is not null) _world.ArmDamageAmp(mob, mult, durMs);
    }

    /// <summary>Wake up — on damage (RTK's rule), or when the timer lapses. Clears the slot so a Cure isn't
    /// needed and a second Doze can land later.</summary>
    internal void WakeUp(bool byDamage)
    {
        if (_sleepUntil == 0) return;
        bool was = Asleep;
        _sleepUntil = 0; _sleepFxAnim = 0;
        BuffRemoveAll(b => b.Category == "sleeps");
        if (was) { SendMiniText(byDamage ? "The pain wakes you." : "You wake up."); SendStats(); }
    }

    /// <summary>Driven by the world heartbeat: redraw the drowse over a sleeping player, and wake them when
    /// the timer runs out. (The mob side rides <see cref="Mob.SetFxRepeat"/>; a player has no Mob to hang it on.)
    /// <para><b>Why the field is read before the monitor</b> — the same reason and the same rules as
    /// <see cref="TickPoison"/>, which carries the long note. <c>_sleepUntil</c> is written only under
    /// <see cref="EnterState"/> (by <see cref="ReceiveSleep"/> and <see cref="WakeUp"/>) and
    /// <see cref="Asleep"/> already reads it unguarded; a hold applied between this read and the return
    /// slips its first drowse redraw or its lapse by one beat, because <c>_sleepFxNext</c> is a whole
    /// repeat interval away when the applier sets it.</para></summary>
    internal void TickSleep()
    {
        if (Volatile.Read(ref _sleepUntil) == 0) return;   // the common case: no monitor entered at all
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (_sleepUntil == 0) return;
        if (!Asleep) { WakeUp(byDamage: false); return; }
        if (_sleepFxAnim <= 0 || Environment.TickCount64 < _sleepFxNext) return;
        _sleepFxNext = Environment.TickCount64 + _sleepFxEvery;
        int a = _sleepFxAnim;
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.EffectOver(_char.Id, a));
    }

    // Add a categorized status to THIS player (curse target side): refresh-not-stack by key, folds into Totals().
    // Unlike ReceiveTimedBuff this keeps a zero-amount entry (a curse's category slot matters even with no stat
    // effect) and records the Category so checkIfCast / cure-by-category work.
    internal void ReceiveCurse(string stat, int amount, int durMs, string key, string name, string category)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (durMs <= 0) return;
        BuffRemoveAll(b => b.Key == key);   // refresh, don't stack
        BuffAdd(new ActiveBuff { Stat = stat ?? "", Amount = amount, Expires = Environment.TickCount64 + durMs, Key = key, Name = name, Category = category ?? "" });
        SendStats();
    }

    // ---- WARD primitives (the `ward` verb: bolster / harden_armor / hoche protections) --------------------------
    // A ward is the BENEFICIAL twin of a curse — the SAME categorized-status storage (ReceiveCurse) and family
    // exclusivity, but cast on yourself/an ally (never PvP-gated) and applying a positive armor (better AC) or, for
    // protections, no stat at all (a pure category-slot occupier that makes curses bounce). Resolved once per cast.
    private Session? _wardPc;   // the ward's resolved PC target for the current cast (self or ally)
    private Mob?     _wardMob;  // …or a mob (harden on a pet); mobs don't track categories (no exclusivity there)

    // Resolve + validate the ward target. "self" -> the caster (protections, which take no target arg). "ally" ->
    // an explicit id or the faced tile: a PC (self or ally) or a mob (harden on a pet). No PvP gate — wards are
    // beneficial. "It doesn't work." on nothing found (RTK bolster/harden's own no-target line).
    internal bool LuaWardTarget(string mode, uint? targetId)
    {
        _wardPc = null; _wardMob = null;
        if (mode == "self") { _wardPc = this; return true; }
        ResolveTargetBuff(targetId, out var pc, out var mob);
        if (pc is null && mob is null) { SendMiniText("It doesn't work."); return false; }
        _wardPc = pc; _wardMob = mob;
        return true;
    }
    // Category check for the ward's own target (PC only — mobs don't carry categories, matching the curse side).
    internal bool LuaWardHasStatus(string category) => _wardPc?.HasStatusCategory(category) ?? false;
    // …and whether the thing occupying that slot is this very ward, so a re-cast reads "You already cast that
    // spell." instead of the generic "another spell" (which, on your own bolster, is plainly wrong).
    internal bool LuaWardAlreadyCast(SpellDef sp) => _wardPc?.HasStatusFromSpell(sp.Key) ?? false;
    // Apply the ward: a PC gets the categorized status (ReceiveCurse — shared curse/ward storage, folds into
    // Totals()/AC and expires+reverts on its own); a mob gets just the stat buff (no category). Plays fx + flavor.
    internal void LuaApplyWard(string category, string stat, int amount, int durMs, SpellDef sp)
    {
        var fx = Content.FxFor(sp);
        if (_wardPc is not null)
        {
            _wardPc.ReceiveCurse(stat, amount, durMs, sp.Key, sp.Name, category);   // zero-amount ok (a protection slot has no stat)
            if (fx is not null) BroadcastFx(_wardPc._char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            TellTarget(_wardPc, sp);   // self: flavor before "You cast X"; ally: "<caster> casts <X> on you."
            Log.Info($"      (lua) {sp.Name} -> ward [{category}] {stat}{amount:+0;-0} on player {_wardPc._char.Id} '{_wardPc._char.Name}' {durMs}ms");
        }
        else if (_wardMob is not null)
        {
            if (!string.IsNullOrEmpty(stat) && amount != 0) _world.ApplyMobBuff(_wardMob, stat, amount, durMs, sp.Key);
            if (fx is not null) BroadcastFx(_wardMob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            Log.Info($"      (lua) {sp.Name} -> ward [{category}] {stat}{amount:+0;-0} on mob {_wardMob.Id} '{_wardMob.Name}' {durMs}ms");
        }
        SendStats();
    }

    /// <summary>An NPC lays one of its own spells on the player — the scripted counterpart of a player cast
    /// (<see cref="LuaApplyWard"/>), used by quest dialog rather than by the spell system. There is no caster
    /// session, no mana, no aether and no roll: the script has already decided this happens.
    ///
    /// <para>The mechanics come from the spell's own rows, so the NPC's cast and a player's are the same ward
    /// in the same exclusivity slot: SpellParams.csv gives category/stat/amount/duration and spell_effects.csv
    /// the animation and sound. That sharing is the point — a Harden Armor from an NPC must block, and be
    /// blocked by, a Harden Armor from a mage, exactly as two player casts do.</para>
    ///
    /// <para>False, changing nothing, if the key names no spell, if it has no SpellParams row or no duration
    /// (so it is not a ward at all), or if its OWN category slot is already occupied — a re-cast into a full
    /// slot is a no-op here rather than a silent refresh, matching <c>spell_verbs.lua</c>'s <c>verbs.ward</c>.
    /// Note the narrower guard: the verb consults that file's <c>BLOCKS</c> table, so a category that also
    /// bounces off OTHER categories (curses off protections, disheartens off bolsters) would land here where a
    /// player cast is refused. Every ward an NPC casts today is <c>hardarmors</c>, whose BLOCKS list is itself
    /// alone, so the two agree; widen this if a scripted cast ever needs one of the others.</para></summary>
    internal bool NpcCastWard(string spellKey, string casterName)
    {
        if (Content.SpellByKey(spellKey) is not SpellDef sp) return false;
        if (!Content.SpellParams.TryGetValue(spellKey, out var row)) return false;

        row.TryGetValue("duration", out var durText);
        if (!int.TryParse(durText, out int durMs) || durMs <= 0) return false;

        row.TryGetValue("category", out var category);
        row.TryGetValue("stat", out var stat);
        row.TryGetValue("amount", out var amountText);
        category ??= ""; stat ??= "";
        if (!int.TryParse(amountText, out int amount)) amount = 0;

        if (category.Length > 0 && HasStatusCategory(category)) return false;

        ReceiveCurse(stat, amount, durMs, sp.Key, sp.Name, category);
        if (Content.FxFor(sp) is SpellFx fx)
            BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        // Same wording a player's ally-cast produces (TellTarget), with the NPC's name in the caster slot.
        var flavor = Content.TargetTextFor(sp.Key);
        SendMiniText(flavor.Length > 0 ? flavor : $"{casterName} casts {sp.Name} on you.");
        SendStats();
        Log.Info($"      (npc) {casterName} casts {sp.Name} -> ward [{category}] {stat}{amount:+0;-0} {durMs}ms");
        return true;
    }

    // ---- the two spells the mythic alliance NPCs cast (Server/MythicAlliance.cs) -----------------
    // Both are "NPC SPELLS" in Spells.csv (the 50000 block) — named, so the client-facing wording is right,
    // but carrying no SpellParams row of their own, because neither is castable by a player and neither is a
    // ward. They are scripted acts, so they are written as acts here rather than run through the ward path.

    /// <summary>Rebirth: "a full healing, ressurection spell" (Atlas). The mythic casts it on an ally who
    /// comes back and says its enemy's name again — the standing reward for the alliance, and the reason
    /// players kept a guard room in walking distance. Identical restoration to a shaman's revival, so it
    /// works on a ghost and on a merely-battered ally alike.</summary>
    internal void NpcCastRebirth(string casterName)
    {
        PlayNpcSpellFx("rebirth");
        ReviveInPlace(IsDead ? $"{casterName} casts Rebirth on you." : $"{casterName} restores you.");
    }

    /// <summary>Stormstrike, then home: what the mythic does to someone who is sworn to its enemy, or who
    /// turns down the alliance to its face. Nussan is the source for the pairing — "If you try to do an
    /// alliance with the enemy of an animal you already alli'ed with, it will zap you and send you to
    /// tavern" — and RTK's script is the same two beats (<c>stormstrike.cast</c> then <c>returnToInn</c>).
    ///
    /// <para>The damage is the only Stormstrike figure in our DATA — spell_effects.csv's
    /// <c>stormstrike_mage</c>, <c>125 + level*4 + ceil(((will+1)/2)*3.5)</c> — read against the VICTIM,
    /// because the mythic has no caster stat block to read it against. That keeps the blow proportionate at
    /// both ends of the level range instead of picking a constant out of the air, and it lands where the
    /// sources leave it: a hard slap and a long walk back, not an execution. It can still finish someone who
    /// was already nearly dead, which is what "Die scum!" is doing in the line above it. (It is NOT the only
    /// figure in existence, which this comment used to claim: RTK's own Spells/NPCs/stormstrike.lua:9 deals
    /// <c>ceil(target.health * 0.50)</c> — proportional to CURRENT HP, needing no caster block either.
    /// Adopting that is a separate call and is not made here; see #79.)</para>
    ///
    /// <para>Lands through <see cref="ReceiveNpcSpell"/> and NOT <see cref="ReceiveEnvironmentDamage"/>: the
    /// mythic is a named caster, so a Harden Body ward stops the blow, as RTK's removeHealthExtend does. The
    /// banish is not part of the spell and runs either way — RTK calls stormstrike.cast and returnToInn as
    /// two beats, and warding the zap does not buy you the right to stay in the chamber.</para></summary>
    internal void NpcCastStormstrike(string casterName)
    {
        PlayNpcSpellFx("stormstrike");
        int dmg = 125 + CharLevel * 4 + (int)Math.Ceiling(((CharWill + 1) / 2.0) * 3.5);
        ReceiveNpcSpell(dmg, casterName, $"{casterName} attacks you with Stormstrike spell.");
        ReturnToInn();
        SendStats();
    }

    // Play a scripted NPC cast's animation over the player. The 50000-block spells carry no spell_effects row
    // of their own; where a player-castable twin exists (stormstrike_mage) its row holds the animation, so try
    // the NPC key first and fall back to the twin. No fx anywhere is not an error — the act still happened.
    private void PlayNpcSpellFx(string key)
    {
        var sp = Content.SpellByKey(key) ?? Content.SpellByKey(key + "_mage");
        var fx = sp is null ? null : Content.FxFor(sp) ?? (Content.SpellByKey(key + "_mage") is SpellDef twin ? Content.FxFor(twin) : null);
        if (sp is null || fx is null) return;
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
    }

    // Cure: remove every active status of this category from the caster (RTK removes durations by category). No
    // fade line (a cure is a deliberate cleanse, not a lapse). Returns how many were cleared.
    internal int LuaCureCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return 0;
        int n = BuffRemoveAll(b => b.Category.Length > 0 && CureMatches(b.Category, category));   // curing `curses` also clears minor curses
        // The sleep hold lives in its own timer as well as the buff list (the gates read the timer, so they
        // have to agree). No shipped Cure carries cureCat "sleeps" today — this keeps the two in step if one
        // ever does, rather than leaving a player whose slot is clear but who still can't swing.
        if (CureMatches("sleeps", category)) WakeUp(byDamage: false);
        if (CureMatches("venoms", category)) CurePoison();   // same reason: the DoT rides its own timer too
        if (n > 0) SendStats();
        return n;
    }

    // ---- Tier-3 utility/target primitives (mana_steal/mana_gift/cleanse/revive/leap/mana_battery verbs) --------
    // Thin wrappers so the LOGIC (guards, formulas, ordering, messages) lives in the Lua verbs while these do the
    // mechanical act, each mirroring the logic the removed C# CastManaSteal/CastManaGift/CastCleanse/CastRevive/
    // CastLeap/CastManaBattery handlers used to own (none of them remain; Lua() fails the cast if the verb isn't
    // loaded). Self HP/MP setters clamp to the effective caps.
    internal uint LuaMaxMp        => EffMaxMp;
    internal void LuaSetHp(int n)   { _char.Hp = (uint)Math.Clamp(n, 0, (int)EffMaxHp); SendStats(); }
    internal void LuaSetMana(int n) { _char.Mp = (uint)Math.Clamp(n, 0, (int)EffMaxMp); SendStats(); }

    // Resolve the targeted PLAYER for a Tier-3 utility cast (explicit id -> that player incl. self, else faced
    // peer). Stored for the rest of the cast so the target getters/setters below all act on the same session.
    private Session? _pcSpellTarget;
    internal bool LuaResolvePcTarget(SpellDef sp, uint? targetId)
    {
        _pcSpellTarget = ResolvePcCastTarget(targetId);
        if (_pcSpellTarget is null) { LogNoTarget(sp); return false; }
        return true;
    }
    internal uint LuaTargetMana    => _pcSpellTarget?._char.Mp ?? 0;
    internal uint LuaTargetMaxMana => _pcSpellTarget?.EffMaxMp ?? 0;
    internal bool LuaTargetIsDead  => _pcSpellTarget?.IsDead ?? false;
    internal bool LuaTargetIsSelf  => _pcSpellTarget is not null && ReferenceEquals(_pcSpellTarget, this);
    internal bool LuaTargetInGroup => _pcSpellTarget is not null && _party is not null && ReferenceEquals(_pcSpellTarget._party, _party);
    internal int  LuaTargetArmor   => _pcSpellTarget is null ? 0 : _pcSpellTarget._char.Ac + _pcSpellTarget.Totals().armor;  // effective AC (lower=better)
    internal int  LuaTargetWill    => _pcSpellTarget?.LuaWill ?? 0;
    internal void LuaSetTargetMana(int n)
    {
        if (_pcSpellTarget is null) return;
        _pcSpellTarget._char.Mp = (uint)Math.Clamp(n, 0, (int)_pcSpellTarget.EffMaxMp);
        _pcSpellTarget.SendStats();
    }
    internal void LuaTellTarget(SpellDef sp) { if (_pcSpellTarget is not null) TellTarget(_pcSpellTarget, sp); }
    internal void LuaFlushTarget()           { if (_pcSpellTarget is null) return; _pcSpellTarget.FlushDurations(); _pcSpellTarget.SendStats(); }
    internal void LuaReviveTarget(SpellDef sp)
    {
        if (_pcSpellTarget is null) return;
        _pcSpellTarget.ReviveAt(_pcSpellTarget._char.Map, _pcSpellTarget._char.X, _pcSpellTarget._char.Y, $"{Snapshot().Name} cast {sp.Name} on you.");
    }

    // Revive the CASTER in place, returning whether they were actually dead (so the verb can pick its
    // flavour line). Distinct from LuaSetHp, which only moves the number: ghost form is DERIVED
    // from Hp==0 but the client is only redrawn by RefreshAppearance, so raising HP through the plain setter
    // leaves a living player rendered as a ghost. Hyun Moo's revival is the self-revive with no relocation —
    // unlike Silver Thread or the poet Resurrect family, which move you to a Shaman.
    // ---- Chung Ryong's Rage primitives -------------------------------------------------------------
    // The one fury that CLIMBS: recast inside its window to go tier 1→6, each tier costing more, hitting
    // harder, adding AC, and charging a vita price when it finally lapses. The tier can't live in Lua because
    // RegenTick reads it to apply that wear-out drain, so C# keeps the field and the verb drives it: the verb
    // owns the tier TABLE and the climb rule, this owns recording the tier and arming the effects.

    /// <summary>The Chung Ryong rage tier currently recorded (0 = none). Pair with <c>rageActive</c> — a stale
    /// tier lingers after the fury lapses, which is exactly what RegenTick's drain needs.</summary>
    internal int LuaCrRageTier => _crRageTier;

    /// <summary>Record a Chung Ryong rage tier and arm its effects: the swing multiplier + duration, and the
    /// tier's AC as a KEYED buff so each climb silently replaces the previous tier's rather than stacking.
    /// <paramref name="durMs"/> greater than zero starts a FRESH run; zero is a climb inside the run already
    /// burning and deliberately leaves its deadline alone (RTK arms setDuration in its first-cast branch only —
    /// every tier-up resets the aether and nothing else), so the fury always ends in its vita drain.</summary>
    internal void LuaSetCrRage(int tier, int mult, int ac, int durMs, string name)
    {
        long now = Environment.TickCount64;
        if (durMs > 0)
        {
            // Settle a lapsed run's price BEFORE arming the new one: RegenTick fires the wear-out, so without
            // this a recast landing in the same world tick the fury expired would silently void the drain.
            if (_crRageTier > 0 && now >= _rageUntil) ChungRyongRageWearOff();
            _rageUntil = now + durMs;
        }
        _crRageTier = tier;
        _rageAmount = mult;
        _rageName   = name;                             // buff box shows the SPELL, not the tier (see BuffBoxText)
        BuffRemoveAll(b => b.Key == CrRageAcKey);
        if (ac != 0)
            BuffAdd(new ActiveBuff { Stat = "armor", Amount = ac, Expires = _rageUntil, Key = CrRageAcKey, Name = name });
        SendStats();
        MarkDirty();
    }

    /// <summary>The keyword classifier's verdict for a spell with NO spell_effects row (Content.EffectOf):
    /// "heal" - "damage" - "buff" - "other". The ~266 spells the export never covered are dispatched on this
    /// alone, so exposing it is what lets the generic fallback live in Lua like everything else.</summary>
    internal string LuaEffectKind(SpellDef sp) => Content.EffectOf(sp) switch
    {
        Content.SpellEffect.Heal   => "heal",
        Content.SpellEffect.Damage => "damage",
        Content.SpellEffect.Buff   => "buff",
        _                          => "other",
    };

    /// <summary>Play a RAW anim/sound over the resolved target mob rather than the caster - the generic
    /// fallback's zap, which has no spell_effects row of its own to draw from (so ctx:fxTarget can't serve).</summary>
    internal void LuaFxRawTarget(int anim, int sound, uint? targetId)
    {
        var mob = ResolveCastTarget(targetId);
        if (mob is not null) BroadcastFx(mob.Id, anim, sound);
    }

    /// <summary>The Hyun Moo revival's <c>ctx:reviveSelf()</c> — the third revive path, alongside
    /// <see cref="ReviveInPlace"/> and <see cref="ReviveAt"/>: it drops the ghost form and takes
    /// <see cref="IsDead"/> false, so it is a revive in #196's sense and ends in <c>SaveChar</c> for the same
    /// reason. <c>MarkDirty</c> left the death's own row (Hp 0, penalised exp) on disk for up to
    /// <c>AutoSaveMs</c>, and a crash in that window logged the player back in as a ghost.
    /// <para>This method takes no <see cref="EnterState"/> of its own: it is reached only from the cast
    /// handler, whose monitor is already held across the Lua verb, and that is the monitor
    /// <c>SaveChar</c>'s <c>AssertStateHeld</c> requires (as <c>MarkDirty</c>'s did before it).</para></summary>
    internal bool LuaReviveSelf()
    {
        bool wasDead = _char.Hp == 0;
        _char.Hp = EffMaxHp;
        if (wasDead) RefreshAppearance();   // drop the ghost look for us and everyone watching
        SendStats();
        SaveChar();   // #196: a revive is on disk before it returns, the way the Die() that preceded it is
        return wasDead;
    }

    // ---- pack-slot primitives (the Mend Equipment family) --------------------------------------------
    // The first CAPABILITY a verb has to read the player's bag. Deliberately split query-from-action so the
    // VERB owns every decision and message and C# only executes: LuaPackSlotState classifies the slot,
    // LuaPackSlotName names it, LuaRepairPackSlot does the one thing the engine must do. (A single
    // "mendFirstSlot()" primitive would have been shorter and would have put the whole spell back in C#.)

    /// <summary>Classify pack slot <paramref name="slot"/> (0-based) for a repair spell:
    /// <c>empty</c> · <c>notgear</c> (not equipment, or has no durability rating) · <c>perfect</c> (already
    /// full) · <c>ok</c> (repairable and damaged).</summary>
    internal string LuaPackSlotState(int slot)
    {
        var it = InvAt(slot);
        var def = it is null ? null : Content.ItemById(it.ItemId);
        if (it is null || def is null) return "empty";
        if (!def.IsEquip || def.Durability == 0) return "notgear";
        // Undamaged gear reports "perfect" whatever else it is — RTK's smith only reaches its
        // "Sorry, but this item cannot be repaired!" line after `choice.dura < choice.maxDura` (player.lua:1547),
        // so a full-durability item is never told it is unrepairable. This ordering also keeps the ~480
        // ItmIndestructible rows (which sit at full durability forever) on the "perfect" line they had before
        // Unrepairable became data-driven.
        if (it.Dura >= def.Durability) return "perfect";
        // Unrepairable gear (ItmRepairable = 0: the totem helms, the smith-forged subpath weapons, the headbands)
        // degrades permanently — no smith and no repair spell restores it. Nothing to do with being bonded.
        // "notgear" makes the repair verb say "<name> cannot be repaired." (spell_verbs.lua), the right line.
        return def.Unrepairable ? "notgear" : "ok";
    }

    /// <summary>Display name of whatever sits in pack slot <paramref name="slot"/> ("" if empty).</summary>
    internal string LuaPackSlotName(int slot)
    {
        var it = InvAt(slot);
        var def = it is null ? null : Content.ItemById(it.ItemId);
        return def?.Name ?? "";
    }

    /// <summary>Restore pack slot <paramref name="slot"/> to full durability and reset its 50/25/10/5/1%
    /// warning ladder, redrawing the bag cell. No-op unless the slot is in the <c>ok</c> state above.</summary>
    internal void LuaRepairPackSlot(int slot)
    {
        if (LuaPackSlotState(slot) != "ok") return;
        var it = InvAt(slot)!;
        var def = Content.ItemById(it.ItemId)!;
        it.Dura = def.Durability;
        it.Repair = 0;              // the warning ladder starts over (Session.CheckDura)
        MarkDirty();
        SendAddItem(it);            // redraw the cell so the new durability shows
    }

    // Leap (RTK race): step up to maxDist tiles in the faced direction, stopping at the last passable tile (same
    // BlockedMove collision as movement), then re-anchor the viewport there. Returns tiles moved (0 = blocked, no
    // move). A faithful port of CastLeap's movement half; the verb owns the mana/cooldown around it.
    internal int LuaLeap(int maxDist)
    {
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (md is null) return 0;
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int dist = 0;
        for (int step = 1; step <= maxDist; step++)
        {
            int nx = _char.X + dx * step, ny = _char.Y + dy * step;
            if (nx < 0 || ny < 0 || nx >= _char.MapXs || ny >= _char.MapYs || md.BlockedMove(nx, ny, _facing)) break;
            dist = step;
        }
        if (dist == 0) return 0;
        ushort nx2 = (ushort)(_char.X + dx * dist), ny2 = (ushort)(_char.Y + dy * dist);
        string mapName = Content.Maps.TryGetValue(_char.Map, out var mi) ? mi.Name : "";
        EnterMap(_char.Map, _char.MapXs, _char.MapYs, nx2, ny2, mapName);
        return dist;
    }

    // ---- Tier-4 world-effecting primitives -----------------------------------------------------------------
    // Each wraps the irreducible engine core its removed C# CastX handler used to own (none of them remain),
    // so the Lua verb owns only guards/mana/messages. Where a constant is data-bound (per-kind trap mana, pet cap, gate boxes) the
    // primitive resolves it from Content, not a CSV row — these spells are classified in C#, not by a params row.
    internal bool LuaWarpOut          => Content.WarpOut(_char.Map);
    internal int  LuaSpellMana(SpellDef sp) { var fx = Content.FxFor(sp); return fx is not null && fx.Mana > 0 ? fx.Mana : 5; }
    internal int  LuaSpellAether(SpellDef sp) => Content.FxFor(sp)?.Aether ?? 0;
    internal void LuaMarkNarrated()   => _castNarrated = true;
    internal bool LuaHasLegend(string mark) => HasLegend(mark);
    internal void LuaForgetSpell(SpellDef sp) => ForgetOneSpell(sp.Id);

    // Gateway core: region+gate lookup, random landing tile, EnterMap + self-only arrival line.
    internal bool LuaGateway(string? answer)
    {
        int region = Content.RegionOf(_char.Map);
        if (!Content.GatewayRegions.TryGetValue(region, out var r) || !Content.Maps.TryGetValue(r.Map, out var map))
        { SendMiniText("Can't find any gates!"); return false; }
        char dir = (answer ?? "").ToLowerInvariant().FirstOrDefault(char.IsLetter);
        if (!r.Gates.TryGetValue(dir, out var box))
        { SendMiniText("Which gate? Answer North, East, South or West."); return false; }
        ushort x = (ushort)Random.Shared.Next(box.Xlo, box.Xhi + 1);
        ushort y = (ushort)Random.Shared.Next(box.Ylo, box.Yhi + 1);
        string gate = dir switch { 'n' => "North", 'e' => "East", 'w' => "West", 's' => "South", _ => "" };
        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        SendSound(708, _char.Id);
        SendMiniText($"You have arrived at the {gate} gate.");   // live wording — direction only, no city/map name
        _castNarrated = true;
        Log.Info($"      Gateway(lua) -> region {region} {r.City} {gate} gate: map {map.Id} ({x},{y})");
        return true;
    }

    // Return core (see CastReturn/ReturnToInn): the verb owns the 30-mana debit + warpOut guard.
    internal void LuaReturnHome() { ReturnToInn(); SendStats(); }

    // Divination core: the resolved PC target is _pcSpellTarget (set by ctx:pcTarget). Builds
    // the inspect popup for the caster; spy variant appends the target's inventory. Self-narrates.
    internal bool LuaIsSpy(SpellDef sp) => Content.IsDivinationSpySpell(sp);
    internal int  LuaTargetLevel => _pcSpellTarget?._char.Level ?? 0;
    internal void LuaDivine(SpellDef sp, bool showInventory)
    {
        if (_pcSpellTarget is not { } target) return;
        var tc = target._char;
        var text = new System.Text.StringBuilder();
        text.Append(ClassTitleOf(tc)).Append(' ').Append(tc.Name).Append("     Level ").Append(tc.Level).Append('\n');
        text.Append(tc.Title ?? "").Append('\n');
        text.Append("Might: ").Append(target.EffMight)
            .Append(" Will: ").Append(tc.Will + target.Totals().will)
            .Append(" Grace: ").Append(tc.Grace + target.Totals().grace).Append('\n');
        if (showInventory)
        {
            text.Append("Items: ");
            foreach (var it in tc.Inventory)
            {
                var def = Content.ItemById(it.ItemId);
                if (def is not null) text.Append(def.Name).Append(' ');
            }
        }
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendScriptMessageP(tc.Id, text.ToString(), DialogPortrait.None, prev: false, next: false);
        _castNarrated = true;
        Log.Info($"      Divine(lua) -> divined '{tc.Name}' (inventory={showInventory})");
    }

    // Spot Traps core: draw a caster-only marker on every hidden trap within 15 tiles.
    internal int LuaRevealTraps()
    {
        var traps = RevealableTrapsNear();
        MarkRevealedTraps(traps);
        Log.Info($"      SpotTraps(lua) -> revealed {traps.Length} trap(s) near ({_char.X},{_char.Y})");
        return traps.Length;
    }

    /// <summary>Draw the caster-only marker (RTK's item 99) on each revealed trap's tile. Registered per TRAP
    /// id, so a second cast over ground already marked re-uses the existing sword instead of laying another
    /// one on the same tile, and a trap that later goes off can take its own marker with it
    /// (World -> <see cref="ClearTrapMarker"/>, RTK <c>removeTrapItem</c>). The reveal reaches 15 tiles but the
    /// 0x07 draw is viewport-gated to ~8, so the draw is delegated to <see cref="SyncGroundItems"/>: markers
    /// out of view now are drawn as we walk to them instead of being thrown away.</summary>
    private void MarkRevealedTraps(Trap[] traps)
    {
        ushort markerIcon = Content.ItemById(99)?.Icon ?? 0;
        foreach (var t in traps)
            AddTrapMarker(t.Id, new GroundItem { Id = _world.AllocateItemId(), ItemId = 99, X = t.X, Y = t.Y, Graphic = markerIcon });
        SyncGroundItems(_world.ItemsOn(_char.Map));   // draws every marker now in view (and any we've walked away from, hidden)
    }

    // Filch core: grab the item on the faced tile — coins to purse, else to pack (put back if full).
    internal void LuaFilch()
    {
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int tx = _char.X + dx, ty = _char.Y + dy;
        if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) return;
        if (_world.PeerAt(_char.Map, tx, ty) is not null) return;
        // A thief does NOT get to filch a death pile: that lock is the whole point of "your items will be
        // recovered even if a would-be thief is standing on them" (RTK f1npc.lua). Silent, like every other
        // filch no-op — the spell simply comes up empty.
        var gi = _world.PickUp(_char.Map, tx, ty, _char.Id);
        if (gi is null) return;
        if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); MarkDirty(); return; }
        var def = Content.ItemById(gi.ItemId);
        if (def is null) return;
        if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName, owner: gi.Owner))
            _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = gi.ItemId,
                X = (ushort)tx, Y = (ushort)ty, Amount = gi.Amount, Dura = gi.Dura, Graphic = gi.Graphic, CustomName = gi.CustomName,
                Owner = gi.Owner });
        Log.Info($"      Filch(lua) -> grabbed item {gi.ItemId} from ({tx},{ty})");
    }

    // Set Trap core (see CastTrap + ApplyCast's dispatcher block): resolve the specific trap (typed answer for the
    // set_trap dispatcher, else the spell itself), enforce its level gate, debit its per-kind mana, place it + fx.
    internal bool LuaPlaceTrap(SpellDef sp, string? answer)
    {
        SpellDef trapSpell; Content.TrapKind kind; int level, mana;
        if (Content.IsTrapDispatcher(sp))
        {
            var trapKey = Content.TrapKeyForAnswer(answer ?? "");
            var ts = trapKey is null ? null : Content.SpellByKey(trapKey);
            var info = ts is null ? null : Content.TrapSpellFor(ts);
            if (ts is null || info is null)
            { SendMiniText("Select: Dart trap, Snare trap, Repeating dart, Flash trap, Spear trap, Poison trap, Death trap, Sleep trap"); return false; }
            trapSpell = ts; kind = info.Value.Kind; level = info.Value.Level; mana = info.Value.Mana;
            if (_char.Level < level) { SendMiniText($"You must be level {level} to set that trap."); return false; }
        }
        else if (Content.TrapSpellFor(sp) is (Content.TrapKind k, int _, int mn))
        { trapSpell = sp; kind = k; mana = mn; }
        else return false;

        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        _world.PlaceTrap(_char.Map, _char.X, _char.Y, Content.TrapWireKind(kind), _char.Id);
        SendStats();
        var fx = Content.FxFor(trapSpell);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, trapSpell.PathId), Content.EffectSound(fx, trapSpell.PathId));
        Log.Info($"      {trapSpell.Name}(lua) -> placed {kind} trap at ({_char.X},{_char.Y})");
        return true;
    }

    // Bladestorm core: place the decoy; the verb owns mana/cooldown/fx.
    internal void LuaPlaceBladestorm(int lifetimeMs)
    {
        _world.PlaceTrap(_char.Map, _char.X, _char.Y, "bladestorm", _char.Id, Environment.TickCount64 + lifetimeMs);
        Log.Info($"      Bladestorm(lua) -> trap placed at ({_char.X},{_char.Y}), expires in {lifetimeMs}ms");
    }

    // Pet Summon core (see CastPetSummon): spawn this pet spell's mob on the first free tile of a CLOCKWISE
    // sweep that starts at the direction the caster is facing — front, then right, then behind, then left
    // (facing dirs are 0=N 1=E 2=S 3=W, so clockwise is simply +1). With all four cardinal neighbours taken,
    // the summon lands ON the poet's own tile and stacks there; the cap (Content.PetCapFor) is 4/6/8, so a
    // high-level poet WILL stack, by design.
    //
    // This replaces a front-tile-or-stack rule, which is what RTK's cotw_SpawnSetThreat does. Ported straight,
    // it meant a poet summoning four pets in a row got the first one in front and the other three piled on his
    // own tile — the ring you actually want (and the reason summons work as a barricade) took four deliberate
    // turns to build. Sweeping the neighbours instead builds it in one place, and the front tile is still
    // preferred, so a single summon lands exactly where it always did.
    //
    // Each pet arrives facing back at the poet — computed from ITS OWN placement direction, not the caster's,
    // so the one on your left looks right at you rather than copying whichever way you happened to be turned.
    internal int  LuaPetCount => _world.PetCountFor(_char.Map, _char.Id);
    internal int  LuaPetCap   => Content.PetCapFor(_char.Level);
    internal int  LuaPetMana(SpellDef sp)      => Content.PetSpellFor(sp) is (string _, int _, int m, int _) ? m : 0;
    internal int  LuaPetCooldownMs(SpellDef sp) => Content.PetSpellFor(sp) is (string _, int _, int _, int c) ? c : 0;
    internal bool LuaSummonPet(SpellDef sp)
    {
        if (Content.PetSpellFor(sp) is not (string mobKey, int _, int _, int _)) return false;
        var def = Content.MobByKey(mobKey);
        if (def is null) return false;
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);

        ushort sx = _char.X, sy = _char.Y;           // fallback: stacked on the poet, once the ring is full
        byte placeDir = (byte)((_facing + 2) & 3);   // …and a stacked pet just looks the way the poet came from
        for (int step = 0; step < 4; step++)
        {
            byte dir = (byte)((_facing + step) & 3);
            int tx = _char.X + dir switch { 1 => 1, 3 => -1, _ => 0 };
            int ty = _char.Y + dir switch { 0 => -1, 2 => 1, _ => 0 };
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            if (md is not null && md.BlockedMove(tx, ty, dir)) continue;
            if (_world.MobAt(_char.Map, tx, ty) is not null) continue;
            if (_world.PeerAt(_char.Map, tx, ty) is not null) continue;
            sx = (ushort)tx; sy = (ushort)ty;
            placeDir = (byte)((dir + 2) & 3);   // face back at the poet from wherever it ended up
            break;
        }

        var mob = SummonWorldMob(def.Look, sx, sy, def.Name, def.Hp, dir: placeDir, color: def.Color,
                                  exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
        _world.OwnSummonedMob(mob, _char.Id, 300_000);   // conjured, so World.Tick despawns it at PetExpiresAt
        Log.Info($"      {sp.Name}(lua) -> summoned pet '{mob.Name}' ({mob.Id}) for player {_char.Id} at ({sx},{sy})");
        return true;
    }

    // Morph core (see CastMorph): the resolved plan (look/female-look/mana/duration) is staged by CastMorphArch
    // before the verb runs — the verb owns the already-active + mana guards, this applies + rebroadcasts.
    private (ushort look, ushort lookF, int mana, int dur)? _pendingMorph;
    internal void LuaSetPendingMorph(ushort look, ushort lookF, int mana, int dur) => _pendingMorph = (look, lookF, mana, dur);
    internal int  LuaMorphMana => _pendingMorph?.mana ?? 0;
    internal bool LuaMorphActive()
    {
        if (_pendingMorph is not { } m) return false;
        ushort newLook = (m.lookF != 0 && _char.Sex == 1) ? m.lookF : m.look;
        return _morphLook != 0 && _morphLook == newLook;
    }
    internal void LuaApplyMorph(SpellDef sp)
    {
        if (_pendingMorph is not { } m) return;
        ushort newLook = (m.lookF != 0 && _char.Sex == 1) ? m.lookF : m.look;
        BuffRemoveAll(b => b.Key == _morphKey);
        _morphLook = newLook;
        _morphColor = 0;
        _morphUntil = Environment.TickCount64 + m.dur;
        _morphKey = sp.Key;
        BuffAdd(new ActiveBuff { Stat = "", Amount = 0, Expires = _morphUntil, Key = sp.Key, Name = sp.Name });
        SendStats();
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        ShowPlayer(this);
        Log.Info($"      {sp.Name}(lua) -> morph look={_morphLook} for {m.dur}ms");
    }

    // Propose core: the verb owns the engaged/married guard; this fires the async ask flow.
    internal bool LuaPropose(SpellDef sp) { _ = RunProposeAsync(sp); return true; }

    // ---- combat-stray primitives (sacrifice strikes + ambush) ----------------------------------------------
    // The facing-tile physical strikes. The per-family FORMULAS (damage/mana/cooldown/HP cost) live in the Lua
    // verb; these primitives do the irreducible engine ops — resolve the faced mob, armor-net + apply, the
    // overkill backflow/overflow, and (ambush) the leap + swing. Mirror the logic the removed CastSacrificeStrike/
    // CastAmbush handlers used to own (neither remains). A single stash holds the resolved target for the rest of the cast.
    private Mob? _frontStrikeMob;
    private Session? _frontStrikePc;
    private int  _frontStrikeX, _frontStrikeY;

    internal string LuaSacrificeFamily(SpellDef sp) => Content.SacrificeFamilyFor(sp)?.ToString() ?? "";
    internal int    LuaAlignment  => _char.Alignment;
    // (There is deliberately no "is Baekho's Rage specifically active" primitive. One existed — `_rageAmount
    // == 5 && EffRage > 1` — on the premise that Baekho's Rage was set apart from the ordinary furies. RTK's
    // own spellTables.lua lists `baekhos_rage_rogue` inside `lesserFuries`, between Wolf's Fury and Soul's
    // Rage, so there is no such distinction to test: it is a fury like the others, and the fury is already
    // fully served by EffRage (the swing multiplier) and LuaRageActive (the exclusivity gate). Its only
    // caller was the sacrifice verb's x1.5, which had misread CHIN-BAEK-HO-RYUNG — a Black Potion ward, a
    // different mechanic with a near-identical name — for this. See Content.TakesChinBaekHoRyung.)

    internal bool LuaSacFrontMob()
    {
        var (fx, fy) = FrontTile();
        _frontStrikeX = fx; _frontStrikeY = fy;
        _frontStrikePc = null;
        var m = _world.MobAt(_char.Map, fx, fy);
        _frontStrikeMob = (m is not null && m.Alive) ? m : null;
        if (_frontStrikeMob is not null) return true;
        // No mob on the faced tile: in a PvP map a player standing there is a legal target too (RTK canPK),
        // so Whirlwind/Berserk & the rogue strikes land the same one-tile hit against a peer as against a mob.
        // Off a PvP map there's nothing to hit — the strike swings at empty air (mana/cooldown still spent).
        if (Content.IsPvpMap(_char.Map)) _frontStrikePc = _world.PeerAt(_char.Map, fx, fy);
        return _frontStrikePc is not null;
    }
    internal int LuaSacApply(SpellDef sp, int damage)
    {
        var fam = Content.SacrificeFamilyFor(sp) ?? Content.SacrificeFamily.Berserk;
        // PvP: the strike landed on a player. Routed through the canonical PvP damage path (deflect roll,
        // Deduction, death penalty) — mana was already spent in the verb, so pass 0.
        //
        // OVERKILL IS RETURNED, so the strike feeds backflow/overflow off a player kill exactly as off a mob
        // kill. This used to return 0 flat. The Rogue tutor doc's central example is a PK kill and ends "890k
        // is split between vita/mana; 445k healed to both stats", and the Overflow FAQ answers "Does overflow
        // damage work in PK? Yes, it does" — so refusing overkill in PvP was our invention, not the game's.
        // Both consumers stay era-gated (see LuaOverflow / LuaBackflow).
        //
        // The target's armor is NOT applied here: ReceiveSpellDamage nets it at intake for every PvP path
        // (see the note there), so netting it again would land twice. The archive's worked example — "DA's
        // iPoeti for 1800k damage to 0 AC. AC modifier -50, damage done is 900k" — is satisfied there.
        if (_frontStrikePc is { } pc)
        {
            uint prePcHp = pc._char.Hp;
            if (!HitPlayerWithSpell(pc, damage, 0, sp, out int landedPvp)) return 0;   // refused (not a PvP map)
            BroadcastFx(pc._char.Id, SacrificeAnim(fam), SacrificeSound(fam));
            // A deflect reports landed=0, which makes this negative — no overkill from a resisted strike.
            int pvpOverkill = landedPvp - (int)prePcHp;
            Log.Info($"      {sp.Name}(lua) -> sacrifice strike ({fam}) on player '{pc._char.Name}' " +
                     $"raw {damage}, landed {landedPvp}, overkill {pvpOverkill} (pvp)");
            return pvpOverkill;
        }
        if (_frontStrikeMob is not { } mob) return 0;
        int netDamage = Combat.ApplyArmor(damage, mob.Ac, floor: -95);
        int overkill = netDamage - (int)mob.Hp;   // overkill uses the mob's PRE-hit HP (RTK), read before TryDamage
        _world.TryDamage(_char.Map, mob, netDamage, out bool died, _char.Id);
        BroadcastFx(mob.Id, SacrificeAnim(fam), SacrificeSound(fam));
        ShowDamageResult(mob.Id, mob, died);
        if (died) { uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp); AwardKillExp(reward, _char.Map, mob.X, mob.Y, mob.Key); }
        Log.Info($"      {sp.Name}(lua) -> sacrifice strike ({fam}) dmg {netDamage} overkill {overkill}");
        return overkill;
    }
    // ERA GATES. Both of these mechanics postdate our 2001-07-09 target by years — warrior overflow by six
    // (2007-04-10), the rogue refund by seven (2008-09-18) — so both are OFF in the default configuration and
    // the four strikes resolve as a plain one-tile hit. The gate lives here rather than in spell_verbs.lua so
    // it cannot be lifted by a script edit alone; see Server/Era.cs and docs/common/Era-Gating.md.
    //
    // Deliberately TWO keys, not one. KRU shipped the rogue side seventeen months after the warrior side as an
    // explicit balance patch, so 2007-04-10..2008-09-18 is a real era in which warriors had overflow and rogues
    // had nothing — the window the whole path-balance controversy happened in. One key could not express it.
    internal void LuaBackflow(int overkill, int preHp, int preMp)
    {
        if (!Era.Has(Era.RogueOverkill)) return;
        ApplyBackflow(overkill, (uint)preHp, (uint)preMp);
    }
    internal void LuaOverflow(SpellDef sp, int overkill)
    {
        if (!Era.Has(Era.WarriorOverflow)) return;
        ApplyOverflow(overkill, _frontStrikeX, _frontStrikeY, sp);
    }

    internal bool LuaAmbushMob()
    {
        var (fx, fy) = FrontTile();
        _frontStrikeMob = _world.MobAt(_char.Map, fx, fy);
        return _frontStrikeMob is not null;
    }
    internal bool LuaAmbushLeap()
    {
        if (_frontStrikeMob is not { } mob) return false;
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        int[] order = { _facing, (_facing + 1) & 3, (_facing + 3) & 3 };   // opposite our approach, then the two flanks
        foreach (int sideDir in order)
        {
            var (tx, ty) = StepDir(mob.X, mob.Y, sideDir);
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            if (md?.BlockedMove(tx, ty, sideDir) ?? false) continue;
            if (_world.PeerAt(_char.Map, tx, ty) is not null) continue;
            if (_world.MobAt(_char.Map, tx, ty) is not null) continue;
            _facing = (byte)((sideDir + 2) & 3);   // arrive facing back toward the mob
            string mapName = Content.Maps.TryGetValue(_char.Map, out var mi) ? mi.Name : "";
            EnterMap(_char.Map, _char.MapXs, _char.MapYs, (ushort)tx, (ushort)ty, mapName);
            SendAction(_char.Id, ActionType.Attack, time: 8, param: 0);
            _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, ActionType.Attack, 8, 0), except: this);
            return true;
        }
        return false;
    }
    internal void LuaAmbushStrike(SpellDef sp)
    {
        if (_frontStrikeMob is not { } mob) return;
        var (dmg, crit) = PlayerSwingDamage(SwingTarget.Of(mob));
        if (dmg <= 0) return;   // whiff (Combat.RollPlayerSwingRtk) — silent, no text
        if (_world.TryDamage(_char.Map, mob, dmg, out bool died, _char.Id))
        {
            var weapon = _char.Equipment.FirstOrDefault(e => e.Slot == 1);
            if (weapon is not null) DeductDura(weapon);
            ShowDamageResult(mob.Id, mob, died, crit ? (byte)0xFF : HitCritByte);
            PlayHitSfx(mob.Id);   // physical strike — same on-connect impact sfx as a plain swing
            Log.Info($"      {sp.Name}(lua) -> leapt behind '{mob.Name}' and struck for {dmg}{(crit ? " (CRIT)" : "")}");
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardKillExp(reward, _char.Map, mob.X, mob.Y, mob.Key);
            }
        }
    }

    // The stat variables an RTK spell formula reads (player.level, player.will, target.baseHealth, …). Effective
    // (base + gear + buff) values, so a buffed caster hits harder. enchant/rage/invis reflect the real armed
    // multiplier now (EffEnchant/EffRage/Stealthed) in case any Damage-archetype formula ever reads them; fury
    // has no separate tracked state (player.rage covers every fury tier) so it stays a no-op 1.
    private Dictionary<string, double> SpellVars(Mob? target)
    {
        var t = Totals();
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["player.level"]      = _char.Level,
            ["player.will"]       = _char.Will + t.will,
            ["player.grace"]      = _char.Grace + t.grace,
            ["player.might"]      = EffMight,
            ["player.magic"]      = _char.Mp,
            ["player.maxMagic"]   = EffMaxMp,
            ["player.health"]     = _char.Hp,
            ["player.maxHealth"]  = EffMaxHp,
            ["player.enchant"]    = EffEnchant, ["player.rage"] = EffRage, ["player.fury"] = 1, ["player.invis"] = Stealthed ? 5 : 1,
            ["target.health"]     = target?.Hp ?? 0,
            ["target.baseHealth"] = target?.MaxHp ?? 0,
        };
    }

    // RTK magic-deflect roll (clif_parsemagic, clif.c:8910-8934): resist = target.Protection + the target's
    // Will advantage over the caster (in 10-point steps), then exponential decay (0.9^prot) turns that into a
    // fail chance. Only spells flagged SplCanFail roll this at all. `target.Protection` now comes from the
    // merged-in CTK `MobProtection` column (Content.cs) — previously always 0 for lack of a source column.
    // No mana is spent on a deflected cast (RTK returns before the Lua "cast" script — which is where mana is
    // actually debited — ever runs).
    private bool RollDeflect(Mob target)
    {
        int casterWill = _char.Will + Totals().will;
        int willDiff = Math.Max(0, target.Will - casterWill);
        int prot = Math.Max(0, target.Protection + (int)(willDiff / 10.0 + 0.5));
        int failChance = (int)(100 - Math.Pow(0.9, prot) * 100 + 0.5);
        return Random.Shared.Next(100) < failChance;
    }

    // Send the live TARGET-flavor line for a spell to its target. On a self-cast (target == caster) it prints
    // just before HandleCast's central "You cast <name>." (so you see flavor then cast line, like live NexusTK);
    // when cast on ANOTHER player they get their flavor line (falling back to a generic "<caster> casts <X> on
    // you." only if no live flavor is recorded). The caster themselves never gets flavor — only "You cast X".
    private void TellTarget(Session target, SpellDef sp)
    {
        // Wrapped at the DEFINITION rather than at each of the nine call sites — the NotifyGroup precedent
        // (Session.Chat.cs) — because every one of those sites reaches a peer and NONE of them is already
        // inside that peer's section: the whole file takes exactly one other peer monitor, the wedding's
        // WithStatePair. Rule 3 makes the self-cast branch the re-entrant no-op, since we are already under
        // our own monitor there.
        //
        // #29 rule 2 (Server/Session.State.cs). Rule 1 and the Lua gate: every caller is a Lua* primitive
        // reached from spell_verbs.lua through SpellContext on the CASTER's read-loop thread, so the gate is
        // held and the order is own monitor -> gate -> peer monitor, the one
        // Tests/SessionActorTests.cs's LuaGateAgainstAPeerMonitorCannotDeadlock pins. No world lock: the tick's
        // mob casts go to Session.ApplyMobSpell (World.cs:2412) with a Content.MobSpellDef and never reach
        // here, and DrainQueuedCasts runs inside Dispatch, on the handler thread (Session.cs:720).
        var flavor = Content.TargetTextFor(sp.Key);
        target.WithState(() =>
        {
            if (ReferenceEquals(target, this)) { if (flavor.Length > 0) SendMiniText(flavor); }
            else target.SendMiniText(flavor.Length > 0 ? flavor : $"{_char.Name} casts {sp.Name} on you.");
        });
    }

    /// <summary>Apply a spell's timed stat buff to THIS player — a buff someone else cast on us AND our own
    /// self-cast, which are the same thing once <see cref="LuaBuffTarget"/> has resolved who the target is.
    /// Refresh-not-stack: one sweep by spell key, then one entry per non-zero stat, all sharing a deadline (so
    /// a multi-stat row can't lose all but the last, which a per-stat "remove then add" would). Folds into
    /// Totals() → HUD/melee live; the caster owns the fx and the flavor line.
    ///
    /// <para><paramref name="category"/> is the RTK checkIfCast exclusivity slot, and passing it is what makes
    /// <see cref="HasStatusCategory"/> see the buff at all. A categorised buff lands even with no stat of its
    /// own — the slot IS the effect — while an uncategorised one with nothing to apply stays a no-op.</para></summary>
    internal void ReceiveTimedBuff(IReadOnlyList<string> stats, IReadOnlyList<string> amounts,
                                   int durMs, string key, string name, string category)
    {
        if (durMs <= 0) return;
        category ??= "";
        BuffRemoveAll(b => b.Key == key);   // refresh, don't stack — once, for every stat
        long expires = Environment.TickCount64 + durMs;
        int applied = 0;
        for (int i = 0; i < stats.Count; i++)
        {
            int amt = i < amounts.Count && double.TryParse(amounts[i], out var d) ? (int)Math.Floor(d) : 0;
            if (stats[i].Length == 0 || amt == 0) continue;
            BuffAdd(new ActiveBuff
            { Stat = stats[i], Amount = amt, Expires = expires, Key = key, Name = name, Category = category });
            applied++;
        }
        if (applied == 0 && category.Length > 0)
            BuffAdd(new ActiveBuff
            { Stat = "", Amount = 0, Expires = expires, Key = key, Name = name, Category = category });
        SendStats();
    }

    // Backstab/Flank stance timers (RTK player.backstab/player.flank, set true by these Warrior skills'
    // "recast" hook and cleared by "uncast" when the duration lapses — we just track expiry directly, same
    // pattern as Mob.FrozenUntil).
    // NEITHER IS A DAMAGE MULTIPLIER (2026-08-04) — both are TARGETING spells that extend which neighbouring
    // tile a swing can reach, at reduced damage. Both are read by Session.SwingTargets, which owns the rules:
    //   BackstabStance (lvl 15) -> "Enables to Attack from warrior's back"    -> REAR tile,  x0.5
    //   FlankStance    (lvl 20) -> "Enables to Attack to the Warrior's Sides" -> ONE side,   x0.5 (random)
    // NOT to be confused with the positional mechanic also nicknamed "backstab": striking a target whose
    // back is to the blow is x2 for anyone, always (Combat.IsBehindTarget) — a property of the TARGET's
    // facing, independent of these spells. A backstab-spell swing into a mob that is itself facing away
    // earns both. Combat.IsBackstabAngle/IsFlankAngle are retired; do not re-wire them.
    //
    // FOUR-WAY is the third of these, and Baekho's Cunning 4 is the only thing that grants it: the swing
    // reaches ALL FOUR adjacent tiles at once, with no side roll. Two independent archive sources pin it --
    // Poet Tutor SkaDemon's "Rage and Cunning" board post labels the tiers "Fury / Backstab / Flank / 4 Way
    // Attack / Super Sanctuary", and Dalsichvedin's 2004-05-05 damage chart gives a target count per tier of
    // 1/2/3/4/4. Those two agree with each other AND with the code above: Cunning 3 reaching 3 tiles is only
    // possible if Flank is ONE side (RTK's blind `rand`), which is what SwingTargets already does -- so the
    // count that finally distinguishes 3 from 4 is the evidence that Flank is not both sides. Cunning 5 keeps
    // it (the chart's second 4); its own "Super Sanctuary" label is the 85% deduction, not a targeting change.
    private long _backstabUntil, _flankUntil, _fourWayUntil;
    private bool BackstabStance => Environment.TickCount64 < _backstabUntil;
    private bool FlankStance    => Environment.TickCount64 < _flankUntil;
    private bool FourWayStance  => Environment.TickCount64 < _fourWayUntil;

    // Rage-tier timer (RTK player.rage — Wolf's/Tiger's/Dragon's Fury, Baekho's Rage): swingDamage.lua
    // multiplies the ENTIRE player swing by max(player.rage,1), so this is the single biggest melee
    // multiplier a Warrior/Rogue can stack (up to 5x at the level-99 tier). Expires back to the RTK
    // baseline of 1 (not 0) automatically once _rageUntil lapses — see EffRage.
    private long _rageUntil;
    private int  _rageAmount = 1;
    internal string _rageName = "";   // the arming spell's display name, for the buff box (else a bare "Fury")
    private int  EffRage => Environment.TickCount64 < _rageUntil ? _rageAmount : 1;

    // Damage-reduction "deduction" slot (RTK player.deduction — the sanctuary line + Baekho's Cunning): a
    // fractional MULTIPLIER on incoming damage. 1.0 = full damage, lower = less (sanctuary 0.5 = take half,
    // Cunning ramps to 0.6 = 40% off). Single-slot — RTK makes the sources mutually exclusive (checkIfCast),
    // so last cast owns it; expires back to 1.0 automatically. Applied in Session.ApplyMobHit.
    // Set true by a handler that already sent the caster's outcome line (teleport arrival, inspect result), so
    // HandleCast skips the generic "You cast <name>." for it (e.g. Gateway shows only "You have arrived...").
    private bool _castNarrated;

    // Damage-reduction (RTK "deduction": incoming damage x mult). TWO INDEPENDENT sources that do NOT stack and
    // are NOT additive (classic NTK): the Sanctuary line and Baekho's Cunning. Precedence: while Sanctuary is
    // active it OVERRIDES Cunning entirely — you get its 0.5 even when that's WORSE than a high Cunning tier
    // (this is the classic "casting Sanctuary downgrades a high-Cunning rogue"). When Sanctuary lapses, the
    // still-running Cunning value re-asserts on its own timer. Each clamps to [0,1] (can only ever reduce).
    // See nexustk-495-curse-status-system + the Baekho's Cunning tutor chart.
    private double _sancDeduct = 1.0;    private long _sancDeductUntil;   private string _sancDeductName = "";
    private double _cunningDeduct = 1.0; private long _cunningDeductUntil;
    internal bool SancDeductActive    => Environment.TickCount64 < _sancDeductUntil;
    internal bool CunningDeductActive => Environment.TickCount64 < _cunningDeductUntil;
    // Sanctuary overrides Cunning while active; else the still-active Cunning value; else no reduction.
    internal double EffDeduction => SancDeductActive ? _sancDeduct : CunningDeductActive ? _cunningDeduct : 1.0;
    internal long   SancDeductUntil    => _sancDeductUntil;
    internal long   CunningDeductUntil => _cunningDeductUntil;
    internal string SancDeductName     => _sancDeductName;

    // Sanctuary-line deduction (sanctuary/protect_soul/magic_shield/guard_life) cast on us/self — overrides any
    // active Cunning for its whole duration. `mult` is the incoming-damage multiplier (0.5 = take half).
    internal void ApplySanctuaryDeduction(double mult, int durMs, string name)
    {
        if (durMs <= 0) return;
        _sancDeduct = Math.Clamp(mult, 0.0, 1.0);
        _sancDeductUntil = Environment.TickCount64 + durMs;
        _sancDeductName = name ?? "";
        SendStats();
    }
    // Baekho's Cunning tier deduction — its own slot, suppressed while Sanctuary is up, re-asserts when it lapses.
    internal void ApplyCunningDeduction(double mult, int durMs)
    {
        if (durMs <= 0) return;
        _cunningDeduct = Math.Clamp(mult, 0.0, 1.0);
        _cunningDeductUntil = Environment.TickCount64 + durMs;
        SendStats();
    }

    // Chung Ryong's Rage — the Warrior Chung Ryong subpath's incremental fury (Warrior Tutor SoulHunter's
    // board post, live-server truth; the boards outrank RTK). ONE spell key, recast every 120s to climb
    // tier 1→6: each tier costs more mana, multiplies the whole swing harder (6/9/12/18/27/81 — the era-
    // matched 2001-02 values, NOT the later 8/14/…/81 rebalance), hardens your armour, and drains a slice of
    // vita when it finally WEARS OUT (renewing before that — the reward for maintaining — costs no vita).
    // Ac is an AC DELTA (more AC = more damage taken), so a tier's hardening is NEGATIVE.
    // Mult/Mana/Ac here are a MIRROR of CHUNG_RYONG_RAGE in spell_verbs.lua, which is what actually drives a
    // cast; only VitaLostPct is read from this table (by ChungRyongRageWearOff, below). Keep the two in step.
    private readonly record struct CrRageTier(int Mult, int Mana, int Ac, double VitaLostPct);
    private static readonly CrRageTier[] ChungRyongRageTiers =
    {
        new(6,     2000,   0, 0.20),   // Rage 1
        new(9,     7200,   0, 0.20),   // Rage 2
        new(12,   16200,  -5, 0.20),   // Rage 3
        new(18,   28800, -15, 0.40),   // Rage 4
        new(27,   64800, -30, 0.60),   // Rage 5
        new(81,  145800, -50, 1.00),   // Rage 6 — leaves you at 1 vita/mana
    };
    // The recast interval is the generic aether gate (spell_effects aether=120000). The RUN is a fixed 938s
    // armed by the first cast and never re-armed (RTK sets setDuration in its first-cast branch only), so the
    // 120s gate buys seven casts inside one run — tier 6 at t=600s, held to t=938s, then the wear-out fires
    // and takes its vita. Both survive a relog (Session.TimedEffects persists RageUntil AND CrRageTier, so the
    // drain can't be dodged by logging out). _crRageTier==0 means not up. Mirror of CR_RAGE_DURATION_MS in
    // spell_verbs.lua, which is what actually feeds a cast.
    private const int CrRageDurationMs = 938_000;
    private const string CrRageAcKey = "chung_ryongs_rage_ac";
    private int _crRageTier;

    // Called from RegenTick when the Chung Ryong fury lapses without being renewed: apply the tier's vita
    // price (tier 6 leaves you at 1 vita/mana), drop the AC buff, and reset the tier. Floors at 1 — the fury
    // wearing off guts you but never outright kills.
    private void ChungRyongRageWearOff()
    {
        var tier = ChungRyongRageTiers[_crRageTier - 1];
        _crRageTier = 0;
        BuffRemoveAll(b => b.Key == CrRageAcKey);
        if (tier.VitaLostPct >= 1.0) { _char.Hp = 1; _char.Mp = 1; }
        else _char.Hp = (uint)Math.Max(1, (int)(_char.Hp * (1.0 - tier.VitaLostPct)));
        SendMiniText("Chung Ryong's rage leaves you drained.");
        SendStats();
    }

    // Stealth timer (Rogue Invisible/Spirit's Form/Life's Cloak/Glass Form): a flat 5x damage multiplier on
    // the swing that follows (tswolf 8/2001, era-matched to 4.95: "Invisible increases attack by 5 times";
    // RTK's Lua says 9x but that's a later rebalance and RTK isn't authoritative), meant as a one-shot
    // sneak-attack burst — landing that hit strips the stealth immediately
    // (RTK `block:removeDuras(invis)` after a nonzero hit), so Session.PlayerSwingDamage clears _stealthUntil
    // itself once it applies the multiplier. ONLY the damage multiplier is ported here — real RTK's
    // PC_INVIS also hides the player's sprite from other clients (clif.c), which isn't touched by this pass.
    private long _stealthUntil;
    private bool Stealthed => Environment.TickCount64 < _stealthUntil;
    // The faded (form-5) sprite is a peer-visible state with no server entity of its own — same shape as morph.
    // _stealthShown tracks whether we're currently drawn faded so the revert fires exactly once, whether stealth
    // ends by a hit (redrawn inline), a timer lapse, or a Cleanse/flush (both caught by World.Tick's IsStealthExpired).
    private bool _stealthShown;
    private string _stealthName = "Invisible";   // the specific stealth spell cast (Invisible/Spirit's Form/…) — shown in the buff box
    /// <summary>Read by World.Tick to fire the one-time revert when stealth ends without an inline redraw.</summary>
    public bool IsStealthExpired => _stealthShown && !Stealthed;
    /// <summary>Restore the normal look after stealth lapses (World.Tick / the on-hit drop path).</summary>
    public void RevertStealth()
    {
        using var _ = EnterState();   // #29: World.Tick calls this on the stealth timer lapsing
        if (!_stealthShown) return;
        _stealthShown = false;
        RefreshAppearance();
    }

    /// <summary>Fully drop Invisible — clears BOTH the timer (so the 5x sneak multiplier stops) and the faded
    /// look. Overt actions break stealth: landing a sneak hit (PlayerSwingDamage) and grabbing floor loot
    /// (HandlePickup). No-op if not stealthed.</summary>
    public void BreakStealth() { if (_stealthUntil == 0 && !_stealthShown) return; _stealthUntil = 0; RevertStealth(); }

    // Enchant tier (RTK player.enchant — see Content.EnchantFor): unlike rage, swingDamage.lua multiplies
    // ONLY the raw weapon-swing term (s/2) by this, not the whole swing (Session.PlayerSwingDamage). Baseline
    // is 1 (not 0), same as EffRage.
    //
    // NOT A TIMER — IT IS BOUND TO THE WEAPON. The warrior tutor spell list is unanimous across all five
    // tiers (Enchant / Infuse / Ingress / Viper's Venom / Dragon's Flame): "Duration = until the weapon is
    // taken off or person log off". RTK's own scripts agree structurally — ingress.lua carries cast/recast/
    // uncast hooks and no duration at all, which is exactly why spell_effects.csv's durationMs column is
    // blank on every enchant row (there was never a duration to extract).
    //
    // THIS USED TO BE A `_enchantUntil` TICK DEADLINE, and that blank column was the bug: stance_enchant's
    // `ctx.durationMs > 0 and ctx.durationMs or 60000` fallback silently gave every enchant SIXTY SECONDS,
    // while the fury cast in the same breath ran its real 625000. Ingress is the single largest term in a
    // warrior's swing (x3 on s/2 — a level-99 Chaos blade swing drops from ~1984 raw to ~784 without it),
    // so it read as "my damage randomly falls off a cliff" with nothing on screen to explain it. Do NOT
    // reintroduce a duration here; if an enchant ever needs one it belongs in the CSV, not in a fallback.
    //
    // Dropped by DropWeaponEnchant on any weapon-slot change (Session.Items.DropEnchantIfWeapon: unequipped,
    // swapped, or broken) and by ClearAllTimedEffects. The "or person log off" half needs no code — this is
    // session state and Session.TimedEffects deliberately does not persist it (unlike RageUntil).
    // Deliberately NOT surfaced in the buff box (Session.Dialog): every line there is "Name Ns" and this has
    // no countdown to show.
    private double _enchantAmount = 1;
    private double EffEnchant => _enchantAmount;

    /// <summary>Drop the weapon enchant, with RTK's own uncast line (shared verbatim by every alignment
    /// variant of enchant.lua / infuse.lua / ingress.lua). No-op when no enchant is running, so the callers
    /// can fire it on every weapon-slot change without gating first.</summary>
    internal void DropWeaponEnchant()
    {
        if (_enchantAmount <= 1) return;
        _enchantAmount = 1;
        SendMiniText("The glimmer subsides into a throb and then vanishes.");
        SendStats();
    }

    // RTK "morphs" duration group (see Content.MorphSpells/MorphDispatchFor) — purely cosmetic disguise.
    // CORRECTED 2026-07-26: an earlier pass concluded self-view was client-engine blocked, based on 0x33's
    // handler (0x44fef0) always forcing the player sprite archive (0x4f2a84) via 0x463380. That's real, but
    // it only proves 0x33 can't draw a monster — it does NOT apply to self. Fresh disassembly of the shared
    // entity factory (0x44d7d0) shows a THIRD path: when a packet's entity id equals the client's own self
    // id (checked via `cmp edi,[[ebx+0x40c]+0x108]`), the factory never touches the peer/monster ctor OR
    // 0x33's hardcoded-archive draw call at all — it reroutes to 0x461e30, which writes the look descriptor
    // straight into the self entity's OWN struct fields (self+0x178/0x17c/0x180), the identical fields a
    // real monster entity carries. That path is reachable the exact same way the peer workaround already
    // reaches it: send a real 0x07 Monster.epf creature-spawn, just addressed to the caster's own id instead
    // of a peer's. So Session.ShowPlayer — the single choke point every peer re-sync path already funnels
    // through — now updates the CASTER's own view too (World.Broadcast with no `except`, including a
    // self-call). The caster's own id never enters World's mob list, so click/party/trade resolution
    // (HandleClickInfo checks MobById before Online.ById) is unaffected either way.
    private ushort _morphLook;     // 0 = not morphed; else the Monster.tbl index peers see us as (0x8000|this)
    private byte   _morphColor;
    private long   _morphUntil;
    private string _morphKey = "";
    public bool IsMorphed => _morphLook != 0 && Environment.TickCount64 < _morphUntil;
    /// <summary>Read by World.Tick (outside the lock) to know when to fire the revert broadcast.</summary>
    public bool IsMorphExpired => _morphLook != 0 && Environment.TickCount64 >= _morphUntil;

    /// <summary>Clears morph state and re-broadcasts our real human look to everyone, including ourselves.
    /// Called from the toggle-off cast above, and from World.Tick (via IsMorphExpired) when the duration
    /// lapses on its own.</summary>
    public void RevertMorph()
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (_morphLook == 0) return;
        BuffRemoveAll(b => b.Key == _morphKey);
        _morphLook = 0; _morphColor = 0; _morphUntil = 0; _morphKey = "";
        BroadcastFx(_char.Id, -1, 411);   // morph-exit "poof" (anim skipped, sound only) — RTK is silent on exit; ours plays 411 to the whole map
        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);   // force-clear the morphed entity (incl. its nameplate) on peers before restoring the real look
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        ShowPlayer(this);   // restore our own view too (same 0x07-self-id path we used to morph)
    }

    // These four read their graphic+sound from the same spell_effects.csv row every other spell uses, keyed by
    // the family's canonical identifier. They used to call Content.ZapEffect(201/120/119/125, class) directly
    // with RTK's `local spellFX` constants — which carried RTK's own alignment bugs: Lethal Strike and
    // Whirlwind are UNALIGNED spells that were passing a kwisin (201) and an ohaeng (125) constant
    // respectively. Going through the row fixes both and keeps one source of truth (see re/fill_spell_fx.py).
    private static readonly Dictionary<Content.SacrificeFamily, string> SacrificeKeys = new()
    {
        [Content.SacrificeFamily.LethalStrike]    = "lethal_strike_rogue",
        [Content.SacrificeFamily.DesperateAttack] = "desperate_attack_rogue",
        [Content.SacrificeFamily.Berserk]         = "berserk_warrior",
        [Content.SacrificeFamily.Whirlwind]       = "whirlwind_warrior",
        [Content.SacrificeFamily.FocusedBlow]     = "focused_blow_rogue",
        [Content.SacrificeFamily.Siege]           = "siege_warrior",
    };
    private static (int anim, int sound) SacrificeFx(Content.SacrificeFamily fam)
    {
        if (!SacrificeKeys.TryGetValue(fam, out var key)) return (-1, -1);
        var fx = Content.SpellByKey(key) is { } sp ? Content.FxFor(sp) : null;
        return fx is null ? (-1, -1) : (Content.EffectAnim(fx), Content.EffectSound(fx));
    }
    private static int SacrificeAnim(Content.SacrificeFamily fam) => SacrificeFx(fam).anim;
    private static int SacrificeSound(Content.SacrificeFamily fam) => SacrificeFx(fam).sound;

    // ROGUE OVERKILL — "backflow" in this codebase (2008-09-18, era-gated: see LuaBackflow). A killing Lethal
    // Strike or Desperate Attack refunds the damage it did not need to the caster, "divided equally between
    // vitality and mana" (Rogue tutor board, Amaroq/Destyn). So each pool gets HALF the overkill, not half
    // between them — the doc's own example is 100k damage on a 50k mob refunding 25k to each.
    //
    // TWO caps, both from that doc, and they are different rules:
    //   * amount — "a rogue can at max get 50% of their current stats back", current meaning at cast time;
    //   * ceiling — "never healed to more than the amount they had at the time of casting the attack. A rogue
    //     with 50/25 stats, 100/50 if fully healed, will never be able to heal themself past 50/25."
    // The ceiling is against the PRE-CAST pools, not the character's maximum, which is why EffMaxHp/EffMaxMp
    // are only the outer bound here. A wounded rogue cannot use a vita strike as a heal.
    //
    // ORDER MATTERS AND IS THE CALLER'S JOB: verbs.sacrifice charges the strike's own HP/MP cost BEFORE
    // calling this, because the doc's arithmetic only works that way round. LS takes 50% vita and overkill
    // refills 50%, so "a person has the potential to gain all of their current stats back"; DA zeroes mana
    // and overkill refills to "50% mana, nothing more". Refunding first and charging afterwards — which is
    // what this used to do — halves LS's refund and destroys DA's mana refund outright.
    private void ApplyBackflow(int overkill, uint preHealth, uint preMagic)
    {
        if (overkill < 1) return;
        int refund = (int)Math.Ceiling(overkill / 2.0);
        int hpCap  = (int)Math.Ceiling(preHealth / 2.0);
        int mpCap  = (int)Math.Ceiling(preMagic / 2.0);
        _char.Hp = Math.Min(Math.Min(EffMaxHp, preHealth), _char.Hp + (uint)Math.Min(refund, hpCap));
        _char.Mp = Math.Min(Math.Min(EffMaxMp, preMagic),  _char.Mp + (uint)Math.Min(refund, mpCap));
    }

    // WARRIOR OVERFLOW (2007-04-10, era-gated — see LuaOverflow). A vita strike that kills its target splashes
    // the damage it did not need onto the four orthogonally-adjacent tiles of THE TARGET's cell, not ours.
    //
    // The archive documents this twice and the two disagree, so which one we implement is a real decision:
    //
    //   Tutor FAQ  (Overflow Damage FAQ v1.6, Tynan)  each target takes (done - needed) * 0.2, NOT divided.
    //   Revision   (Yttribium/Ixeus, "Revised overflow formula", test-derived, explicitly revising the FAQ)
    //              each target takes ((done - needed) / targetCount) * 1.05 — split, with a 5% bonus.
    //
    // We implement the REVISION, for three reasons: it is later, it is the only one of the two that reports
    // actual measurements, and RTK's warrior/overflow.lua independently arrives at the same shape — which is
    // corroboration from a source that never read either post. The FAQ number is kept above so nobody has to
    // rediscover the conflict. (Both agree on everything else: the pattern, the exp, and 0 AC.)
    //
    // ONE HOP ONLY. RTK's Lua recurses — each splashed kill re-splashes from its own tile — and we ported that,
    // but the FAQ is explicit that "a target killed by overflow damage will not cause more overflow damage",
    // and the revision describes a single transfer to a fixed set. A chain-kill cleave is also a far bigger
    // thing than either source describes. The recursion is gone; do not reintroduce it from the RTK Lua.
    //
    // MOBS AND PLAYERS BOTH. "Does overflow damage work in PK? Yes, it does." (FAQ VI.) RTK's overflow.lua
    // splashes mobs only — its berserk.lua calls Overflow.Cast from the mob branch and not from its PC branch —
    // but the FAQ is a description of the live game and outranks the reimplementation, so peers on a PvP map
    // are splashed too, sharing the pool with the mobs rather than getting a second helping of it.
    private void ApplyOverflow(int baseDamage, int srcX, int srcY, SpellDef sp)
    {
        if (baseDamage < 1) return;
        // Splash FX. A sacrifice-family strike shows its FAMILY's effect, so all four alignment aliases splash
        // with the base spell's animation; Slash and Feral Berserk are in no family (they reach here via
        // TryArchetypeOverflow) and show their own row instead — without this they would splash in Berserk's
        // colours, since SacrificeFamilyFor returns null for them.
        var (ovAnim, ovSnd) = Content.SacrificeFamilyFor(sp) is { } fam
            ? SacrificeFx(fam)
            : Content.FxFor(sp) is { } ofx
                ? (Content.EffectAnim(ofx, sp.PathId), Content.EffectSound(ofx, sp.PathId))
                : (0, 0);
        int total = (int)Math.Ceiling(baseDamage * 1.05);
        var offsets = new (int dx, int dy)[] { (0, 1), (0, -1), (1, 0), (-1, 0) };
        bool pvp = Content.IsPvpMap(_char.Map);
        var mobs = new List<Mob>();
        var peers = new List<Session>();
        foreach (var (dx, dy) in offsets)
        {
            int cx = srcX + dx, cy = srcY + dy;
            if (cx < 0 || cy < 0) continue;
            var m = _world.MobAt(_char.Map, cx, cy);
            if (m is not null && m.Alive) { mobs.Add(m); continue; }
            if (!pvp) continue;
            var peer = _world.PeerAt(_char.Map, (ushort)cx, (ushort)cy);
            // EXCLUDING OURSELVES, WHICH IS NOT OPTIONAL. The struck tile is adjacent to us by construction,
            // so the caster is ALWAYS standing on one of these four cells — the FAQ draws exactly that picture
            // ("The Warrior himself is in one of the spots!"). Without this test every overflow would splash
            // its own caster for a share of a number derived from the caster's own vita.
            // Skipping the DEAD matters for more than tidiness: the share is `total / count`, so counting a
            // corpse would dilute every real victim's hit while ReceiveSpellDamage silently absorbed its
            // portion (it returns immediately for a dead target). Ghosts share tiles with the living in an
            // arena — see Session.Movement's PvpGhostHidden no-clip — so this is reachable, not theoretical.
            if (peer is not null && !ReferenceEquals(peer, this) && !peer.IsDead) peers.Add(peer);
        }
        // That occupied cell is also why a ground-level splash reaches at most THREE targets — the FAQ's
        // "unless the Warrior is stacked on an enemy, or Throw Axe is used, the maximum number of overflow
        // targets is three".
        int count = mobs.Count + peers.Count;
        if (count == 0) return;
        int share = (int)Math.Ceiling((double)total / count);
        foreach (var mob in mobs)
        {
            int net = Combat.ApplyArmor(share, mob.Ac, floor: -95);
            _world.TryDamage(_char.Map, mob, net, out bool died, _char.Id);
            BroadcastFx(mob.Id, ovAnim, ovSnd);
            ShowDamageResult(mob.Id, mob, died);
            // "Do enemies killed by overflow damage yield experience? Yes, they do, even when not previously
            // damaged." (FAQ V.) Their own overkill is DISCARDED — see the one-hop note above.
            if (died) AwardKillExp((uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp), _char.Map, mob.X, mob.Y, mob.Key);
        }
        foreach (var peer in peers)
        {
            // RAW share — ReceiveSpellDamage nets the recipient's own armor and Deduction at intake, so
            // pre-netting here would apply it twice. Each peer therefore takes the same share against its
            // own AC, exactly as each mob above does.
            BroadcastFx(peer._char.Id, ovAnim, ovSnd);
            peer.ReceiveSpellDamage(share, this, sp.Name);
        }
        Log.Info($"      {sp.Name} -> overflow {total} split over {mobs.Count} mob(s) + {peers.Count} peer(s)");
    }

    // Resolve a spell's PLAYER target: an explicit targetId (client-supplied entity id, same convention as
    // ResolveCastTarget's mob lookup) or else whoever's standing on the tile directly faced.
    private Session? ResolvePcCastTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var byId = _world.Online.ById(id);
            if (byId is not null) return byId;
        }
        var (fx, fy) = FrontTile();
        return _world.PeerAt(_char.Map, fx, fy);
    }

    // RTK's `player:flushDuration()` — strips every active timed spell effect in one shot, buffs and
    // debuffs alike. We don't yet track true player-targeted debuffs (only mob-targeted freezes), so this
    // clears every timed mechanic that exists PC-side: stat buffs, rage tiers, the stealth burst, and the
    // Warrior Backstab/Flank stances.
    private void FlushDurations()
    {
        BuffClear();
        _rageUntil = 0;            _crRageTier = 0;   // see ClearAllTimedEffects: a STRIPPED fury owes no drain
        _stealthUntil = 0;
        _backstabUntil = 0;
        _flankUntil = 0;
        _fourWayUntil = 0;
    }

    /// <summary>Strip EVERY running timed effect on this session in one shot — stat buffs, curse/ward status
    /// flags, rage, the Sanctuary/Cunning deduction timers, the enchant multiplier, the two warrior stances,
    /// and any stealth or morph disguise (reverted so the look snaps back). Does NOT render, push stats, or
    /// persist — each caller owns that: <see cref="DispelSelf"/> refreshes + saves, and <see cref="Die"/> is
    /// already redrawing as a ghost and saving. Shared so "@dispel" and death can't drift apart.</summary>
    private void ClearAllTimedEffects()
    {
        RevertMorph();      // restore the real look before we wipe the buff that named the disguise
        BreakStealth();     // clears _stealthUntil + the faded (form-5) sprite

        BuffClear();
        ClearStatusFlags();
        // _crRageTier goes with _rageUntil, and must: the tier is what RegenTick reads to charge Chung Ryong's
        // wear-out drain, so leaving it set behind a zeroed deadline fires the drain on the very next tick —
        // on a player who just DIED it ran ChungRyongRageWearOff's Max(1, ...) floor against Hp 0 and stood
        // the corpse back up at 1 HP. A fury that is stripped (death, @dispel, Cleanse) never wore out, so it
        // owes no vita; only lapsing on its own timer charges the price.
        _rageUntil = 0;            _rageAmount = 1;   _rageName = "";   _crRageTier = 0;
        _sancDeductUntil = 0;      _sancDeduct = 1.0; _sancDeductName = "";
        _cunningDeductUntil = 0;   _cunningDeduct = 1.0;
        _backstabUntil = 0;        _flankUntil = 0;   _fourWayUntil = 0;
        _enchantAmount = 1;        // weapon-bound, not timed — but @dispel/death still strip it
    }

    /// <summary>"@dispel" — strip EVERY timed effect on the caster, buff and debuff alike, and re-render.
    /// A superset of <see cref="FlushDurations"/> (which only covers stat buffs + rage + stealth + the two
    /// warrior stances): this also clears the curse/ward status flags, the Sanctuary/Cunning deduction
    /// timers and the enchant multiplier, and reverts a stealth or morph disguise so the look snaps back.
    /// Persists (MarkDirty), so a cleared effect can't be restored by a relog.</summary>
    internal void DispelSelf()
    {
        ClearAllTimedEffects();

        SendStats();          // drop the buff/debuff box + refresh any stat the buffs were bending
        RefreshAppearance();  // belt-and-braces redraw (RevertMorph/BreakStealth already did, if they fired)
        MarkDirty();
        Log.Info($"   -> DISPEL: {_char.Name} cleared all timed effects");
    }

    // One step from (x,y) in direction dir (0=N/1=E/2=S/3=W — same convention as FrontTile/_facing/Mob.Dir).
    private static (int x, int y) StepDir(int x, int y, int dir) => (dir & 3) switch
    {
        0 => (x, y - 1),
        1 => (x + 1, y),
        2 => (x, y + 1),
        _ => (x - 1, y),
    };

    // RTK seeSpotTraps (spotTraps.lua) is CLASS-BRANCHED, and both branches route through this one reveal:
    //   * class 1 (Warrior) — watchful_eye.lua family — reveals hidden AMBUSH tiles (RTK's MobSpawnNpc), the
    //     cave mob-spawn traps; "Spots Ambushes on ground and marks them off as Steel daggers."
    //   * class 2 (Rogue)   — dog/spot_traps.lua       — reveals the rogue combat-trap family (dart/snare/
    //     repeating/flash/spear/poison/death/sleep).
    // Neither reveals the cosmetic shiver echo (Session.TryMythicFallRoom), the tiger warp-traps, or a
    // bladestorm decoy. A non-warrior caster of either spell gets the rogue set (RTK only special-cases class 1).
    private Trap[] RevealableTrapsNear() =>
        _world.TrapsNear(_char.Map, _char.X, _char.Y, 15)
            .Where(t => CharBasePathId == 1
                ? t.Kind == "ambush"
                : t.Kind != "ambush" && t.Kind != "shiver" && t.Kind != "bladestorm")
            .ToArray();

    // Watchful Eye (warrior) + Spot Traps (dog/rogue), see Content.IsSpotTrapsSpell and RevealableTrapsNear:
    // marks each revealed trap within 15 tiles (RTK seeSpotTraps: distanceSquare(player, npc, 15)) with item 99
    // ("wooden sword" — RTK's own marker; its Lua comment calls it a "steel dagger" but the dropped id is 99
    // either way). The marker is drawn for the CASTER only, never World.DropItem, matching RTK's
    // addTrapSpotters/getTrapSpotters per-player visibility tagging — see MarkRevealedTraps.
    //
    // Lifetime is RTK's: a mark "will remain on screen for as long as you want", i.e. until you leave the map
    // (ForgetShownMobs) or the trap it marks GOES OFF, at which point the trap's own removeTrapItem takes the
    // sword with it (World -> ClearTrapMarker). There is still no player-facing bulk-clear — RTK's own
    // removeSpotTraps is a separate GM-style command.

    /// <summary>Called by World.CheckPlayerTrapTrigger when WE step on our own (or anyone's) bladestorm decoy.
    /// RTK's damage is ONE number — floor(health*0.5) + calculateDamage(35000 netted against our own armor,
    /// Combat.ApplyArmor mirrors calculateDamage's identical deduction formula) — applied both to us and to
    /// every mob the facing cone catches. Capped here to leave at least 1 HP on OURSELVES only: a trap
    /// tripped mid-walk has no death-flow of its own to hook (unlike a real melee/spell kill), same
    /// "self-cost, never actually lethal" precedent as CastSacrificeStrike. Returns the UNCAPPED value so the
    /// cone's mob targets take the real RTK number, not our clamped one.</summary>
    public int ApplyBladestormSelfDamage()
    {
        int effectiveAc = _char.Ac + Totals().armor;
        int raw = (int)(_char.Hp * 0.5) + Combat.ApplyArmor(35000, effectiveAc, floor: -80);
        int applied = Math.Min(raw, (int)_char.Hp - 1);
        if (applied > 0) _char.Hp -= (uint)applied;
        SendStats();
        SendMiniText("AIEE~! A trap goes off right beneath you!");
        return raw;
    }

    /// <summary>Called by World.CheckPlayerTrapTrigger when we step on a "shiver" fall-echo (see
    /// Session.TryMythicFallRoom): pure flavor, no effect — RTK's WarpTrapShiverNpc, unified onto every mythic
    /// fall cave. One-shot; the caller has already removed the trap.</summary>
    public void FeelShiver() => SendMiniText("You feel a sudden shiver.");

    /// <summary>Called by World.CheckPlayerTrapTrigger when we step on one of Sute's Cave's hidden cold tiles
    /// (see <see cref="SuteAi.FrigidTrapKind"/>): a flat hit and its own line, "A blast of frigid cold hits
    /// you." The caller has already removed the tile and placed a replacement elsewhere in the room.
    ///
    /// <para>Unlike the bladestorm self-hit above, this one CAN kill. Bladestorm is your own trap and is
    /// capped to leave 1 HP for that reason; this is the dungeon's hazard, and a room advertised as having
    /// "magical traps" that can never actually finish anyone is not a hazard. Routed through
    /// <see cref="ReceiveEnvironmentDamage"/> so it takes the deduction reduction and reaches
    /// <see cref="Die"/> exactly like a creature's spell would.</para></summary>
    public void TakeFrigidBlast() => ReceiveEnvironmentDamage(SuteAi.FrigidDamage, SuteAi.FrigidText);

    /// <summary>Called by World.FireAmbushLocked when we step on a cave ambush tile: the burst mobs are already
    /// spawned around us (our post-step SyncMobs renders them); this is just the "Rabbits ambush you!" style
    /// status line. See Content.AmbushMapDef.</summary>
    public void ShowAmbushText(string msg) => SendMiniText(msg);

    // Gateway destinations are data-driven (game-data/GatewayGates.csv -> Content.GatewayRegions): region
    // -> the kingdom's city map + the four gate spawn boxes. Casting Gateway warps you to a RANDOM tile inside
    // the box for the gate you answered (N/E/S/W), on the region's city map. Coords are 1:1 with RTK
    // (gateway.lua); only the four playable kingdoms (regions 0-3) have gates. Hot-reloads via @reload.

    // Gateway: teleport to a gate of the caster's kingdom. The N/E/S/W answer to the spell's question picks the
    // gate; the region (Content.RegionOf) picks the city. Faithful to gateway.lua incl. its guards (dead can't
    // cast, warp-locked maps say "It doesn't work here", non-kingdom maps "Can't find any gates!") and the
    // per-gate random landing spread. No mana cost — RTK's gateway only calls canCast (a state check), not a
    // mana debit. On success we re-run the full map-entry sequence via EnterMap so the client redraws.
    // A virtual "npc" purely for the propose/marriage dialog headers (never spawned or looked up). Distinct
    // sentinel from F1 (0xFFFFFFFF) / subpath-chat (0xFFFFFFFE) / Trade (0xFFFFFFFD) / WorldMap (0xFFFFFFFC).
    private static readonly Mob MarriageVirtualNpc = new(0xFFFFFFFB, 0, 0, 0, "Cupid", 1);

    private async Task RunProposeAsync(SpellDef sp)
    {
        string? name = await DlgInput(MarriageVirtualNpc, "What is the name of your beloved?");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, _char.Name, StringComparison.OrdinalIgnoreCase))
        { SendMiniText("You can't marry yourself."); return; }

        var target = ResolveOnlinePlayer(name, "Player is not valid or not online.", RefuseChannel.MiniText);
        if (target is null) return;
        // RTK checks the beloved is physically nearby (getObjectsInArea); same-map is our practical proxy
        // for "nearby", matching the gate Trade already uses for its own "must be present" check.
        if (target.CharMap != CharMap)
        { SendMiniText("Your beloved must be near you when you make this commitment."); return; }
        if (target.HasLegend("engaged") || target.HasLegend("married"))
        { SendMiniText($"{target.Snapshot().Name} is already committed to someone else!"); return; }
        if (target.CountItem("engagement_ring") < 1)
        { SendMiniText("You have not given them an engagement ring yet."); return; }

        ForgetOneSpell(sp.Id);
        _ = target.PromptProposal(this);
    }

    /// <summary>Runs on the PROPOSEE's own session (called cross-session — same pattern as Trade/Party):
    /// shows the accept/decline prompt and, on accept, engages both parties.</summary>
    internal async Task PromptProposal(Session proposer)
    {
        int choice = await DlgMenu(MarriageVirtualNpc,
            $"{proposer.Snapshot().Name} proposes marriage. Do you accept?",
            new[] { "Yes! I am madly in love.", "I must decline." });

        string me = Snapshot().Name, them = proposer.Snapshot().Name;
        if (choice == 1)
        {
            AddLegend($"Engaged to {them} ({Character.GameDate})", "engaged", 6, 1);
            proposer.AddLegend($"Engaged to {me} ({Character.GameDate})", "engaged", 6, 1);
            TakeItem("engagement_ring", 1);
            long timer = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200;   // RTK: 3-day cool-down
            SetEngaged(them, isProposee: true, timer);
            proposer.SetEngaged(me, isProposee: false, timer);
            proposer.SendMiniText($"{me} accepts!");
            SendMiniText($"You are now engaged to {them}!");
        }
        else
        {
            proposer.SendMiniText($"{me} regretably must decline.");
            SendMiniText("What but love can last forever?");
        }
    }

    /// <summary>Runs the wedding ceremony against this player's current fiancé (RTK
    /// <c>ChapelNpc.marriageprompt</c>): asks the FIANCÉ (the original proposer) for their "I do", then
    /// marries both on accept. Returns the message the Chapel should show, or null if the outcome was
    /// already messaged directly (a decline).</summary>
    internal async Task<string?> RunMarriageCeremony()
    {
        var fiance = _world.Online.FindPlayer(_char.Fiance);
        if (fiance is null) return "Both parties must be present for the ceremony to commence";
        if (fiance.HasLegend("married")) return "The person is already married.";

        string me = _char.Name, them = fiance.Snapshot().Name;
        int choice = await fiance.DlgMenu(MarriageVirtualNpc,
            $"Do you, {them} take {me} as your partner?",
            new[] { "I do. (you will lose much xp if you divorce)", "I don't." });

        if (choice != 1)
        {
            SendMiniText($"{them} regretably must decline.");
            fiance.SendMiniText("What but love can last forever?");
            return null;
        }

        string date = Character.GameDate;
        // Both characters change together — legend, engagement, spouse and the keepsake — and the whole
        // ceremony runs on the FIANCÉ's thread (their dialog reply is what resumed us). One critical section
        // across the pair, in the usual rank order, so neither half can be saved or read mid-wedding (#29).
        var bride = this;
        WithStatePair(bride, fiance, () =>
        {
            bride.AddLegend($"Married to {them} ({date})", "married", 6, 1);
            fiance.AddLegend($"Married to {me} ({date})", "married", 6, 1);
            bride.RemoveLegend("engaged"); fiance.RemoveLegend("engaged");
            bride.ClearEngagement(); fiance.ClearEngagement();
            bride.SetSpouse(them); fiance.SetSpouse(me);
            bride.GiveRewardItem("love", 1); fiance.GiveRewardItem("love", 1);
        });
        SendMiniText("I now pronounce you (married)");
        fiance.SendMiniText("I now pronounce you (married)");
        return "Congratulations! You are both now married.";
    }

    /// <summary>Which <c>Inns.csv</c> group this character returns to (RTK <c>Player.returnFunc</c>, which
    /// reads <c>registry["home"]</c> before falling through to <c>returnToInn</c>'s country switch).
    ///
    /// <para>A BOUND HOME WINS OVER YOUR NATION. Talking to an outlying town's mayor sets
    /// <see cref="HomeReg"/> and from then on Return puts you in HIS tavern no matter which kingdom you
    /// belong to — that is the whole point of the option, and RTK checks it first for the same reason. The
    /// mayors are the only writers; moving kingdom clears it back to 0 (see <see cref="SetNation"/>), which
    /// is also what RTK does.</para>
    ///
    /// <para>Otherwise it is your nation's tavern set. Neutral is NOT a missing case — the wilderness is
    /// where neutrals live, so it has its own group (a clearing by Rotah, RTK <c>country == 0</c>). Only the
    /// nations with no tavern set of their own (Shilla/Jinhan/Paekjae/Kaya, none of them reachable in this
    /// era) fall back, and they fall back to Kugnae's.</para></summary>
    private string HomeGroup() => QuestCounter(HomeReg) switch
    {
        HomeSanhae  => "Sanhae",
        HomeHausson => "Hausson",
        _ => _char.Nation switch
        {
            0 => "Wilderness",   // Neutral — RTK's `country == 0` clearing, not a tavern at all
            2 => "Buya",
            3 => "Nagnang",
            _ => "Kugnae",       // Kugnae + any nation without its own tavern set
        },
    };

    /// <summary>The bound-home registry key and its values, RTK's <c>registry["home"]</c> verbatim: 0 = none
    /// (use your nation), 10 = Sanhae, 11 = Hausson. RTK also defines 1 = clan hall and 2 = subpath hall;
    /// neither exists on this server, so neither is listed — an unrecognised value falls through to the
    /// nation set rather than stranding the player, which is why the switch above is a `_` default.
    /// <para>Lives in the int quest registry (<c>Character.Quests</c>) exactly as it does in RTK, so it
    /// persists and hot-reloads with no schema change. The Lua side reads/writes it by the same name.</para></summary>
    internal const string HomeReg     = "home";
    internal const int    HomeNone    = 0;
    internal const int    HomeSanhae  = 10;
    internal const int    HomeHausson = 11;

    // RTK Player.returnToInn (player.lua:4607): "home" for Return / yellow_scroll / qui_hyang is a RANDOM
    // tavern in your set (each has a bed to wake up in), NOT the nation's home-city interior — that's
    // CharacterFactory.HomeCityFor, which stays the fresh-character spawn + Silver-Thread revive point (it
    // used to double as the return target, which is why Return dumped you at Jadespear). The tavern lists
    // and their (4,5)/(4,6) arrival tiles are verbatim from RTK, data-driven via game-data/Inns.csv ->
    // Content.Inns, and hot-reload with @reload; which GROUP a given player uses is HomeGroup above.
    private void ReturnToInn()
    {
        var inns = Content.Inns.GetValueOrDefault(HomeGroup());
        if (inns is { Count: > 0 })
        {
            var pick = inns[Random.Shared.Next(inns.Count)];
            // A box, not a tile: every tavern row is a 1x1 box, the wilderness clearing is 4x4 (RTK
            // `warp(1002, random(206,209), random(139,142))` — no bed out there to wake up in).
            ushort px = (ushort)Random.Shared.Next(pick.X, pick.X2 + 1);
            ushort py = (ushort)Random.Shared.Next(pick.Y, pick.Y2 + 1);
            if (Content.TryMap(pick.Map, out var hm)) { EnterMap(hm.Id, hm.Xs, hm.Ys, px, py, hm.Name); return; }
        }

        // Safety net: if a nation's tavern map isn't loaded, fall back to the home city so the warp never
        // silently no-ops (all six 4.95 tavern maps are present, so this only guards future data drift).
        var (fm, fx, fy) = CharacterFactory.HomeCityFor(_char.Nation);
        if (Content.TryMap(fm, out var fmap)) EnterMap(fmap.Id, fmap.Xs, fmap.Ys, fx, fy, fmap.Name);
    }

    // Per-spell cooldowns ("aether"), keyed by spell identifier -> earliest next-cast tick (ms). Mirrors RTK's
    // player:setAether. Lightweight and session-local (resets on relog, like most timers here).
    private readonly Dictionary<string, long> _aether = new();
    private bool OnCooldown(string key, out int secondsLeft)
    {
        secondsLeft = 0;
        if (_aether.TryGetValue(key, out var until) && Environment.TickCount64 < until)
        { secondsLeft = (int)((until - Environment.TickCount64) / 1000); return true; }   // FLOOR, matching RTK — a sub-second remainder shows "0" (e.g. Invisible's 1s aether cast too fast)
        return false;
    }
    private void SetCooldown(string key, int ms) => _aether[key] = Environment.TickCount64 + ms;

    // The world mob a cast should affect: an explicit target id if the client sent one, else the mob on the
    // tile the caster faces (like melee). World mobs only — session-local debug dummies aren't spell targets.
    private Mob? ResolveCastTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var byId = _world.MobById(_char.Map, id);
            if (byId is not null) return byId;
        }
        var (fx, fy) = FrontTile();
        return _world.MobAt(_char.Map, fx, fy);
    }

    // Resolve a DAMAGE spell's target as either a mob OR a player (yourself or another) — the damage archetype
    // can hit both (PvP + self, e.g. sparking yourself in an arena). An explicit target id resolves DIRECTLY to a
    // mob or a player (no faced-tile fallback, so a client-targeted player is never hijacked by a mob you happen
    // to face); with no id we hit the faced tile, mob first then a peer. At most one of (mob, pc) is non-null.
    private (Mob? mob, Session? pc) ResolveDamageTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var m = _world.MobById(_char.Map, id);
            if (m is not null) return (m, null);
            return (null, _world.Online.ById(id));   // player id (incl. your own on a self-cast) — may be null
        }
        var (fx, fy) = FrontTile();
        var fm = _world.MobAt(_char.Map, fx, fy);
        if (fm is not null) return (fm, null);
        var peer = _world.PeerAt(_char.Map, fx, fy);
        if (peer is not null) return (null, peer);
        // Nothing aimed at. IN A PVP MAP that resolves to YOU — same "unaimed falls back to the caster" rule
        // every other self-cast rides (see ResolveTargetBuff's selfIfUnaimedInPvp), since the client gives a spell
        // no way to say "me". Zapping yourself is a legal thing to do where PvP is on.
        //
        // OFF a PvP map it stays null, so the cast fails silently rather than resolving to a target it would
        // then have to refuse out loud with "You can't attack that target." — nothing was there, and saying
        // nothing is the honest answer.
        //
        // FOOTGUN, stated once and left alone because it is the asked-for behaviour: in an arena, a zap thrown
        // the instant your target dies now lands on you.
        return (null, Content.IsPvpMap(_char.Map) ? this : null);
    }

    // PvP magic-deflect: the same RTK formula as RollDeflect(mob), but the defender is a PLAYER — resist scales
    // with the target's effective-Will advantage over the caster (PCs carry no innate Protection stat, so that
    // term is 0 and a caster whose Will >= the target's can never be deflected). No mana is spent on a deflect
    // (the caller rolls this BEFORE debiting). A self-cast never deflects (willDiff 0), so callers skip it there.
    private bool RollDeflectPvp(Session target)
    {
        int casterWill = _char.Will + Totals().will;
        int willDiff = Math.Max(0, target.LuaWill - casterWill);
        int prot = Math.Max(0, (int)(willDiff / 10.0 + 0.5));
        int failChance = (int)(100 - Math.Pow(0.9, prot) * 100 + 0.5);
        return Random.Shared.Next(100) < failChance;
    }

    // Apply a damage spell to a PC target — the PvP / self path. Self-cast is allowed anywhere (it only hurts
    // you); hitting ANOTHER player requires a PvP map (Content.IsPvpMap, RTK canPK approximation). Spends `mana`
    // here (pass 0 if the caller already spent it, e.g. the per-spell `damage` primitive). Returns false with a
    // notice (mana NOT spent) if disallowed. Rolls the PvP magic-deflect (SplCanFail spells only) before the
    // debit — a deflected cast spends no mana but still "happened" (returns true), matching the mob path.
    private bool HitPlayerWithSpell(Session pc, int amt, int mana, SpellDef sp) =>
        HitPlayerWithSpell(pc, amt, mana, sp, out _);

    /// <param name="landed">Damage actually applied, uncapped by the victim's remaining HP — 0 if the cast was
    /// refused or deflected. Only the vita strikes read it, to find their overkill; see LuaSacApply.</param>
    private bool HitPlayerWithSpell(Session pc, int amt, int mana, SpellDef sp, out int landed)
    {
        landed = 0;
        bool isSelf = ReferenceEquals(pc, this);
        // The PvP-map gate covers YOURSELF too. It used to exempt a self-cast on the reasoning that it only
        // hurts you — but "only hurts you" isn't true of a game with a death penalty and a corpse run, and it
        // meant a mistyped target let you kill yourself in the middle of a town. A hostile spell aimed at a
        // player is a hostile spell aimed at a player; where PvP is off, it doesn't land on anyone.
        if (!Content.IsPvpMap(_char.Map)) { SendMiniText("You can't attack that target."); return false; }
        if (mana > 0)
        {
            if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
            _char.Mp -= (uint)mana;   // spent BEFORE the deflect roll — a resisted spell was still cast
        }
        if (!isSelf && sp.CanFail && RollDeflectPvp(pc)) { SendStats(); SendMiniText("Your magic has been deflected."); return true; }
        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(pc._char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        landed = pc.ReceiveSpellDamage(amt, this, sp.Name);
        SendStats();
        Log.Info($"      {sp.Name} -> player {pc._char.Id} '{pc._char.Name}' for {amt} (pvp{(isSelf ? "/self" : "")})");
        return true;
    }

}
