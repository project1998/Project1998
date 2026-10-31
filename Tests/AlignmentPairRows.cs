using Server;

namespace Tests;

/// <summary>
/// Which spell rows are one spell's four alignment versions (unaligned, Kwi-Sin, Ming-Ken, Ohaeng), written here from
/// their sources rather than read from the server's table (<c>Content.AlignmentPairOf</c>), so the facts can judge that
/// table row by row (#417's delta re-check, D1). Nothing here reads an RTK file at run time.
/// </summary>
internal static class AlignmentPairRows
{
    /// <summary>RTK's swap lists (<c>rtklua/Accepted/Scripts/swapAlignment.lua</c>): the unaligned list, then Kwi-Sin's,
    /// Ming-Ken's and Ohaeng's, index for index; a swap gives each stem the class suffix of the player's base class (lines
    /// 531-537). Each row's comment is its index and the four lines it is copied from. Row 108's Ming-Ken stem is
    /// <c>mingken_companion</c>, as #417 decided: line 379 writes Kwi-Sin's word again, and RTK's own Caterpillar script
    /// names that version <c>mingken_companion_poet</c> (<c>rtklua/Accepted/Spells/poet/cotw_caterpillar.lua</c>:99).
    /// </summary>
    public static readonly string[][] RtkSwap =
    {
        new[] { "taunt", "anger", "tease", "mock" },                                                            // 1: 38 155 272 389
        new[] { "wolfs_fury", "souls_rage", "spirit_of_the_forest", "augmentation" },                           // 2: 39 156 273 390
        new[] { "bless", "sanctification", "tribal_gathering", "strength_of_purpose" },                         // 3: 40 157 274 391
        new[] { "fleshspeak", "spiritsong", "helping_hand", "heal_others" },                                    // 4: 41 158 275 392
        new[] { "backstab", "back_battle", "back_attack", "back_damage" },                                      // 5: 42 159 276 393
        new[] { "flank", "flank_battle", "flank_attack", "flank_damage" },                                      // 6: 43 160 277 394
        new[] { "tigers_fury", "filling_the_soul", "spirit_of_the_wild", "ohaengs_grace" },                     // 7: 44 161 278 395
        new[] { "enchant", "spiritual_aid", "oneness", "strengthen_weapon" },                                   // 8: 45 162 279 396
        new[] { "relief", "renew_essence", "lifesong", "sweet_waters" },                                        // 9: 46 163 280 397
        new[] { "potence", "spirit_arm", "touch_of_the_bear", "sharpen" },                                      // 10: 47 164 281 398
        new[] { "watchful_eye", "spirits_whisper", "creatures_guidance", "spot_unbalance" },                    // 11: 48 165 282 399
        new[] { "dragons_fury", "strength_of_ancestors", "spirit_of_the_dragon", "ohaengs_anger" },             // 12: 49 166 283 400
        new[] { "infuse", "tincture_of_the_unknown", "tigers_claw", "whetstone" },                              // 13: 50 167 284 401
        new[] { "ingress", "hand_of_darkness", "dragons_claw", "razors_edge" },                                 // 14: 51 168 285 402
        new[] { "berserk", "no_fear", "tigers_pounce", "winds_blast" },                                         // 15: 52 169 286 403
        new[] { "vigor", "touch_of_health", "balm", "restoration" },                                            // 16: 53 170 287 404
        new[] { "whirlwind", "deaths_angel", "natures_own", "bladedance" },                                     // 17: 54 171 288 405
        new[] { "hoche", "immunity", "forest_blessing", "magics_shield" },                                      // 18: 55 172 289 406
        new[] { "salvation", "redemption", "ointment_of_light", "return_of_life" },                             // 19: 56 173 290 407
        new[] { "assault", "deaths_challenge", "cold_snap", "volley" },                                         // 20: 57 174 291 408
        new[] { "hunangs_relief", "from_the_brink", "natures_slave", "lifes_essence" },                         // 21: 58 175 292 409
        new[] { "feral", "kwisin_cloak", "mingken_cloak", "ohaeng_cloak" },                                     // 22: 59 176 293 410
        new[] { "might", "spirit_strength", "inner_blessing", "temper" },                                       // 23: 60 177 294 411
        new[] { "judge", "spiritual_advisor", "natural_talent", "appraise" },                                   // 24: 61 178 295 412
        new[] { "singe", "embrace_of_the_void", "lightning", "natures_storm" },                                 // 25: 62 179 296 413
        new[] { "mend_wounds", "spiritual_cure", "natures_kiss", "cooling_touch" },                             // 26: 63 180 297 414
        new[] { "ignite", "spirit_strike", "wrath_of_nature", "thunderclap" },                                  // 27: 64 181 298 415
        new[] { "shadow_figure", "spirit_warrior", "natural_defense", "ohaengs_armor" },                        // 28: 65 182 299 416
        new[] { "rodent", "kwisin_disguise", "mingken_disguise", "ohaeng_disguise" },                           // 29: 66 183 300 417
        new[] { "spy", "spiritual_guide", "natures_handiwork", "judgement_day" },                               // 30: 67 184 301 418
        new[] { "gangrel", "kwisin_mask", "mingken_mask", "ohaeng_mask" },                                      // 31: 68 185 302 419
        new[] { "invisible", "spirits_form", "lifes_cloak", "glass_form" },                                     // 32: 69 186 303 420
        new[] { "ambush", "displacement", "waylay", "reflect" },                                                // 33: 70 187 304 421
        new[] { "beast", "kwisin_chameleon", "mingken_chameleon", "ohaeng_chameleon" },                         // 34: 71 188 305 422
        new[] { "amnesia", "forgetfulness", "free_spirit", "mislead" },                                         // 35: 72 189 306 423
        new[] { "recover", "spirits_embrace", "infuse_energy", "lifes_river" },                                 // 36: 73 190 307 424
        new[] { "desperate_attack", "the_voids_measure", "beastly_frenzy", "tilting_the_balance" },             // 37: 74 191 308 425
        new[] { "filch", "spirits_hand", "quick_fingers", "light_touch" },                                      // 38: 75 192 309 426
        new[] { "drain", "drink_of_souls", "parasite", "absorb" },                                              // 39: 76 193 310 427
        new[] { "seal_wounds", "spirit_caress", "heal_injury", "bandage" },                                     // 40: 77 194 311 428
        new[] { "lethal_strike", "afterlifes_embrace", "mingkens_judgement", "calculating_blow" },              // 41: 78 195 312 429
        new[] { "maros_remedy", "kwisin_maros_remedy", "mingken_maros_remedy", "ohaeng_maros_remedy" },         // 42: 79 196 313 430
        new[] { "masos_remedy", "kwisin_masos_remedy", "mingken_masos_remedy", "ohaeng_masos_remedy" },         // 43: 80 197 314 431
        new[] { "race", "spiritual_jump", "leap_of_faith", "transport" },                                       // 44: 81 198 315 432
        new[] { "refresh", "souls_awakening", "healing_waters", "fire_dance" },                                 // 45: 82 199 316 433
        new[] { "bladestorm", "swords_dance", "tigers_ambush", "cutting_edge" },                                // 46: 83 200 317 434
        new[] { "chance", "randomness", "natures_choice", "trial_by_fire" },                                    // 47: 84 201 318 435
        new[] { "daggers_remedy", "kwisin_daggers_remedy", "mingken_daggers_remedy", "ohaeng_daggers_remedy" }, // 48: 85 202 319 436
        new[] { "swift_recovery", "renew_energy", "loving_dew", "invigorate" },                                 // 49: 86 203 320 437
        new[] { "harden_armor", "thicken_skin", "shield_of_life", "elemental_armor" },                          // 50: 87 204 321 438
        new[] { "lay_hands", "spirits_smile", "lifes_water", "quicken" },                                       // 51: 88 205 322 439
        new[] { "erupt", "soulstorm", "avalanche", "deluge" },                                                  // 52: 89 206 323 440
        new[] { "valor", "strengthen", "bless_muscles", "power_burst" },                                        // 53: 90 207 324 441
        new[] { "ion", "soul_strike", "tree_dart", "sunstroke" },                                               // 54: 91 208 325 442
        new[] { "purge", "cure_illness", "restore_health", "remove_poison" },                                   // 55: 92 209 326 443
        new[] { "ion_charge", "crescendo", "flight_of_arrows", "blazing_sands" },                               // 56: 93 210 327 444
        new[] { "recover", "spirits_embrace", "infuse_energy", "lifes_river" },                                 // 57: 94 211 328 445
        new[] { "cure_paralysis", "release_binds", "return_movement", "free_movement" },                        // 58: 95 212 329 446
        new[] { "sanctuary", "protect_soul", "guard_life", "magic_shield" },                                    // 59: 96 213 330 447
        new[] { "invoke", "spirits_power", "life_force", "gather_magic" },                                      // 60: 97 214 331 448
        new[] { "heal", "ancestors_touch", "life_flow", "infuse_lifeforce" },                                   // 61: 98 215 332 449
        new[] { "impact", "soul_spike", "treefall", "shatter" },                                                // 62: 99 216 333 450
        new[] { "explode", "soul_chasm", "winters_vortex", "volcano" },                                         // 63: 100 217 334 451
        new[] { "remove_curse", "release_curse", "undo_evil", "restore_armor" },                                // 64: 101 218 335 452
        new[] { "call_lightning", "death_scream", "natures_wounding", "rain_of_fire" },                         // 65: 102 219 336 453
        new[] { "vex", "deaths_face", "unnatural_selection", "flaw" },                                          // 66: 103 220 337 454
        new[] { "venom", "spirits_leech", "snake_bite", "corruption" },                                         // 67: 104 221 338 455
        new[] { "blind", "dark_veil", "winters_shadow", "ice_glare" },                                          // 68: 105 222 339 456
        new[] { "paralyze", "spirit_leash", "cold_binds", "lockup" },                                           // 69: 106 223 340 457
        new[] { "electrocute", "eater_of_the_dead", "forests_discord", "shatter_storm" },                       // 70: 107 224 341 458
        new[] { "confuse", "curse_of_the_void", "despise_friend", "chaos_rain" },                               // 71: 108 225 342 459
        new[] { "sleep", "sweet_musings", "essence_of_poppies", "stillness" },                                  // 72: 109 226 343 460
        new[] { "stormstrike", "river_of_blood", "natural_disaster", "winds_of_disaster" },                     // 73: 110 227 344 461
        new[] { "doze", "voids_touch", "still_ethers", "still_waters" },                                        // 74: 111 228 345 462
        new[] { "tempest", "dance_macabre", "wilding", "chain_lightning" },                                     // 75: 112 229 346 463
        new[] { "rejuvenate", "still_embrace", "infuse_life", "healing_rain" },                                 // 76: 113 230 347 464
        new[] { "hellfire", "consume_soul", "flesh_eaters", "hurricane" },                                      // 77: 114 231 348 465
        new[] { "solace", "festival_of_souls", "natures_bounty", "ohaengs_blessing" },                          // 78: 115 232 349 466
        new[] { "bekyuns_heal", "death_denied", "one_with_life", "balanced_touch" },                            // 79: 116 233 350 467
        new[] { "inferno", "deaths_door", "natures_denial", "steel_storm" },                                    // 80: 117 234 351 468
        new[] { "vital_spark", "spirits_kiss", "spark_of_health", "water_of_nature" },                          // 81: 118 235 352 469
        new[] { "anoint", "brothers_of_spirit", "gathering_of_power", "natures_family" },                       // 82: 119 236 353 470
        new[] { "remove_veil", "clear_darkness", "restore_sight", "purge_darkness" },                           // 83: 120 237 354 471
        new[] { "endear", "possess_soul", "charm_life", "align_follower" },                                     // 84: 121 238 355 472
        new[] { "revitalize", "ancestors_embrace", "lifeforce", "lifespring" },                                 // 85: 122 239 356 473
        new[] { "inspiration", "draw_energy", "harness_power", "combine_focus" },                               // 86: 123 240 357 474
        new[] { "remedy", "brethren_of_spirits", "gathering_of_the_flock", "gathering_of_majesty" },            // 87: 124 241 358 475
        new[] { "atone", "restore_will", "raise_guard", "align_armor" },                                        // 88: 125 242 359 476
        new[] { "fortify", "ancestors_kiss", "fertile_soil", "waters_of_purity" },                              // 89: 126 243 360 477
        new[] { "second_sight", "hear_spirits", "improve_sight", "show_hidden" },                               // 90: 127 244 361 478
        new[] { "retribution", "spirit_puppet", "palm_of_life", "tornado" },                                    // 91: 128 245 362 479
        new[] { "barrier", "spirit_barrier", "life_barrier", "balance_barrier" },                               // 92: 129 246 363 480
        new[] { "inspire", "share_energy", "bestow_power", "release_focus" },                                   // 93: 130 247 364 481
        new[] { "scourge", "damage_will", "drop_guard", "unalign_armor" },                                      // 94: 131 248 365 482
        new[] { "earthquake", "tossing_the_bones", "natures_fury", "groundstrike" },                            // 95: 132 249 366 483
        new[] { "blockade_human", "block_entry", "distance_self", "protect_sides" },                            // 96: 133 250 367 484
        new[] { "heavens_kiss", "clan_of_souls", "healing_hand", "earths_embrace" },                            // 97: 134 251 368 485
        new[] { "harden_body", "deaths_guard", "lifes_protection", "body_of_alignment" },                       // 98: 135 252 369 486
        new[] { "dispell", "remove_magic", "return_natural", "restore_balance" },                               // 99: 136 253 370 487
        new[] { "flare", "dance_of_death", "breaking_of_the_world", "storm_crow" },                             // 100: 137 254 371 488
        new[] { "water_of_life", "breath_of_power", "healing_breath", "breath_of_life" },                       // 101: 138 255 372 489
        new[] { "resurrect", "return_spirit", "mingken_blessing", "death_undone" },                             // 102: 139 256 373 490
        new[] { "stream_of_life", "purity_of_spirit", "natures_abundance", "forge_of_life" },                   // 103: 140 257 374 491
        new[] { "bolster", "dark_armor", "life_armor", "armor_of_elements" },                                   // 104: 141 258 375 492
        new[] { "dishearten", "dark_fear", "break_will", "harshen_attack" },                                    // 105: 142 259 376 493
        new[] { "essence_of_life", "essence_of_spirit", "lifes_embrace", "earths_cradle" },                     // 106: 143 260 377 494
        new[] { "spark", "glimpse_of_the_void", "bolt", "natures_ire" },                                        // 107: 144 261 378 495
        new[] { "cotw_caterpillar", "kwisin_companion", "mingken_companion", "ohaeng_companion" },              // 108: 145 262 379 496
        new[] { "cotw_fluffy_dog", "kwisin_assistant", "mingken_assistant", "ohaeng_assistant" },               // 109: 146 263 380 497
        new[] { "cotw_panda_bear", "kwisin_protector", "mingken_protector", "ohaeng_protector" },               // 110: 147 264 381 498
        new[] { "cotw_wild_monkey", "kwisin_fighter", "mingken_fighter", "ohaeng_fighter" },                    // 111: 148 265 382 499
        new[] { "cotw_gorilla", "kwisin_warrior", "mingken_warrior", "ohaeng_warrior" },                        // 112: 149 266 383 500
        new[] { "cotw_wind_dancer", "kwisin_champion", "mingken_champion", "ohaeng_champion" },                 // 113: 150 267 384 501
        new[] { "cotw_wind_warrior", "kwisin_avatar", "mingken_avatar", "ohaeng_avatar" },                      // 114: 151 268 385 502
    };

