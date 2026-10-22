-- The dealer: lets Slay the Spire 2 play Balatro on its own screen.
--
-- StS2 draws the table; this module keeps Balatro where StS2 needs it and answers the table_*
-- requests (see protocol/PROTOCOL.md):
--   table_open        -> the current hand once one is dealt, or the shop while Balatro is in it.
--                        For Deal In, menus, the round summary and blind selection are clicked through.
--   table_preview     -> which poker hand a set of cards makes
--   table_discard     -> discards cards, then replies with the refreshed hand
--   table_play        -> plays cards, then replies with the score and a step-by-step scoring trace
--   table_sort        -> sorts the hand by rank or suit
--   table_reorder     -> puts the hand in the order given (the player dragged cards around)
--   table_shop_buy    -> buys a joker, then replies with the refreshed shop
--   table_shop_reroll -> rerolls the shop's jokers
--   table_shop_leave  -> leaves the shop; for Deal In the next hand is dealt in the background
--   table_link        -> (one-way) names the StS2 run ahead of time, so its hand is ready early
-- Cards are identified by Balatro's per-card sort_id.
--
-- StS2 links the table to one of its runs, in one of two modes:
--   * 'deal_in': another character's Deal In card plays single hands. The player's joker relics
--     are the jokers (StS2 is the source of truth), the shop only sells joker relics, and running
--     out of hands starts the blind over.
--   * 'jimbo': Jimbo's whole run is a Balatro run, driven step by step (bridge/jimbo.lua).
--     Balatro is the source of truth and the dealer never moves the run on by itself.
-- Either way a Balatro run belongs to one StS2 run: another run's link sets the current Balatro
-- run aside and continues (or starts) the linked one.

local cards = require('bridge.cards')

local dealer = {}

local ACTION_COOLDOWN = 0.15 -- seconds between automatic button presses (each button is pressed once)
local SETTLE_LIMIT = 0.5     -- a hand counts as dealt once Balatro is idle, or after this long regardless
local SHOP_SETTLE_LIMIT = 3  -- likewise a shop counts as stocked once Balatro is idle, or after this long
local PLAY_TIMEOUT = 20      -- give up on a played hand that never reached scoring
local RUN_START_TIMEOUT = 15 -- give up waiting for a run that was started and never appeared
local REPRESS_SECONDS = 3    -- a button still there this long after its press gets pressed again
local STALL_SECONDS = 10     -- a request waiting this long gets Balatro's situation written to the log
local RUN_ARCHIVE = 10       -- set-aside Balatro runs kept for other StS2 runs (other profiles, say)

local PACK_STATES = { 'TAROT_PACK', 'PLANET_PACK', 'SPECTRAL_PACK', 'STANDARD_PACK', 'BUFFOON_PACK' }

local send
local log
local autopilot = false  -- keep driving to a dealt hand even with no request waiting
local waiting = {}       -- { id, needs_redeal, redealt } requests waiting for a dealt hand or the shop
local shop_waiting = {}  -- { id, ready, bought } shop purchases waiting to complete
local playing            -- { id, since, chips_before, target, trace, scoring } until the hand scores
local recording          -- the `playing` entry while Balatro's evaluate_play runs
local leaving_shop       -- set by table_shop_leave until the shop's Next Round button is pressed
local next_action_at = 0
local last_pressed       -- { button, at }: a slow transition must not get a button pressed twice
local selecting_since
local shop_since

-- The StS2 run from the last request that named one:
-- { run, mode, owned = { key = true }, owned_list, pool, deck, stake, seed }
local link
local starting_run       -- { run, game, since } while a run started for `link` is being set up
local restarting_blind   -- set while a lost blind is being set up again

-- Set by bridge/jimbo.lua: extra snapshot fields, and its own update step.
dealer.extend_snapshot = nil
dealer.jimbo_update = nil
dealer.in_combat = function() return false end

local function now()
    return love.timer.getTime()
end
dealer.now = now

local loc = cards.loc

local function jimbo_mode()
    return link and link.mode == 'jimbo'
end
dealer.jimbo_mode = jimbo_mode

-- Snapshots -----------------------------------------------------------------------------------

local function blind_info(used)
    local blind = G.GAME.blind
    local conf = blind and blind.config and blind.config.blind
    if not conf or not conf.key then return nil end
    return {
        key = conf.key,
        name = loc({ type = 'name_text', set = 'Blind', key = conf.key }) or blind.name,
        chips = blind.chips,
        boss = blind.boss and true or false,
        colour = cards.hex(conf.boss_colour),
        sprite = conf.pos and cards.sprite('blind_chips', { x = 0, y = conf.pos.y }, used),
        text = (function()
            -- As Balatro's blind panel shows it: The Wheel's odds, The Mouth's chosen hand.
            local text = blind.get_loc_debuff_text and blind:get_loc_debuff_text() or blind.loc_debuff_text
            text = text and text:gsub('%s+', ' '):gsub('^ ', ''):gsub(' $', '')
            return text ~= '' and text or nil
        end)(),
        disabled = blind.disabled or nil,
        dollars = blind.dollars,
    }
end
dealer.blind_info = blind_info