    /// <summary>RTK's class suffixes, in its <c>classes</c> order: Warrior, Rogue, Mage, Poet.</summary>
    public static readonly string[] Suffixes = { "_warrior", "_rogue", "_mage", "_poet" };

    /// <summary>The content's aligned rows that RTK's lists do not name, other than the ten Sam san families
    /// (<see cref="SamSanAlignedRowTests.Families"/>). Siege's three are the live Nexus Atlas warrior page's names for Siege
    /// (<c>game-data/Sources.csv</c>, <c>atlas-warrior-spells</c>). The split traps are RTK's row 46 under the keys RTK's
    /// trap script gives them (<c>rtklua/Accepted/Spells/rogue/bladestorm_trap.lua</c>:1, 42, 83, 124).</summary>
    public static readonly string[][] Others =
    {
        new[] { "siege_warrior", "souls_freedom_warrior", "lifes_end_warrior", "winter_chill_warrior" },
        new[] { "set_bladestorm_trap", "set_swords_dance_trap", "set_tigers_ambush_trap", "set_cutting_edge_trap" },
    };

    /// <summary>Every row as full spell keys, unaligned first: each RTK row with each class suffix, then
    /// <see cref="Others"/>, then the ten Sam san families (their unaligned row, then their Kwi-Sin, Ming-Ken and Ohaeng
    /// rows).</summary>
    public static readonly IReadOnlyList<string[]> All =
        Suffixes.SelectMany(suffix => RtkSwap.Select(row => row.Select(stem => stem + suffix).ToArray()))
                .Concat(Others)
                .Concat(SamSanAlignedRowTests.Families.Select(f => f.Rows.Select(r => r.Key).Prepend(f.Base).ToArray()))
                .ToList();

    private static readonly Dictionary<string, string[]> ByKey = BuildIndex();

    private static Dictionary<string, string[]> BuildIndex()
    {
        var byKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in All)
            foreach (var key in row) byKey.TryAdd(key, row);   // rows 36 and 57 are the same row, written twice
        return byKey;
    }

    /// <summary>The row that names <paramref name="key"/>, or null.</summary>
    public static string[]? RowOf(string key) => ByKey.GetValueOrDefault(key);

    /// <summary><paramref name="sp"/>'s version in <paramref name="alignment"/>: itself for a spell of every alignment
    /// (alignment -1) or one no row names.</summary>
    public static string VersionIn(SpellDef sp, int alignment) =>
        sp.Alignment < 0 ? sp.Key : RowOf(sp.Key)?[alignment] ?? sp.Key;
}