-- Joker relics the shop can still offer: the pool minus jokers owned or already on sale.
local function shop_candidates()
    local taken = {}
    for _, card in ipairs(G.jokers.cards) do taken[card.config.center.key] = true end
    for _, card in ipairs(G.shop_jokers and G.shop_jokers.cards or {}) do taken[card.config.center.key] = true end
    local keys = {}
    for _, key in ipairs(link and link.pool or {}) do
        if not taken[key] and G.P_CENTERS[key] then keys[#keys + 1] = key end
    end
    return keys
end

-- The Wager (Jimbo's runs): a Boss Blind raised from the shop plays at RAISE times its chips,
-- and beating it adds a Rare Tag (bridge/jimbo.lua).
local RAISE = 1.5
dealer.RAISE = RAISE

local function raised()
    local wager = G.GAME and G.GAME.jimbo_raise
    return wager ~= nil and wager.ante == G.GAME.round_resets.ante
end
dealer.raised = raised

-- The blind that comes after the shop ('Small', 'Big' or 'Boss'), as Balatro's blind select works it out.
local function on_deck_kind()
    local states = G.GAME.round_resets.blind_states
    local function done(s) return s == 'Defeated' or s == 'Skipped' or s == 'Hide' end
    return not done(states.Small) and 'Small' or not done(states.Big) and 'Big' or 'Boss'
end
dealer.on_deck_kind = on_deck_kind

-- One of the ante's three blinds ('Small', 'Big' or 'Boss') as Balatro's blind select shows it.
local function blind_choice(kind, used)
    local reset = G.GAME.round_resets
    local blind = G.P_BLINDS[reset.blind_choices[kind]]
    if not blind then return nil end
    local text
    if blind.boss then
        local vars = blind.vars
        if blind.name == 'The Ox' then vars = { loc(G.GAME.current_round.most_played_poker_hand, 'poker_hands') } end
        text = cards.text('Blind', blind.key, vars)
        if text then text = text:gsub('\n', ' ') end
        -- The Wheel's text starts at its odds (" in 7 cards..."): Balatro puts the chance in front.
        if text and blind.name == 'The Wheel' then text = tostring(G.GAME.probabilities.normal) .. text end
    end
    return {
        key = blind.key,
        kind = kind,
        name = loc({ type = 'name_text', set = 'Blind', key = blind.key }) or blind.name,
        chips = get_blind_amount(reset.ante) * blind.mult * G.GAME.starting_params.ante_scaling
            * ((kind == 'Boss' and raised()) and RAISE or 1),
        raised = (kind == 'Boss' and raised()) or nil,
        boss = blind.boss and true or false,
        colour = cards.hex(blind.boss_colour),
        sprite = used and blind.pos and cards.sprite('blind_chips', { x = 0, y = blind.pos.y }, used) or nil,
        text = text,
        dollars = blind.dollars,
    }
end
dealer.blind_choice = blind_choice

-- The poker hand an Orbital Tag levels, chosen as Balatro's blind select chooses it: once per ante
-- and blind, from the 'orbital' stream, in blind order. StS2 offers the skip in the shop, before
-- Balatro has built the blind select, so the choice is made here; the blind select then finds it
-- made, and the run plays out the same.
local function choose_orbital_hand(blind_type)
    local ante = G.GAME.round_resets.ante
    G.GAME.orbital_choices = G.GAME.orbital_choices or {}
    G.GAME.orbital_choices[ante] = G.GAME.orbital_choices[ante] or {}
    if G.GAME.orbital_choices[ante][blind_type] then return end
    local hands = {}
    for key, hand in pairs(G.GAME.hands) do
        if hand.visible then hands[#hands + 1] = key end
    end
    G.GAME.orbital_choices[ante][blind_type] = pseudorandom_element(hands, pseudoseed('orbital'))
end

-- The blind that comes after the shop, as Balatro's blind select works it out.
local function next_blind_info(used)
    local on_deck = on_deck_kind()
    local info = blind_choice(on_deck, used)
    if not info then return nil end
    -- A Boss Blind can be raised (the Wager) from the second ante on, once a boss has been beaten.
    if jimbo_mode() and on_deck == 'Boss' and not raised() then info.raisable = G.GAME.round_resets.ante >= 2 end
    -- Small and Big Blinds can be skipped for a tag.
    local tag_key = on_deck ~= 'Boss' and G.GAME.round_resets.blind_tags and G.GAME.round_resets.blind_tags[on_deck]
    if tag_key and G.P_TAGS[tag_key] then
        local proto = G.P_TAGS[tag_key]
        if proto.name == 'Orbital Tag' then pcall(choose_orbital_hand, on_deck) end
        info.tag = {
            key = tag_key,
            name = loc({ type = 'name_text', set = 'Tag', key = tag_key }) or proto.name,
            text = cards.tag_text(tag_key, on_deck),
            sprite = used and cards.sprite('tags', proto.pos, used) or nil,
        }
    end
    return info
end
dealer.next_blind_info = next_blind_info

local function shop_info(used)
    if G.STATE ~= G.STATES.SHOP or not G.shop_jokers then return nil end
    local describe = jimbo_mode()
    local function list(area)
        local out = {}
        for i, card in ipairs(area and area.cards or {}) do
            out[i] = cards.card_info(card, used, describe)
            out[i].cost = card.cost
            -- Planets and the like can be bought and used on the spot.
            if card.ability.consumeable then out[i].usable = cards.usable(card, true) end
        end
        return out
    end
    return {
        cards = list(G.shop_jokers),
        vouchers = describe and list(G.shop_vouchers) or nil,
        boosters = describe and list(G.shop_booster) or nil,
        reroll_cost = G.GAME.current_round.reroll_cost,
        unseen = link and link.pool and not describe and #shop_candidates() or nil,
        cash_out = G.GAME.current_round.dollars,
        bankrupt_at = G.GAME.bankrupt_at,
        joker_slots = G.jokers.config.card_limit,
        next_blind = next_blind_info(used),
    }
end

local function snapshot()
    local game = G.GAME
    local used = {}
    local describe = jimbo_mode()
    local hand, jokers = {}, {}
    for i, card in ipairs(G.hand.cards) do hand[i] = cards.card_info(card, used, describe) end
    for i, card in ipairs(G.jokers.cards) do jokers[i] = cards.card_info(card, used, true) end
    local back = game.selected_back and game.selected_back.effect and game.selected_back.effect.center

    local snap = {
        shop = shop_info(used),
        hand = hand,
        jokers = jokers,
        hands_left = game.current_round.hands_left,
        discards_left = game.current_round.discards_left,
        select_limit = G.hand.config.highlighted_limit or 5,
        round_chips = game.chips,
        blind = blind_info(used),
        dollars = game.dollars,
        ante = game.round_resets.ante,
        round = game.round,
        deck_left = #G.deck.cards,
        deck_total = #G.playing_cards,
        back = back and cards.sprite('centers', back.pos, used),
        texture_scale = G.SETTINGS.GRAPHICS.texture_scaling,
    }
    if dealer.extend_snapshot then dealer.extend_snapshot(snap, used) end
    snap.atlases = cards.atlases(used)
    return snap
end
dealer.snapshot = snapshot

-- Hand helpers --------------------------------------------------------------------------------

local function cards_by_id(ids, area)
    local wanted, count = {}, 0
    for _, id in ipairs(type(ids) == 'table' and ids or {}) do
        wanted[tonumber(id)] = true
        count = count + 1
    end
    local found = {}
    for _, card in ipairs((area or G.hand).cards) do
        if wanted[card.sort_id] then found[#found + 1] = card end
    end
    return found, count
end
dealer.cards_by_id = cards_by_id

local function found_in(list, card)
    for _, other in ipairs(list) do
        if other == card then return true end
    end
    return false
end

local function require_selection(ids)
    if G.STATE ~= G.STATES.SELECTING_HAND or playing then error('no hand is dealt right now', 0) end
    local found, asked = cards_by_id(ids)
    if asked == 0 then error('select at least one card', 0) end
    if #found ~= asked then error('some of those cards are no longer in hand', 0) end
    -- The Cerulean Bell's card is played or thrown away with them, asked for or not.
    local picked = #found
    for _, card in ipairs(G.hand.cards) do
        if card.ability.forced_selection and not found_in(found, card) then picked = picked + 1 end
    end
    if picked > (G.hand.config.highlighted_limit or 5) then error('too many cards selected', 0) end
    return found
end

-- Selects exactly these cards, plus the Cerulean Bell's (Balatro never lets it go), each once.
local function highlight(selection)
    G.hand:unhighlight_all()
    for _, card in ipairs(selection) do
        if not found_in(G.hand.highlighted, card) then G.hand:add_to_highlighted(card, true) end
    end
end
dealer.highlight = highlight

local function preview(selection)
    if #selection == 0 then return { hand = '', chips = 0, mult = 0, allowed = true } end
    for _, card in ipairs(selection) do
        if card.facing == 'back' then return { hand = '????', level = '?', chips = '?', mult = '?', allowed = true } end
    end
    local key, name, poker_hands = G.FUNCS.get_poker_hand_info(selection)
    if key == 'NULL' then return { hand = '', chips = 0, mult = 0, allowed = true } end
    local stats = G.GAME.hands[key]
    local blind = G.GAME.blind
    local blocked = blind and blind:debuff_hand(selection, poker_hands, key, true)
    local out = { key = key, hand = name, level = stats.level, chips = stats.chips, mult = stats.mult, allowed = not blocked }
    if blocked then
        -- Balatro's warning over the play area: "Hand will not score", and the boss's rule.
        local rule = blind.get_loc_debuff_text and blind:get_loc_debuff_text() or blind.loc_debuff_text or ''
        out.reason = rule:gsub('%s+', ' '):gsub('^ ', ''):gsub(' $', '')
    elseif blind and not blind.disabled then
        -- What a boss does to a hand it lets through, said before it's played.
        if blind.name == 'The Ox' and key == G.GAME.current_round.most_played_poker_hand then
            out.warn = 'Playing it sets your money to $0'
        elseif blind.name == 'The Arm' and stats.level > 1 then
            out.warn = 'It loses a level'
        elseif blind.name == 'The Tooth' then
            out.warn = 'It costs $' .. #selection
        end
    end
    -- A mode's own change to the hand's base (Jimbo's charged Needle, bridge/jimbo.lua).
    if dealer.preview_hook then out = dealer.preview_hook(out, key) end
    return out
end
dealer.preview = preview

-- Scoring trace -------------------------------------------------------------------------------

local function area_of(card)
    if card.area == G.play then return 'play' end
    if card.area == G.hand then return 'hand' end
    if card.area == G.jokers then return 'jokers' end
    if card.area == G.consumeables then return 'consumables' end
    return 'other'
end

-- Mirrors the text and colour choices in Balatro's card_eval_status_text.
local function popup(eval_type, amt, extra)
    if eval_type == 'debuff' then return loc('k_debuffed') or 'Debuffed!', 'debuff' end
    if eval_type == 'chips' then return loc({ type = 'variable', key = 'a_chips', vars = { amt } }), 'chips' end
    if eval_type == 'mult' or eval_type == 'h_mult' then
        return loc({ type = 'variable', key = 'a_mult', vars = { amt } }), 'mult'
    end
    if eval_type == 'x_mult' or eval_type == 'h_x_mult' then
        return loc({ type = 'variable', key = 'a_xmult', vars = { amt } }), 'xmult'
    end
    if eval_type == 'dollars' then
        return (amt < -0.01 and '-' or '') .. (loc('$') or '$') .. tostring(math.abs(amt)), amt < -0.01 and 'debuff' or 'money'
    end
    if eval_type == 'swap' then return loc('k_swapped_ex'), 'special' end
    extra = extra or {}
    local kind = extra.edition and 'edition' or extra.chip_mod and 'chips'
        or extra.Xmult_mod and 'xmult' or extra.mult_mod and 'mult' or 'special'
    return extra.message, kind
end

-- Wraps the Balatro functions that report scoring, recording them while a played hand scores.
local function install_recorders()
    local status_text = card_eval_status_text
    card_eval_status_text = function(card, eval_type, amt, percent, dir, extra)
        if recording and card then
            local target = (extra and extra.focus) or card
            local text, kind = popup(eval_type, amt, extra)
            if text then
                recording.trace[#recording.trace + 1] = {
                    step = 'popup', card = target.sort_id, area = area_of(target), text = text, kind = kind,
                }
            end
        end
        return status_text(card, eval_type, amt, percent, dir, extra)
    end

    local hand_text = update_hand_text
    update_hand_text = function(config, vals)
        if recording and vals and (vals.chips or vals.mult or vals.handname) then
            recording.trace[#recording.trace + 1] = {
                step = 'counter', chips = vals.chips, mult = vals.mult, hand = vals.handname, level = vals.level,
            }
        end
        return hand_text(config, vals)
    end

    local highlight_fn = highlight_card
    highlight_card = function(card, percent, dir)
        if recording and card and dir == 'up' then recording.scoring[#recording.scoring + 1] = card.sort_id end
        return highlight_fn(card, percent, dir)
    end

    local evaluate_play = G.FUNCS.evaluate_play
    G.FUNCS.evaluate_play = function(...)
        local hand = playing
        recording = hand
        evaluate_play(...)
        recording = nil
        if not hand then return end
        playing = nil

        -- evaluate_play leaves the final values in Balatro's hand_chips and mult globals;
        -- everything after this point is animation.
        local score = math.floor((hand_chips or 0) * (mult or 0))
        local played = {}
        for i, card in ipairs(G.play.cards) do played[i] = card.sort_id end
        local defeated = not dealer.in_combat() and hand.chips_before + score >= (hand.target or math.huge)
        send('table_scored', {
            score = score,
            hand = loc(G.GAME.last_hand_played, 'poker_hands') or G.GAME.last_hand_played,
            chips = hand_chips,
            mult = mult,
            played = played,
            scoring = hand.scoring,
            trace = hand.trace,
            round_chips = hand.chips_before + score,
            target = hand.target,
            defeated = defeated,
            -- That was the last hand: the blind starts over (see restart_blind).
            failed = not defeated and not dealer.in_combat() and G.GAME.current_round.hands_left < 1,
        }, hand.id)
    end
end

-- Deal In rules ------------------------------------------------------------------------------

-- Balatro would end the run here. Instead the blind starts over with fresh hands and discards,
-- while jokers, money and hand levels carry on, so the run keeps growing across fights.
local function restart_blind()
    -- Balatro calls end_round every frame while the hand and deck are both empty.
    if restarting_blind then return end
    restarting_blind = true
    G.E_MANAGER:add_event(Event({
        trigger = 'after',
        delay = 0.2,
        func = function()
            -- Undo what the blind did to the table, as Blind:defeat would; new_round applies it again.
            local blind = G.GAME.blind
            if blind.name == 'The Manacle' and not blind.disabled then G.hand:change_size(1) end
            for _, joker in ipairs(G.jokers.cards) do
                if joker.facing == 'back' then joker:flip() end
            end
            G.FUNCS.draw_from_hand_to_discard()
            G.FUNCS.draw_from_discard_to_deck()
            G.E_MANAGER:add_event(Event({
                func = function()
                    G.GAME.chips = 0
                    new_round()
                    restarting_blind = nil
                    return true
                end,
            }))
            return true
        end,
    }))
end

-- Set by bridge/jimbo.lua: return true to take over end_round (a Jimbo fight never ends by itself).
dealer.end_round_override = nil

local function install_end_round()
    local end_round_fn = end_round
    end_round = function()
        if dealer.end_round_override and dealer.end_round_override() then return end
        local game = G.GAME
        if not jimbo_mode() and game.blind and game.chips < game.blind.chips then return restart_blind() end
        return end_round_fn()
    end
end

local function install_shop()
    -- With a joker relic pool from StS2, the shop's card slots only offer those jokers.
    local create_for_shop = create_card_for_shop
    create_card_for_shop = function(area)
        if not (link and link.pool and not jimbo_mode() and area == G.shop_jokers) then return create_for_shop(area) end
        local keys = shop_candidates()
        if #keys == 0 then return nil end
        local key = pseudorandom_element(keys, pseudoseed('bolatro_shop' .. G.GAME.round_resets.ante))
        local card = create_card('Joker', area, nil, nil, nil, nil, key, 'sho')
        create_shop_card_ui(card, 'Joker', area)
        return card
    end

    -- Once every relic is owned or on sale, create_card_for_shop has nothing to offer and the
    -- shop fills its slots with nil; leave those slots empty.
    local emplace = CardArea.emplace
    CardArea.emplace = function(self, card, ...)
        if card == nil then return end
        return emplace(self, card, ...)
    end
end

-- Balatro's reroll, minus the parts that assume every slot gets a card.
local function reroll_shop()
    local round = G.GAME.current_round
    if round.reroll_cost > 0 then ease_dollars(-round.reroll_cost, true) end
    local was_free = round.free_rerolls > 0
    round.free_rerolls = math.max(round.free_rerolls - 1, 0)
    G.GAME.round_scores.times_rerolled.amt = G.GAME.round_scores.times_rerolled.amt + 1
    calculate_reroll_cost(was_free)
    for i = #G.shop_jokers.cards, 1, -1 do
        G.shop_jokers:remove_card(G.shop_jokers.cards[i]):remove()
    end
    for _ = 1, G.GAME.shop.joker_max do
        local card = create_card_for_shop(G.shop_jokers)
        if not card then break end
        G.shop_jokers:emplace(card)
    end
    for _, joker in ipairs(G.jokers.cards) do joker:calculate_joker({ reroll_shop = true }) end
    save_run()
end

-- Makes Balatro's jokers match the joker relics the player owns in StS2 (Deal In only).
local function sync_jokers()
    if not (link and link.owned and G.jokers) or jimbo_mode() then return end
    local present = {}
    for i = #G.jokers.cards, 1, -1 do
        local card = G.jokers.cards[i]
        local key = card.config.center.key
        if link.owned[key] and not present[key] then
            present[key] = true
        else
            card:remove()
        end
    end
    for _, key in ipairs(link.owned_list) do
        if not present[key] and G.P_CENTERS[key] then
            local card = create_card('Joker', G.jokers, nil, nil, true, nil, key)
            card:set_edition(nil, true, true)
            card:add_to_deck()
            G.jokers:emplace(card)
        end
    end
end

-- Runs ---------------------------------------------------------------------------------------

local function unpack_save(path)
    local packed = get_compressed(path)
    if not packed then return nil end
    local ok, saved = pcall(STR_UNPACK, packed)
    return ok and type(saved) == 'table' and saved or nil
end

local function saved_run()
    if G.SAVED_GAME == nil then G.SAVED_GAME = unpack_save(G.SETTINGS.profile .. '/save.jkr') end
    return G.SAVED_GAME
end

-- Balatro keeps one saved run, but each StS2 profile has its own run. Before switching runs,
-- the current Balatro run is set aside under its StS2 run's name, so going back to that StS2
-- run picks it up where it was instead of starting over.
local function archive_dir()
    return G.SETTINGS.profile .. '/bolatro_runs'
end

local function archive_path(run)
    return archive_dir() .. '/' .. run:gsub('[^%w%-_]', '_') .. '.jkr'
end

local function archive_current_run()
    local saved, run = G.ARGS and G.ARGS.save_run, G.GAME and G.GAME.bolatro_run
    -- save_run stores a copy of the game; it must be this run's (a new run has none at first).
    if not (run and saved and saved.GAME and saved.GAME.bolatro_run == run and G.STAGE == G.STAGES.RUN) then return end
    local fs = love.filesystem
    fs.createDirectory(archive_dir())
    compress_and_save(archive_path(run), saved)
    local kept = {}
    for _, name in ipairs(fs.getDirectoryItems(archive_dir())) do
        local info = fs.getInfo(archive_dir() .. '/' .. name)
        kept[#kept + 1] = { name = name, time = info and info.modtime or 0 }
    end
    table.sort(kept, function(a, b) return a.time > b.time end)
    for i = RUN_ARCHIVE + 1, #kept do fs.remove(archive_dir() .. '/' .. kept[i].name) end
end

-- The saved game of an StS2 run's Balatro run: the current save, or one set aside earlier.
local function saved_game_for(run)
    for _, saved in ipairs({ saved_run() or false, unpack_save(archive_path(run)) or false }) do
        if saved and saved.GAME and saved.GAME.bolatro_run == run then return saved end
    end
    return nil
end

-- Starts a run: a saved one ({ savetext }) or a fresh one ({ stake, seed, deck }).
local function begin_run(args)
    if G.OVERLAY_MENU then G.FUNCS.exit_overlay_menu() end
    if args.deck and G.P_CENTERS[args.deck] then G.GAME.viewed_back = Back(G.P_CENTERS[args.deck]) end
    G.FUNCS.start_run(nil, { savetext = args.savetext, stake = args.stake, seed = args.seed })
    restarting_blind = nil -- start_run clears Balatro's event queue, restart included
    if link and link.run then starting_run = { run = link.run, mode = link.mode, game = G.GAME, since = now() } end
end

-- What a fresh run for the linked StS2 run is: Jimbo's deck, stake and seed, or Deal In's plain run.
local function fresh_run_args()
    if jimbo_mode() then
        if not link.deck then return nil end -- Jimbo hasn't chosen a deck yet
        return { stake = link.stake or 1, seed = link.seed, deck = link.deck }
    end
    return { stake = 1 }
end

-- True once Balatro is in the run that belongs to the linked StS2 run (or no run is linked).
local function in_linked_run()
    if not (link and link.run) then return true end
    return not starting_run and G.STAGE == G.STAGES.RUN and G.GAME and G.GAME.bolatro_run == link.run
end
dealer.in_linked_run = in_linked_run

-- Tags a run started by begin_run once Balatro has set it up.
local function track_run_start()
    if not starting_run then return end
    if G.STAGE == G.STAGES.RUN and G.GAME and G.GAME ~= starting_run.game then
        G.GAME.bolatro_run = starting_run.run
        G.GAME.bolatro_mode = starting_run.mode
        starting_run = nil
    elseif now() - starting_run.since > RUN_START_TIMEOUT then
        starting_run = nil
    end
end

-- Continues the linked run's saved game, or starts a fresh run for it. Returns true if it acted.
local function switch_run(fresh)
    if starting_run then return false end
    if G.STAGE == G.STAGES.MAIN_MENU then
        if G.STATE ~= G.STATES.MENU or not G.MAIN_MENU_UI then return false end
    elseif G.STAGE ~= G.STAGES.RUN then
        return false
    end
    local saved = not fresh and saved_game_for(link.run)
    local args = saved and { savetext = saved } or fresh_run_args()
    if not args then return false end
    archive_current_run()
    log('switching to run ' .. link.run .. (saved and ' (continuing its save)' or ' (new run)'))
    begin_run(args)
    return true
end
dealer.switch_run = switch_run

-- Takes the StS2 run named by a request, if any.
local function keys_of(list)
    local keys = {}
    for _, key in ipairs(type(list) == 'table' and list or {}) do keys[#keys + 1] = tostring(key) end
    return keys
end

local function take_link(data)
    if type(data.run) ~= 'string' or data.run == '' then return end
    local owned, owned_list = {}, {}
    for _, key in ipairs(keys_of(data.jokers)) do
        if not owned[key] then
            owned[key] = true
            owned_list[#owned_list + 1] = key
        end
    end
    local mode = data.mode == 'jimbo' and 'jimbo' or 'deal_in'
    link = {
        run = data.run, mode = mode, owned = owned, owned_list = owned_list, pool = keys_of(data.pool),
        deck = type(data.deck) == 'string' and data.deck or nil,
        stake = tonumber(data.stake), seed = type(data.seed) == 'string' and data.seed or nil,
    }
    if mode == 'jimbo' then link.owned, link.pool = nil, nil end
end
dealer.take_link = take_link

function dealer.link() return link end

-- Request handlers ----------------------------------------------------------------------------

local function wait_for_hand(id, needs_redeal)
    waiting[#waiting + 1] = { id = id, needs_redeal = needs_redeal, since = now() }
end
dealer.wait_for_hand = wait_for_hand

local function on_open(data, msg)
    take_link(data)
    wait_for_hand(msg.id, false)
end

-- StS2 sends this as a fight starts, so the autopilot has that run's hand ready before Deal In.
local function on_link(data, msg)
    take_link(data)
    if msg.id then send('ack', {}, msg.id) end
end

local function require_shop()
    if G.STATE ~= G.STATES.SHOP or not G.shop_jokers or leaving_shop then error('the shop is not open', 0) end
end
dealer.require_shop = require_shop

local function on_shop_buy(data, msg)
    require_shop()
    local card
    for _, c in ipairs(G.shop_jokers.cards) do
        if c.sort_id == tonumber(data.card) then card = c end
    end
    if not card then error('that card is no longer for sale', 0) end
    if card.cost > 0 and card.cost > G.GAME.dollars - G.GAME.bankrupt_at then error('not enough money', 0) end
    if not G.FUNCS.check_for_buy_space(card) then error('no room for another joker', 0) end
    G.FUNCS.buy_from_shop({ config = { ref_table = card, id = 'buy_button' } })
    if link and link.owned and card.ability.set == 'Joker' then
        local key = card.config.center.key
        if not link.owned[key] then
            link.owned[key] = true
            link.owned_list[#link.owned_list + 1] = key
        end
    end
    shop_waiting[#shop_waiting + 1] = { id = msg.id, bought = card, ready = function() return card.area == G.jokers end }
end

local function on_shop_reroll(_, msg)
    require_shop()
    local round = G.GAME.current_round
    if round.reroll_cost > 0 and round.reroll_cost > G.GAME.dollars - G.GAME.bankrupt_at then error('not enough money', 0) end
    if jimbo_mode() then
        G.FUNCS.reroll_shop()
        -- The new stock arrives over a few events; answer once it's in.
        shop_waiting[#shop_waiting + 1] = { id = msg.id, ready = function() return true end }
        return
    end
    if link and link.pool and #shop_candidates() == 0 then error('there are no other jokers to roll', 0) end
    reroll_shop()
    send('table', snapshot(), msg.id)
end

local function on_shop_leave(_, msg)
    if G.STATE == G.STATES.SHOP then leaving_shop = true end
    send('ack', {}, msg.id)
end

local function on_preview(data, msg)
    local selection = cards_by_id(data.cards)
    send('preview', preview(selection), msg.id)
end

local function on_discard(data, msg)
    local selection = require_selection(data.cards)
    if G.GAME.current_round.discards_left <= 0 then error('no discards left', 0) end
    -- What the thrown-away cards were worth: Jimbo blocks with it.
    local chips = 0
    for _, card in ipairs(selection) do chips = chips + card:get_chip_bonus() end
    highlight(selection)
    G.FUNCS.discard_cards_from_highlighted(nil)
    selecting_since = nil
    waiting[#waiting + 1] = { id = msg.id, needs_redeal = true, since = now(), extra = { discarded_chips = chips } }
end

local function on_play(data, msg)
    local selection = require_selection(data.cards)
    if G.play and G.play.cards[1] then error('the last hand is still being scored', 0) end
    if G.GAME.current_round.hands_left <= 0 then error('no hands left', 0) end
    highlight(selection)
    playing = {
        id = msg.id, since = now(), chips_before = G.GAME.chips, target = G.GAME.blind and G.GAME.blind.chips,
        trace = {}, scoring = {},
        -- Jimbo's StS2 Strength and Vigor, as Mult and Chips on the hand (bridge/jimbo.lua).
        bonus = { mult = tonumber(data.mult) or 0, chips = tonumber(data.chips) or 0 },
    }
    G.FUNCS.play_cards_from_highlighted(nil)
    selecting_since = nil
end

local function on_sort(data, msg)
    if G.STATE ~= G.STATES.SELECTING_HAND then error('no hand is dealt right now', 0) end
    G.hand:sort(data.by == 'suit' and 'suit desc' or 'desc')
    send('table', snapshot(), msg.id)
end

-- Puts the hand in the order the player dragged it into on StS2's side (cards not listed keep
-- their order, after the rest). As in Balatro, the order is each card's place left to right:
-- Death reads it to know which selected card is copied onto the others (the rightmost).
local function on_reorder(data, msg)
    if not (G.hand and G.hand.cards) then error('no hand is dealt right now', 0) end
    local order = {}
    for i, id in ipairs(type(data.cards) == 'table' and data.cards or {}) do order[tonumber(id)] = i end
    local listed, rest = {}, {}
    for _, card in ipairs(G.hand.cards) do
        if order[card.sort_id] then listed[#listed + 1] = card else rest[#rest + 1] = card end
    end
    table.sort(listed, function(a, b) return order[a.sort_id] < order[b.sort_id] end)
    for _, card in ipairs(rest) do listed[#listed + 1] = card end
    for i, card in ipairs(listed) do G.hand.cards[i] = card end
    G.hand:set_ranks()
    G.hand:align_cards()
    send('ack', {}, msg.id)
end

-- Driving Balatro -----------------------------------------------------------------------------

local function in_pack()
    for _, name in ipairs(PACK_STATES) do
        if G.STATE == G.STATES[name] then return true end
    end
    return false
end
dealer.in_pack = in_pack

local function button(box, id)
    return box and box:get_UIE_by_ID(id)
end
dealer.button = button

-- Cash Out sits in its own UIBox hung under the round summary, not inside G.round_eval. It only
-- appears once the summary has added up the payout; pressing it clears its `button`.
local function cash_out_button()
    for _, box in ipairs(G.I.UIBOX) do
        local found = box.get_UIE_by_ID and box:get_UIE_by_ID('cash_out_button')
        if found and found.config.button then return found end
    end
    return nil
end
dealer.cash_out_button = cash_out_button

-- Calls a button's function unless that very button was just pressed (its state can outlive the
-- press by a few frames while Balatro animates the transition).
local function press(button_fn, uie)
    if last_pressed and last_pressed.button == uie and now() - last_pressed.at < REPRESS_SECONDS then return false end
    last_pressed = { button = uie, at = now() }
    button_fn(uie)
    return true
end
dealer.press = press

-- The Select button of the blind on deck, once the blind select screen is up.
local function select_blind_button()
    local opts = G.blind_select_opts and G.blind_select_opts[string.lower(G.GAME.blind_on_deck or '')]
    local select = button(opts, 'select_blind_button')
    if not select or not G.blind_prompt_box then return nil end
    return select
end
dealer.select_blind_button = select_blind_button

-- Presses whichever button moves Balatro toward a dealt hand. Returns true if it pressed one.
-- A Jimbo run only gets the menus and overlays clicked away; bridge/jimbo.lua moves it on.
local function press_next_button()
    if G.STATE == G.STATES.MENU then
        -- Only for a request: the first table_open may name a saved run to continue instead.
        if not G.MAIN_MENU_UI or #waiting == 0 or jimbo_mode() then return false end
        begin_run({ stake = 1 })
    elseif G.STATE == G.STATES.GAME_OVER then
        if not G.OVERLAY_MENU then return false end -- wait for the game-over screen to finish
        if jimbo_mode() then
            if not switch_run(true) then return false end
        else
            begin_run({ stake = 1 })
        end
    elseif G.OVERLAY_MENU then
        G.FUNCS.exit_overlay_menu() -- e.g. the "you won" screen; play carries on in endless mode
    elseif G.STATE == G.STATES.SHOP and leaving_shop then
        -- The shop stays open until StS2 has shown it to the player (table_shop_leave).
        local next_round = button(G.shop, 'next_round_button')
        if not next_round or not press(G.FUNCS.toggle_shop, next_round) then return false end
        leaving_shop = nil
    elseif jimbo_mode() then
        return false
    elseif G.STATE == G.STATES.BLIND_SELECT then
        local select = select_blind_button()
        if not select then return false end
        return press(G.FUNCS.select_blind, select)
    elseif G.STATE == G.STATES.ROUND_EVAL then
        local cash_out = G.round_eval and cash_out_button()
        if not cash_out then return false end
        return press(G.FUNCS.cash_out, cash_out)
    elseif in_pack() then
        if not G.booster_pack then return false end
        return press(function() G.FUNCS.skip_booster() end, G.booster_pack)
    else
        return false
    end
    return true
end

-- True when Balatro has nothing left to animate or resolve (non-blocking events don't count).
local function events_idle()
    for _, event in ipairs(G.E_MANAGER.queues.base) do
        if event.blocking then return false end
    end
    return true
end
dealer.events_idle = events_idle

-- Cards are in hand once SELECTING_HAND begins; the blind's and jokers' reactions to the draw
-- run as events right after, so the hand is final once those are done.
local function hand_settled()
    if G.STATE ~= G.STATES.SELECTING_HAND or playing or not G.hand or #G.hand.cards == 0 then
        selecting_since = nil
        return false
    end
    selecting_since = selecting_since or now()
    return events_idle() or now() - selecting_since >= SETTLE_LIMIT
end
dealer.hand_settled = hand_settled

-- The shop is ready once it is stocked. Stocking is an event queued with the shop that waits for
-- it to slide in, so an idle queue means the stock is in.
local function shop_settled()
    if G.STATE ~= G.STATES.SHOP or playing or leaving_shop or not G.shop_jokers or not button(G.shop, 'next_round_button') then
        shop_since = nil
        return false
    end
    shop_since = shop_since or now()
    return events_idle() or now() - shop_since >= SHOP_SETTLE_LIMIT
end
dealer.shop_settled = shop_settled

function dealer.is_playing() return playing ~= nil end

-- The hand being played's added Mult and Chips ({ mult, chips }), or nil.
function dealer.play_bonus() return playing and playing.bonus end

function dealer.install(bridge, keep_hand_ready)
    send = bridge.send
    log = bridge.log
    dealer.send = send
    dealer.log = log
    autopilot = keep_hand_ready
    bridge.handlers.table_open = on_open
    bridge.handlers.table_preview = on_preview
    bridge.handlers.table_discard = on_discard
    bridge.handlers.table_play = on_play
    bridge.handlers.table_sort = on_sort
    bridge.handlers.table_reorder = on_reorder
    bridge.handlers.table_shop_buy = on_shop_buy
    bridge.handlers.table_shop_reroll = on_shop_reroll
    bridge.handlers.table_shop_leave = on_shop_leave
    bridge.handlers.table_link = on_link
    install_recorders()
    install_end_round()
    install_shop()
end

-- Drops requests from a host that went away.
function dealer.reset()
    waiting = {}
    shop_waiting = {}
    if playing then playing.id = nil end
end

-- Logs, once per request, what Balatro is doing while a request goes unanswered too long, so a
-- stall can be diagnosed from bridge.log. If Balatro never got into the linked run, it starts a
-- fresh one for it: a new Balatro run beats a table that never opens.
local function describe_situation(id, since)
    local blocking = 0
    for _, event in ipairs(G.E_MANAGER.queues.base) do
        if event.blocking then blocking = blocking + 1 end
    end
    local function name(map, value)
        for k, v in pairs(map) do
            if v == value then return k end
        end
        return tostring(value)
    end
    log(string.format('request #%s waiting %ds: stage=%s state=%s wipe=%s menu=%s overlay=%s paused=%s ' ..
        'blocking_events=%d starting_run=%s linked=%s run=%s/%s mode=%s',
        tostring(id), math.floor(now() - since), name(G.STAGES, G.STAGE), name(G.STATES, G.STATE),
        tostring(G.screenwipe ~= nil), tostring(G.MAIN_MENU_UI ~= nil), tostring(G.OVERLAY_MENU ~= nil),
        tostring(G.SETTINGS.paused), blocking, tostring(starting_run ~= nil), tostring(in_linked_run()),
        tostring(G.GAME and G.GAME.bolatro_run), tostring(link and link.run), tostring(link and link.mode)))
end
dealer.describe_situation = describe_situation

function dealer.rescue_stalled_link(id)
    if not in_linked_run() and link and link.run and not G.screenwipe then
        log('request #' .. tostring(id) .. ' stalled outside run ' .. link.run .. '; starting a fresh run for it')
        local args = fresh_run_args()
        if args then
            starting_run = nil
            begin_run(args)
        end
    end
end

local function report_stalls()
    for _, w in ipairs(waiting) do
        if not w.reported and now() - w.since > STALL_SECONDS then
            w.reported = true
            dealer.rescue_stalled_link(w.id)
            describe_situation(w.id, w.since)
        end
    end
end

-- Answers table_open requests with the dealt hand or the stocked shop.
local function answer_waiting(in_shop)
    local snap
    for i = #waiting, 1, -1 do
        local w = waiting[i]
        if (in_shop and not w.needs_redeal) or (not in_shop and (not w.needs_redeal or w.redealt)) then
            if not snap then
                sync_jokers()
                snap = snapshot()
            end
            local reply = snap
            if w.extra then
                reply = {}
                for k, v in pairs(snap) do reply[k] = v end
                for k, v in pairs(w.extra) do reply[k] = v end
            end
            send('table', reply, w.id)
            table.remove(waiting, i)
        end
    end
end

-- Answers purchases once the joker has arrived and the money has been taken.
local function answer_purchases()
    for i = #shop_waiting, 1, -1 do
        local w = shop_waiting[i]
        w.since = w.since or now()
        if w.ready() and (events_idle() or now() - w.since > SHOP_SETTLE_LIMIT) then
            local snap = snapshot()
            if w.bought then snap.bought = { id = w.bought.sort_id, key = w.bought.config.center.key } end
            send('table', snap, w.id)
            table.remove(shop_waiting, i)
        elseif now() - w.since > PLAY_TIMEOUT then
            send('error', { message = 'the purchase never went through' }, w.id)
            table.remove(shop_waiting, i)
        end
    end
end

function dealer.update()
    if not (G and G.STATES and G.STAGES) then return end
    track_run_start()
    -- Balatro sends a run save to its save thread at most every 30 seconds; send each one at
    -- once, so quitting StS2 soon after a hand doesn't lose it.
    if G.FILE_HANDLER and G.FILE_HANDLER.run then G.FILE_HANDLER.force = true end

    if playing and now() - playing.since > PLAY_TIMEOUT then
        if playing.id then send('error', { message = 'the hand was never scored' }, playing.id) end
        playing = nil
    end
    if leaving_shop and G.STATE ~= G.STATES.SHOP then leaving_shop = nil end
    if #shop_waiting > 0 then answer_purchases() end
    if #waiting > 0 then report_stalls() end

    local in_hand = G.STATE == G.STATES.SELECTING_HAND
    for _, w in ipairs(waiting) do
        if not in_hand then w.redealt = true end
    end

    local linked = in_linked_run()
    if dealer.jimbo_update then dealer.jimbo_update(linked) end
    if linked and hand_settled() then
        answer_waiting(false)
    elseif linked and shop_settled() and #waiting > 0 then
        answer_waiting(true)
    end

    local wants_run = #waiting > 0 or autopilot or (jimbo_mode() and dealer.jimbo_waiting and dealer.jimbo_waiting())
    if (wants_run or leaving_shop) and not playing and now() >= next_action_at and not G.screenwipe then
        -- In another StS2 run's Balatro run: switch runs before anything else.
        local acted
        if not linked and wants_run then
            acted = switch_run()
        else
            acted = press_next_button()
        end
        if acted then next_action_at = now() + ACTION_COOLDOWN end
    end
end

return dealer
