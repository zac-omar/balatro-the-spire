-- Jimbo: Slay the Spire 2's Balatro character plays a whole Balatro run through this module.
--
-- StS2 walks the map and Balatro walks its run in step: every fight is a blind, every shop node
-- is Balatro's shop, and Balatro decides everything about cards, jokers, money and consumables.
-- Nothing here moves the run on by itself; StS2 asks for each step (see protocol/PROTOCOL.md):
--
--   jimbo_link      (one-way) names the StS2 run, its deck, stake and seed
--   jimbo_new_run   -> a fresh Balatro run for the StS2 run, once it is at the first blind select
--   jimbo_state     -> the run as it stands, once Balatro is somewhere stable
--   jimbo_blind     -> selects the blind on deck and deals the fight's first hand
--   jimbo_turn      -> a new StS2 turn: the turn's hands and discards
--   jimbo_win       -> StS2 won the fight: Balatro beats the blind and adds up the cash-out
--   jimbo_cash_out  -> cashes out into the shop
--   jimbo_buy, jimbo_sell, jimbo_use, jimbo_pick, jimbo_skip_pack, jimbo_highlight,
--   jimbo_move_joker, jimbo_dollars, jimbo_deck, jimbo_catalog, jimbo_stats
--
-- In a fight (a "Jimbo blind") Balatro's own round rules give way to StS2's turns:
--   * Each turn has a share of the blind's hands and discards: the blind's hands minus 3 and its
--     discards minus 2, so 1 and 1 at the start, more with decks, vouchers and jokers that add them.
--   * Running out of hands, or scoring past the blind, doesn't end the round: the hand is refilled
--     and play waits for the next turn. Only StS2 ends a fight (jimbo_win).
--   * When the deck runs dry, the discard pile is shuffled back into it, as StS2's draw pile is.

local cards = require('bridge.cards')
local dealer = require('bridge.dealer')

local jimbo = {}

local REQUEST_TIMEOUT = 30 -- a step that hasn't happened by now has gone wrong

local send, log
local now = dealer.now

local requests = {} -- FIFO: { id, label, since, step }; step() returns nil (wait) or reply_type, data
local unlocked_all
local cashing      -- { rows, total }: the cash-out being added up after a won fight

-- The fight in progress, kept in the run itself so it survives Balatro restarting mid-fight:
-- { per_turn = { hands, discards }, reshuffles }. Nil between fights.
local function fight()
    return G.GAME and G.GAME.jimbo_fight
end

local function in_combat()
    return fight() ~= nil
end

-- Names Balatro's current state for StS2.
local function state_name()
    local s = G.STATE
    if s == G.STATES.SELECTING_HAND or s == G.STATES.HAND_PLAYED or s == G.STATES.DRAW_TO_HAND then return 'hand' end
    if s == G.STATES.SHOP then return 'shop' end
    if s == G.STATES.BLIND_SELECT then return 'blind_select' end
    if s == G.STATES.ROUND_EVAL or s == G.STATES.NEW_ROUND then return 'round_eval' end
    if dealer.in_pack() then return 'pack' end
    if s == G.STATES.GAME_OVER then return 'game_over' end
    return 'other'
end

-- The open pack's cards (nil while a pack is being opened or put away).
local function pack_cards()
    return dealer.in_pack() and G.pack_cards and G.pack_cards.cards or nil
end

-- True when Balatro has stopped somewhere StS2 can show.
local function stable()
    if not dealer.in_linked_run() or dealer.is_playing() then return false end
    local s = G.STATE
    if s == G.STATES.SELECTING_HAND then return dealer.hand_settled() end
    if s == G.STATES.SHOP then return dealer.shop_settled() end
    if s == G.STATES.BLIND_SELECT then return dealer.select_blind_button() ~= nil and dealer.events_idle() end
    if s == G.STATES.ROUND_EVAL then return dealer.cash_out_button() ~= nil end
    if dealer.in_pack() then return pack_cards() ~= nil and #pack_cards() > 0 and dealer.events_idle() end
    return false
end

-- Snapshot -----------------------------------------------------------------------------------

local function pack_info(used)
    if not pack_cards() then return nil end
    local list = {}
    for i, card in ipairs(pack_cards()) do
        list[i] = cards.card_info(card, used, true)
        if card.ability.consumeable then list[i].usable = cards.usable(card, true) end
    end
    local kind
    for _, name in ipairs({ 'TAROT_PACK', 'PLANET_PACK', 'SPECTRAL_PACK', 'STANDARD_PACK', 'BUFFOON_PACK' }) do
        if G.STATE == G.STATES[name] then kind = string.lower(name:gsub('_PACK', '')) end
    end
    return { kind = kind, choices = G.GAME.pack_choices or 1, cards = list }
end

-- A pick from a pack that dealt a hand: { card, before, after, used } until a reply carries it.
local pack_effect

local function hand_infos(used)
    local list = {}
    for i, card in ipairs(G.hand and G.hand.cards or {}) do list[i] = cards.card_info(card, used, true) end
    return list
end

local function extend_snapshot(snap, used)
    if not dealer.jimbo_mode() then return end
    local game = G.GAME
    snap.state = state_name()
    local consumables = {}
    for i, card in ipairs(G.consumeables.cards) do
        consumables[i] = cards.card_info(card, used, true)
        consumables[i].usable = cards.usable(card)
    end
    snap.consumables = consumables
    snap.consumable_slots = G.consumeables.config.card_limit
    snap.joker_slots = G.jokers.config.card_limit
    snap.hand_size = G.hand.config.card_limit
    local vouchers = {}
    for key, owned in pairs(game.used_vouchers or {}) do
        if owned then vouchers[#vouchers + 1] = key end
    end
    table.sort(vouchers)
    snap.vouchers = vouchers
    local back = game.selected_back and game.selected_back.effect and game.selected_back.effect.center
    snap.deck_key = back and back.key
    snap.stake = game.stake
    snap.per_turn = fight() and fight().per_turn or nil
    snap.in_fight = in_combat()
    snap.discard_left = #G.discard.cards
    snap.pack = pack_info(used)
    snap.won_run = game.won or nil
    snap.interest = { amount = game.interest_amount, cap = game.interest_cap, off = game.modifiers.no_interest or nil }
    if snap.state ~= 'hand' then snap.next_blind = dealer.next_blind_info(used) end
    -- The tags waiting to be used (Balatro shows them down the side), with their art and words.
    local tags = {}
    for i, tag in ipairs(game.tags or {}) do
        local proto = G.P_TAGS[tag.key]
        tags[i] = {
            key = tag.key,
            name = proto and cards.loc({ type = 'name_text', set = 'Tag', key = tag.key }) or tag.name,
            text = proto and cards.tag_text(tag.key) or nil,
            sprite = proto and cards.sprite('tags', proto.pos, used) or nil,
        }
    end
    snap.tags = tags
    -- The Needle's charge (Jimbo's fights): the next hand's Mult multiplier, from 1.
    local f = fight()
    snap.charge = f and f.needle_charge and f.needle_charge > 0 and (1 + f.needle_charge) or nil
    -- The road ahead, for Jimbo's map: this ante's three blinds (its Boss Blind is already
    -- chosen) and every ante's base chips.
    local antes = {}
    for ante = 1, 8 do antes[ante] = get_blind_amount(ante) * game.starting_params.ante_scaling end
    if pack_effect and pack_effect.after then
        for atlas in pairs(pack_effect.used) do used[atlas] = true end
        snap.pack_effect = { card = pack_effect.card, before = pack_effect.before, after = pack_effect.after }
        pack_effect = nil
    end
    snap.road = {
        ante = game.round_resets.ante,
        small = dealer.blind_choice('Small', used),
        big = dealer.blind_choice('Big', used),
        boss = dealer.blind_choice('Boss', used),
        antes = antes,
    }
end

-- Requests -----------------------------------------------------------------------------------

-- Queues a step for request `msg`: `step` runs every frame until it returns the reply.
local function pend(msg, label, step, timeout)
    requests[#requests + 1] = { id = msg.id, label = label, since = now(), step = step, timeout = timeout or REQUEST_TIMEOUT }
end

local function reply_state()
    return 'jimbo', dealer.snapshot()
end

-- Balatro's isolated profile knows nothing yet; a Jimbo run gets every joker, deck and voucher.
local function unlock_everything()
    if unlocked_all or love.filesystem.getIdentity() ~= 'BalatroBridge' then return end
    unlocked_all = true
    for _, group in ipairs({ G.P_CENTERS, G.P_BLINDS, G.P_TAGS }) do
        for _, v in pairs(group) do
            if not v.demo and not v.wip then
                v.unlocked, v.discovered, v.alerted = true, true, true
            end
        end
    end
end

local function link_from(data)
    data.mode = 'jimbo'
    dealer.take_link(data)
    unlock_everything()
end

local function find_card(area, id)
    for _, card in ipairs(area and area.cards or {}) do
        if card.sort_id == tonumber(id) then return card end
    end
    return nil
end

local function affordable(card)
    return card.cost <= 0 or card.cost <= G.GAME.dollars - G.GAME.bankrupt_at
end

-- Handlers -----------------------------------------------------------------------------------

local function on_link(data, msg)
    link_from(data)
    if msg.id then send('ack', {}, msg.id) end
end

local function on_state(data, msg)
    link_from(data)
    pend(msg, 'state', function()
        if stable() then return reply_state() end
    end)
end

-- A fresh Balatro run for the StS2 run: Jimbo has just picked his deck at the Dealer.
local function on_new_run(data, msg)
    link_from(data)
    local started = false
    pend(msg, 'new run', function()
        if not started then
            started = dealer.switch_run(true)
            return nil
        end
        if dealer.in_linked_run() and G.STATE == G.STATES.BLIND_SELECT and stable() then return reply_state() end
    end)
end

-- Moves Balatro on to the blind select screen (out of a round summary, shop or pack).
local function toward_blind_select()
    if G.STATE == G.STATES.ROUND_EVAL then
        local cash_out = dealer.cash_out_button()
        if cash_out then dealer.press(G.FUNCS.cash_out, cash_out) end
    elseif G.STATE == G.STATES.SHOP and dealer.shop_settled() then
        local next_round = dealer.button(G.shop, 'next_round_button')
        if next_round then dealer.press(G.FUNCS.toggle_shop, next_round) end
    elseif dealer.in_pack() and G.booster_pack and dealer.events_idle() then
        dealer.press(function() G.FUNCS.skip_booster() end, G.booster_pack)
    end
end

-- Status cards: what an enemy shuffles into Jimbo's StS2 piles (Wound, Burn, Slimed...) joins
-- his Balatro deck for the fight as a dud, a Stone Card that's always debuffed: it never scores
-- and takes up room in hand. StS2 keeps the card's own rules (what it does at the end of a turn);
-- Balatro only holds it. Duds with Exhaust are gone once discarded or played, and every dud
-- leaves when the fight does.

local function is_dud(card) return card and card.ability and card.ability.jimbo_dud ~= nil end

local function each_dud(fn)
    for i = #(G.playing_cards or {}), 1, -1 do
        local card = G.playing_cards[i]
        if is_dud(card) then fn(card) end
    end
end

local function remove_dud(card)
    card:start_dissolve(nil, true)
    card:remove()
end

-- Duds with Exhaust that were discarded or played don't come back.
local function exhaust_duds()
    for i = #G.discard.cards, 1, -1 do
        local card = G.discard.cards[i]
        if is_dud(card) and card.ability.jimbo_dud.exhaust then remove_dud(card) end
    end
end

local function purge_duds()
    each_dud(remove_dud)
end

local DUD_PILES = { hand = 'hand', discard = 'discard', draw = 'deck' }

local function add_dud(spec)
    local area = G[DUD_PILES[spec.pile] or 'deck']
    local card = create_playing_card({ front = pseudorandom_element(G.P_CARDS, pseudoseed('jimbo_dud')), center = G.P_CENTERS.m_stone }, area, true, true)
    card.ability.jimbo_dud = { ref = spec.ref, name = tostring(spec.name or 'Status'), text = spec.text, exhaust = spec.exhaust and true or nil }
    card:set_debuff(true)
    if area == G.deck then
        -- Shuffled in somewhere, as StS2 shuffles a status into the draw pile.
        local list, last = G.deck.cards, #G.deck.cards
        local at = pseudorandom('jimbo_dud_at', 1, last)
        list[at], list[last] = list[last], list[at]
    end
end

-- StS2 added status cards to Jimbo's piles (add), or one burned away at the end of his turn (remove).
local function on_duds(data, msg)
    link_from(data)
    local done = false
    pend(msg, 'status cards', function()
        if not done then
            if not (fight() and stable()) then return nil end
            done = true
            for _, spec in ipairs(data.add or {}) do add_dud(spec) end
            local gone = {}
            for _, ref in ipairs(data.remove or {}) do gone[ref] = true end
            each_dud(function(card) if gone[card.ability.jimbo_dud.ref] then remove_dud(card) end end)
            return nil
        end
        if stable() and dealer.events_idle() then return reply_state() end
    end)
end

-- The share of the blind's hands and discards each turn gets, and how many turns those hands
-- last: past them, the fight is in overtime (StS2 makes the enemies stronger every turn).
local function share_of(hands, discards)
    local each = math.max(1, hands - 3)
    return { hands = each, discards = math.max(0, discards - 2), turns = math.max(1, math.ceil(hands / each)) }
end

local function start_turn()
    local round = G.GAME.current_round
    round.hands_left = fight().per_turn.hands
    round.discards_left = fight().per_turn.discards
end

-- The fight's hands and discards still to come, counted over the whole fight as Balatro counts a
-- round's: its totals less what's been played and discarded, never below 0. A fight whose turns
-- get no discards has none to use, so none are left. A hand that's scoring counts as played
-- (Balatro only counts it once it has scored).
-- Overtime: the fight's turns have outlasted the blind's hands (StS2 counts the turns, see
-- on_turn); from then on the enemies grow stronger every turn.
local function in_overtime()
    local f = fight()
    return f and f.turn and f.per_turn and f.per_turn.turns and f.turn > f.per_turn.turns or false
end

-- Charge the Needle (Jimbo's fights): The Needle gives the blind one hand, so Overtime comes on
-- turn 2, and faster (JimboCombat). Its one hand can be held: each turn that ends without a hand
-- played charges the next one, x2, x3, then x4 Mult on its base. Playing a hand spends the charge.
local NEEDLE_MAX = 3
local function needle_live()
    local b = G.GAME.blind
    return b and b.name == 'The Needle' and not b.disabled and fight() and fight().blind_hands and true or false
end

local function charge_needle()
    local f = fight()
    if not needle_live() then
        f.needle_charge = nil
        return
    end
    local played = G.GAME.current_round.hands_played
    -- Turn 1 has no turn before it to have held.
    if (f.turn or 1) > 1 and (f.needle_played or 0) == played then f.needle_charge = math.min((f.needle_charge or 0) + 1, NEEDLE_MAX) end
    f.needle_played = played
end

local function needle_multiplier()
    local f = fight()
    return needle_live() and f.needle_charge and f.needle_charge > 0 and (1 + f.needle_charge) or 1
end

local function fight_left(scoring)
    local f, round = fight(), G.GAME.current_round
    local hands = math.max(0, (f.blind_hands or 0) - round.hands_played - (scoring and 1 or 0))
    local discards = 0
    if f.per_turn and f.per_turn.discards > 0 then discards = math.max(0, (f.blind_discards or 0) - round.discards_used) end
    return hands, discards
end

-- The Rosetta Stone at the start of a turn: what StS2 changes about the turn's energy becomes its
-- hands (a gain of at most 1, and never below 1 hand), what it changes about the draw becomes its
-- discards, and each card-afflicting debuff on Jimbo (Tangled, Hex...) debuffs a card in his hand
-- for the turn. Hands and discards gained or lost count in the fight's totals too.
local function unafflict()
    for _, card in ipairs(G.playing_cards or {}) do
        if card.ability.jimbo_afflicted then
            card.ability.jimbo_afflicted = nil
            if G.GAME.blind then G.GAME.blind:debuff_card(card) else card:set_debuff(false) end
        end
    end
end

local function adjust_turn(data)
    local f, round = fight(), G.GAME.current_round
    local hands, discards = tonumber(data.hands) or 0, tonumber(data.discards) or 0
    if hands ~= 0 then
        local before = round.hands_left
        round.hands_left = math.max(1, before + math.min(hands, 1))
        f.blind_hands = (f.blind_hands or 0) + (round.hands_left - before)
    end
    if discards ~= 0 then
        local before = round.discards_left
        round.discards_left = math.max(0, before + discards)
        f.blind_discards = (f.blind_discards or 0) + (round.discards_left - before)
    end
    unafflict()
    local count = tonumber(data.afflict) or 0
    if count > 0 then
        local candidates = {}
        for _, card in ipairs(G.hand.cards) do
            if not card.debuff and not card.ability.jimbo_dud then candidates[#candidates + 1] = card end
        end
        pseudoshuffle(candidates, pseudoseed('jimbo_afflict'))
        for i = 1, math.min(count, #candidates) do
            candidates[i].ability.jimbo_afflicted = true
            candidates[i]:set_debuff(true)
        end
    end
end

-- Whether Balatro's Mr. Bones would save this round: he's there and not debuffed, and the
-- fight's hands have scored at least a quarter of the blind.
local function bones_can_save()
    local blind = G.GAME.blind
    if not (blind and blind.chips and blind.chips > 0) or G.GAME.chips / blind.chips < 0.25 then return false end
    for _, joker in ipairs(G.jokers.cards) do
        if joker.ability.name == 'Mr. Bones' and not joker.debuff then return true end
    end
    return false
end

-- The fight begins: Balatro plays the blind on deck (StS2 names the kind it expects).
local function on_blind(data, msg)
    link_from(data)
    local selected = false
    pend(msg, 'blind', function()
        if not dealer.in_linked_run() then return nil end
        local s = G.STATE
        if not selected then
            if s == G.STATES.SELECTING_HAND or s == G.STATES.HAND_PLAYED or s == G.STATES.DRAW_TO_HAND then
                -- Already in this blind (StS2 restarted the fight after a reload): carry on with it.
                selected = true
                local resets = G.GAME.round_resets
                G.GAME.jimbo_fight = fight() or {
                    per_turn = share_of(resets.hands, resets.discards), blind_hands = resets.hands, blind_discards = resets.discards,
                    first_drawn = true,
                }
            elseif s == G.STATES.BLIND_SELECT then
                local select = dealer.select_blind_button()
                if not select or not dealer.events_idle() then return nil end
                if data.kind and data.kind ~= G.GAME.blind_on_deck then
                    log('StS2 expected the ' .. tostring(data.kind) .. ' blind but Balatro is on the ' .. tostring(G.GAME.blind_on_deck))
                end
                G.GAME.jimbo_fight = { reshuffles = 0 }
                purge_duds() -- strays from a fight that never finished
                unafflict()
                dealer.press(G.FUNCS.select_blind, select)
                selected = true
            else
                toward_blind_select()
            end
            return nil
        end
        if not dealer.hand_settled() then return nil end
        local f, round = fight(), G.GAME.current_round
        if not f.per_turn then
            -- What the blind gave the round (decks, vouchers, jokers like Burglar) is shared out per
            -- turn. What's been used of it is Balatro's own count (see fight_left).
            f.blind_hands, f.blind_discards = round.hands_left, round.discards_left
            f.per_turn = share_of(round.hands_left, round.discards_left)
            f.first_drawn = true -- the fight's hand is dealt; later refills aren't a round start
            f.turn = 1
        end
        start_turn()
        return reply_state()
    end)
end

-- A new StS2 turn: the hand is topped up and the turn's hands and discards come in.
local function on_turn(data, msg)
    link_from(data)
    local refilling = false
    exhaust_duds()
    pend(msg, 'turn', function()
        if not (fight() and fight().per_turn) then error('no fight is on', 0) end
        if not dealer.hand_settled() then return nil end
        if not refilling and #G.hand.cards < G.hand.config.card_limit and (#G.deck.cards > 0 or #G.discard.cards > 0) then
            refilling = true
            fight().turn_refill = true
            G.STATE = G.STATES.DRAW_TO_HAND
            G.STATE_COMPLETE = false
            return nil
        end
        fight().turn = tonumber(data.turn) or ((fight().turn or 1) + 1)
        charge_needle()
        start_turn()
        adjust_turn(data)
        return reply_state()
    end)
end

-- StS2 won the fight: the blind is beaten. Replies with the cash-out, once Balatro has added it up.
local function on_win(data, msg)
    link_from(data)
    local ended = false
    pend(msg, 'win', function()
        if not dealer.in_linked_run() then return nil end
        if G.STATE == G.STATES.SHOP and dealer.shop_settled() then
            -- Already cashed out (StS2 asked again after a reload).
            G.GAME.jimbo_fight = nil
            return 'jimbo_cash_out', { rows = {}, total = 0, paid = {}, notes = {}, table = dealer.snapshot() }
        end
        if not ended then
            if G.STATE == G.STATES.ROUND_EVAL then
                ended = true
            elseif G.STATE == G.STATES.SELECTING_HAND and not dealer.is_playing() and dealer.events_idle() then
                purge_duds()
                unafflict()
                -- The cash-out pays for the fight's hands (and discards) left unused, as Balatro's
                -- does: a fight won in fewer hands earns more.
                local f, round = fight(), G.GAME.current_round
                if f and f.blind_hands then round.hands_left, round.discards_left = fight_left(false) end
                -- Mr. Bones saved Jimbo in StS2: the blind isn't beaten, and Balatro's own end of
                -- round lets Mr. Bones save the run (and go), as in Balatro.
                local saved = data.saved and bones_can_save()
                G.GAME.jimbo_fight = nil
                cashing = { rows = {}, total = 0, paid = {}, notes = {} }
                -- The Wager: a raised Boss Blind beaten pays a Rare Tag (a free Rare Joker in the next shop).
                if dealer.raised() and G.GAME.blind:get_type() == 'Boss' and not saved then
                    add_tag(Tag('tag_rare'))
                    cashing.notes[#cashing.notes + 1] = 'The Wager: ' .. (cards.loc({ type = 'name_text', set = 'Tag', key = 'tag_rare' }) or 'Rare Tag')
                end
                G.GAME.jimbo_raise = nil
                if not saved then G.GAME.chips = math.max(G.GAME.chips, G.GAME.blind.chips) end
                G.STATE = G.STATES.NEW_ROUND
                G.STATE_COMPLETE = false
                ended = true
            end
            return nil
        end
        if G.STATE ~= G.STATES.ROUND_EVAL or not dealer.cash_out_button() then return nil end
        local summary = cashing or { rows = {}, paid = {}, notes = {} }
        local total = cashing and cashing.total or G.GAME.current_round.dollars
        cashing = nil
        return 'jimbo_cash_out', {
            rows = summary.rows, total = total, paid = summary.paid, notes = summary.notes, table = dealer.snapshot(),
        }
    end)
end

-- The Wager: from the shop, the Boss Blind on deck is raised (dealer.RAISE times its chips) for
-- a Rare Tag if it's beaten. From the second ante on; once raised, it stays raised.
local function on_raise(data, msg)
    link_from(data)
    pend(msg, 'raise', function()
        if not stable() then return nil end
        if dealer.on_deck_kind() ~= 'Boss' or G.GAME.round_resets.ante < 2 then
            error('only a Boss Blind can be raised, from the second ante on', 0)
        end
        G.GAME.jimbo_raise = { ante = G.GAME.round_resets.ante }
        return reply_state()
    end)
end

local function on_cash_out(data, msg)
    link_from(data)
    pend(msg, 'cash out', function()
        if G.STATE == G.STATES.ROUND_EVAL then
            local cash_out = dealer.cash_out_button()
            if cash_out then dealer.press(G.FUNCS.cash_out, cash_out) end
            return nil
        end
        if G.STATE == G.STATES.SHOP and dealer.shop_settled() then return reply_state() end
    end)
end

-- Waits for Balatro to finish what a button started, then replies with the run. Balatro moves
-- money and cards in queued events (a purchase's price comes off after the card moves), so this
-- waits for the queue to empty too, for at most SETTLE_EVENTS seconds from the request.
local SETTLE_EVENTS = 3
local function settle(msg, label, done)
    local since = now()
    pend(msg, label, function()
        if (done == nil or done()) and stable() and (dealer.events_idle() or now() - since >= SETTLE_EVENTS) then
            return reply_state()
        end
    end)
end

local function on_buy(data, msg)
    link_from(data)
    dealer.require_shop()
    local card = find_card(G.shop_jokers, data.card)
    local area = 'cards'
    if not card then card, area = find_card(G.shop_vouchers, data.card), 'voucher' end
    if not card then card, area = find_card(G.shop_booster, data.card), 'booster' end
    if not card then error('that card is no longer for sale', 0) end
    if not affordable(card) then error('not enough money', 0) end
    local e = { config = { ref_table = card, id = data.use and 'buy_and_use' or 'buy_button' } }
    if area == 'cards' then
        if data.use then
            if not card.ability.consumeable or not cards.usable(card, true) then error('that card can\'t be used now', 0) end
        elseif not G.FUNCS.check_for_buy_space(card) then
            error(card.ability.consumeable and 'no room for another consumable' or 'no room for another joker', 0)
        end
        G.FUNCS.buy_from_shop(e)
    else
        -- Vouchers are redeemed and packs opened as they're bought; both take their own price.
        G.FUNCS.use_card(e)
    end
    settle(msg, 'buy', function() return card.area ~= G.shop_jokers and card.area ~= G.shop_vouchers and card.area ~= G.shop_booster end)
end

local function on_sell(data, msg)
    link_from(data)
    local card = find_card(G.jokers, data.card) or find_card(G.consumeables, data.card)
    if not card then error('that card is gone', 0) end
    if not card:can_sell_card() then error(card.ability.eternal and 'eternal cards can\'t be sold' or 'that can\'t be sold right now', 0) end
    G.FUNCS.sell_card({ config = { ref_table = card } })
    settle(msg, 'sell', function() return card.area ~= G.jokers and card.area ~= G.consumeables end)
end

local function select_in_hand(ids)
    local selection, asked = dealer.cards_by_id(ids)
    if #selection ~= asked then error('some of those cards are no longer in hand', 0) end
    dealer.highlight(selection)
end

local function on_use(data, msg)
    link_from(data)
    local card = find_card(G.consumeables, data.card)
    if not card then error('that card is gone', 0) end
    -- On the cards in hand: a fight's, or the hand an Arcana or Spectral pack deals (Balatro lets
    -- the consumable slots be used on it too, and StS2 shows the effect as a pack card's).
    local pack_hand = dealer.in_pack() and G.hand and #G.hand.cards > 0
    if G.STATE == G.STATES.SELECTING_HAND or pack_hand then select_in_hand(data.cards) end
    if not cards.usable(card) then error('that can\'t be used right now', 0) end
    if pack_hand then
        pack_effect = { used = {} }
        pack_effect.card = cards.card_info(card, pack_effect.used, true)
        pack_effect.before = hand_infos(pack_effect.used)
    end
    G.FUNCS.use_card({ config = { ref_table = card } })
    settle(msg, 'use', function()
        if card.area == G.consumeables or G.STATE == G.STATES.PLAY_TAROT then return false end
        if pack_hand and pack_effect and not pack_effect.after then
            if not (dealer.in_pack() and stable() and dealer.events_idle()) then return false end
            pack_effect.after = hand_infos(pack_effect.used)
        end
        return true
    end)
end

local function on_pick(data, msg)
    link_from(data)
    if not pack_cards() then error('no pack is open', 0) end
    local card = find_card(G.pack_cards, data.card)
    if not card then error('that card is gone', 0) end
    if G.hand and #G.hand.cards > 0 then select_in_hand(data.cards) end
    if card.ability.consumeable then
        if not cards.usable(card, true) then error('that can\'t be used on those cards', 0) end
    elseif card.ability.set == 'Joker' and not G.FUNCS.check_for_buy_space(card) then
        error('no room for another joker', 0)
    end
    -- A tarot or spectral used on the cards the pack dealt: the reply shows them before and after
    -- (taken just before the pack closes and they go back to the deck), so StS2 can show the effect.
    local watch = card.ability.consumeable and G.hand and #G.hand.cards > 0
    if watch then
        pack_effect = { used = {} }
        pack_effect.card = cards.card_info(card, pack_effect.used, true)
        pack_effect.before = hand_infos(pack_effect.used)
    end
    G.FUNCS.use_card({ config = { ref_table = card } })
    settle(msg, 'pick', function()
        if card.area == G.pack_cards then return false end
        -- A pack with more to choose stays open: its hand is the after.
        if watch and pack_effect and not pack_effect.after and dealer.in_pack() and stable() and dealer.events_idle() then
            pack_effect.after = hand_infos(pack_effect.used)
        end
        return true
    end)
end

local function on_skip_pack(data, msg)
    link_from(data)
    if not dealer.in_pack() then error('no pack is open', 0) end
    G.FUNCS.skip_booster()
    settle(msg, 'skip pack', function() return not dealer.in_pack() end)
end

-- Skips the next blind for its tag, as Balatro's blind select would: Balatro leaves the shop
-- (StS2 shows the skip there) and skips the Small or Big Blind on deck. A tag that opens a pack
-- opens it now. Jimbo still gets the next shop (see on_open_shop).
local function on_skip(data, msg)
    link_from(data)
    local skipped = false
    pend(msg, 'skip', function()
        if not skipped then
            if G.STATE == G.STATES.SHOP then
                local next_round = dealer.button(G.shop, 'next_round_button')
                if next_round and dealer.shop_settled() then dealer.press(G.FUNCS.toggle_shop, next_round) end
                return nil
            end
            if G.STATE ~= G.STATES.BLIND_SELECT or not dealer.select_blind_button() or not dealer.events_idle() then return nil end
            local on_deck = G.GAME.blind_on_deck
            if on_deck ~= 'Small' and on_deck ~= 'Big' then error("a boss blind can't be skipped", 0) end
            G.FUNCS.skip_blind({ UIBox = G.blind_select_opts[string.lower(on_deck)] })
            G.GAME.jimbo_owes_shop = true
            skipped = true
            return nil
        end
        if stable() then return reply_state() end
    end)
end

-- Opens the shop that a skipped blind didn't pay for: in Jimbo's runs the shop between blinds is
-- always open. Otherwise replies with the run as it is.
local function on_open_shop(data, msg)
    link_from(data)
    pend(msg, 'shop', function()
        if G.STATE == G.STATES.BLIND_SELECT and G.GAME.jimbo_owes_shop and G.blind_select and dealer.events_idle() then
            G.GAME.jimbo_owes_shop = nil
            G.blind_select:remove()
            G.blind_select = nil
            if G.blind_prompt_box then
                G.blind_prompt_box:remove()
                G.blind_prompt_box = nil
            end
            G.STATE = G.STATES.SHOP
            G.STATE_COMPLETE = false
            return nil
        end
        if stable() and not (G.STATE == G.STATES.BLIND_SELECT and G.GAME.jimbo_owes_shop) then return reply_state() end
    end)
end

-- Selects cards in hand (for a tarot, say): replies with the poker hand and what's usable now.
local function on_highlight(data, msg)
    link_from(data)
    -- Waits its turn: changing the selection while a tarot is still working on it (they work
    -- on the selected cards over a second or two) would pull cards out from under it.
    pend(msg, 'highlight', function()
        if not stable() or not dealer.events_idle() then return nil end
        local selection = dealer.cards_by_id(data.cards)
        dealer.highlight(selection)
        local usable = {}
        for _, card in ipairs(G.consumeables.cards) do
            if cards.usable(card) then usable[#usable + 1] = card.sort_id end
        end
        local pack_usable = {}
        for _, card in ipairs(G.pack_cards and G.pack_cards.cards or {}) do
            if card.ability.consumeable and cards.usable(card, true) then pack_usable[#pack_usable + 1] = card.sort_id end
        end
        return 'jimbo_highlight', { preview = dealer.preview(selection), usable = usable, pack_usable = pack_usable }
    end)
end

-- Moves a joker to slot `to`, as dragging it does. Queued behind any hand still scoring, so the
-- order only changes between hands.
local function on_move_joker(data, msg)
    link_from(data)
    pend(msg, 'move joker', function()
        if not (stable() and dealer.events_idle()) then return nil end
        local card = find_card(G.jokers, data.card)
        if not card then error('that joker is gone', 0) end
        local to = math.max(1, math.min(#G.jokers.cards, tonumber(data.to) or 1))
        for i, c in ipairs(G.jokers.cards) do
            if c == card then table.remove(G.jokers.cards, i) break end
        end
        table.insert(G.jokers.cards, to, card)
        G.jokers:set_ranks()
        return reply_state()
    end)
end

-- StS2 took or gave money (a thief, say): Balatro's dollars follow.
local function on_dollars(data, msg)
    link_from(data)
    local delta = tonumber(data.delta) or 0
    if delta ~= 0 then ease_dollars(delta, true) end
    if msg.id then send('ack', { dollars = G.GAME.dollars }, msg.id) end
end

-- Every playing card in the run, for StS2's deck view.
-- Balatro's Run Info: each poker hand's level, chips, mult and plays, in Balatro's order
-- (secret hands only once they've turned up).
local function poker_hands()
    local list = {}
    for _, key in ipairs(G.handlist) do
        local stats = G.GAME.hands[key]
        if stats and stats.visible then
            list[#list + 1] = {
                key = key, name = localize(key, 'poker_hands'),
                level = stats.level, chips = stats.chips, mult = stats.mult, played = stats.played,
            }
        end
    end
    return list
end

local function on_deck(data, msg)
    link_from(data)
    local used = {}
    local list = {}
    local where = {}
    for _, c in ipairs(G.hand.cards) do where[c] = 'hand' end
    for _, c in ipairs(G.discard.cards) do where[c] = 'discard' end
    for _, c in ipairs(G.play.cards) do where[c] = 'play' end
    for _, card in ipairs(G.playing_cards) do
        local info = cards.card_info(card, used, true)
        info.facing = 'front'
        info.debuff = nil
        info.pile = where[card] or 'deck'
        list[#list + 1] = info
    end
    send('jimbo_deck', { cards = list, hands = poker_hands(), atlases = cards.atlases(used), texture_scale = G.SETTINGS.GRAPHICS.texture_scaling }, msg.id)
end

-- The run in numbers, as Balatro's game over screen shows it.
local function on_stats(data, msg)
    link_from(data)
    local game = G.GAME
    local scores = game.round_scores or {}
    local function amount(key) return scores[key] and scores[key].amt or 0 end
    local most, plays = nil, 0
    for _, key in ipairs(G.handlist) do
        local stats = game.hands[key]
        if stats and stats.played > plays then most, plays = key, stats.played end
    end
    send('jimbo_stats', {
        ante = game.round_resets.ante,
        round = game.round,
        best_hand = amount('hand'),
        most_played = most and localize(most, 'poker_hands') or nil,
        most_played_count = plays,
        cards_played = amount('cards_played'),
        cards_discarded = amount('cards_discarded'),
        cards_purchased = amount('cards_purchased'),
        times_rerolled = amount('times_rerolled'),
        jokers = #G.jokers.cards,
    }, msg.id)
end

-- What the Dealer can offer: vouchers Balatro's shop could stock next (requirements met, none
-- owned), or legendary jokers not yet owned. Seeded, so a reload offers the same.
local function on_offers(data, msg)
    link_from(data)
    pend(msg, 'offers', function()
        if not stable() then return nil end
        local candidates = {}
        if data.kind == 'voucher' then
            for _, key in ipairs(get_current_pool('Voucher')) do
                if key ~= 'UNAVAILABLE' then candidates[#candidates + 1] = key end
            end
        elseif data.kind == 'legendary' then
            local owned = {}
            for _, card in ipairs(G.jokers.cards) do owned[card.config.center.key] = true end
            for _, center in ipairs(G.P_CENTER_POOLS.Joker) do
                if center.rarity == 4 and not owned[center.key] then candidates[#candidates + 1] = center.key end
            end
        end
        table.sort(candidates)
        local picks = {}
        local seed = 'dealer_' .. tostring(data.kind) .. G.GAME.round_resets.ante
        for _ = 1, math.min(tonumber(data.count) or 3, #candidates) do
            local key, i = pseudorandom_element(candidates, pseudoseed(seed))
            picks[#picks + 1] = key
            table.remove(candidates, i)
        end
        return 'jimbo_offers', { keys = picks }
    end)
end

-- A gift (the Dealer's): a voucher redeemed, a booster pack opened on the spot, or a joker or
-- consumable created, free.
local function on_grant(data, msg)
    link_from(data)
    local center = G.P_CENTERS[tostring(data.key)]
    if not center then error('no such card: ' .. tostring(data.key), 0) end
    local granted = false
    pend(msg, 'gift', function()
        if not stable() then return nil end
        if not granted then
            granted = true
            if center.set == 'Voucher' then
                G.GAME.used_vouchers[center.key] = true
                Card.apply_to_run(nil, center)
            elseif center.set == 'Booster' then
                -- Opened as a tag opens one.
                local card = Card(G.play.T.x + G.play.T.w / 2 - G.CARD_W * 1.27 / 2, G.play.T.y + G.play.T.h / 2 - G.CARD_H * 1.27 / 2,
                    G.CARD_W * 1.27, G.CARD_H * 1.27, G.P_CARDS.empty, center, { bypass_discovery_center = true, bypass_discovery_ui = true })
                card.cost = 0
                card.from_tag = true
                G.FUNCS.use_card({ config = { ref_table = card } })
                card:start_materialize()
            else
                local area = center.set == 'Joker' and G.jokers or G.consumeables
                local card = create_card(center.set, area, nil, nil, nil, nil, center.key, 'dealer')
                card:add_to_deck()
                area:emplace(card)
            end
            return nil
        end
        if center.set == 'Booster' and not dealer.in_pack() then return nil end
        if stable() then return reply_state() end
    end)
end

local function on_catalog(_, msg)
    local was = unlocked_all
    local items = cards.catalog()
    unlocked_all = was
    send('jimbo_catalog', { items = items, version = G.VERSION }, msg.id)
end

-- Balatro rules for Jimbo's fights --------------------------------------------------------------

local function reshuffle()
    exhaust_duds()
    if #G.discard.cards == 0 then return end
    for i = #G.discard.cards, 1, -1 do
        local card = G.discard:remove_card(G.discard.cards[i])
        if card then
            card.ability.discarded = nil
            G.deck:emplace(card)
        end
    end
    local f = fight()
    f.reshuffles = (f.reshuffles or 0) + 1
    G.deck:shuffle('jimbo_reshuffle' .. G.GAME.round_resets.ante .. '_' .. f.reshuffles)
end

-- A Jimbo fight never ends by itself: whatever Balatro thinks of the score, it keeps dealing.
local function end_round_override()
    if not in_combat() then return false end
    if G.STATE == G.STATES.NEW_ROUND then
        G.STATE = G.STATES.DRAW_TO_HAND
        G.STATE_COMPLETE = false
    elseif G.STATE == G.STATES.SELECTING_HAND and #G.hand.cards == 0 and #G.discard.cards > 0 then
        -- Everything was played or thrown away: shuffle the discards back in and deal again.
        G.STATE = G.STATES.DRAW_TO_HAND
        G.STATE_COMPLETE = false
    end
    return true
end

-- Jokers that count cards would count StS2's status duds too (they're Stone Cards in the deck), so
-- each counts with the duds taken out of the cards it looks at.
local DUD_BLIND = {
    ['Stone Joker'] = function() return G.playing_cards end,
    ["Driver's License"] = function() return G.playing_cards end,
    ['Erosion'] = function() return G.playing_cards end,
    ['Blue Joker'] = function() return G.deck and G.deck.cards end,
    ['Blackboard'] = function() return G.hand and G.hand.cards end,
}

-- Runs fn(...) with the duds taken out of `list`, then puts them back where they were.
local function without_duds(list, fn, ...)
    local taken = {}
    for i = #list, 1, -1 do
        if is_dud(list[i]) then taken[#taken + 1] = { i, table.remove(list, i) } end
    end
    if #taken == 0 then return fn(...) end
    local results = { pcall(fn, ...) }
    for k = #taken, 1, -1 do table.insert(list, taken[k][1], taken[k][2]) end
    if not results[1] then error(results[2], 0) end
    return unpack(results, 2, table.maxn(results))
end

local function dud_blind(card, fn, ...)
    local list_of = in_combat() and card.ability and DUD_BLIND[card.ability.name]
    local list = list_of and list_of()
    if not list then return fn(card, ...) end
    return without_duds(list, fn, card, ...)
end

-- What a won fight's end of round does before the cash-out (Gold Cards held, Rental, Blue Seals,
-- jokers' end-of-round words): Balatro shows it on the cards, which StS2 has put away by then, so
-- it joins the cash-out instead. Money is summed per kind of card; words become notes.
local function note_round_end(card, eval_type, amt, extra)
    local center = card.config and card.config.center
    local playing = card.ability and (card.ability.set == 'Default' or card.ability.set == 'Enhanced')
    local name = center and cards.name(center) or '?'
    if eval_type == 'dollars' and tonumber(amt) then
        local label = playing and name
            or (cards.loc({ type = 'name_text', set = 'Other', key = 'rental' }) or 'Rental') .. ': ' .. name
        for _, row in ipairs(cashing.paid) do
            if row.label == label then
                row.dollars, row.count = row.dollars + amt, row.count + 1
                return
            end
        end
        cashing.paid[#cashing.paid + 1] = { label = label, dollars = amt, count = 1 }
    elseif (eval_type == 'jokers' or eval_type == 'extra') and type(extra) == 'table' and type(extra.message) == 'string' then
        if extra.message == cards.loc('k_again_ex') then return end -- a retrigger; its payout is counted
        if playing and card.seal then
            name = cards.loc({ type = 'name_text', set = 'Other', key = string.lower(card.seal) .. '_seal' }) or name
        end
        cashing.notes[#cashing.notes + 1] = name .. ': ' .. extra.message
    end
end

local function install_rules()
    dealer.end_round_override = end_round_override
    dealer.in_combat = in_combat

    -- While a hand scores, jokers that count the round's discards left (Banner, Mystic Summit)
    -- count the fight's, as in Balatro, not the turn's share. Acrobat and Dusk ("final hand of
    -- round") work while the fight is in Overtime: no hands are left then, and some are before.
    -- The Rosetta Stone: Jimbo's StS2 Strength is Mult and his Vigor is Chips, added to the hand's
    -- base before anything else scores (so jokers' xMult multiplies them too). A hand's base Mult
    -- never drops below 1.
    local evaluate_play = G.FUNCS.evaluate_play
    G.FUNCS.evaluate_play = function(e)
        local f = fight()
        if not (f and f.blind_hands) then return evaluate_play(e) end
        local round = G.GAME.current_round
        local turn_hands, turn_discards = round.hands_left, round.discards_left
        local hands
        hands, round.discards_left = fight_left(true)
        round.hands_left = in_overtime() and 0 or math.max(1, hands)
        local bonus, charge, saved = dealer.play_bonus(), needle_multiplier(), {}
        if (bonus and (bonus.mult ~= 0 or bonus.chips ~= 0)) or charge > 1 then
            for key, hand in pairs(G.GAME.hands) do
                saved[key] = { hand.mult, hand.chips }
                if bonus then
                    hand.mult = hand.mult + math.max(bonus.mult, 1 - hand.mult)
                    hand.chips = hand.chips + math.max(bonus.chips, -hand.chips)
                end
                hand.mult = hand.mult * charge
            end
        end
        local ok, err = pcall(evaluate_play, e)
        for key, was in pairs(saved) do
            local hand = G.GAME.hands[key]
            hand.mult, hand.chips = was[1], was[2]
        end
        -- The Needle's charge is spent on this hand.
        if charge > 1 then f.needle_charge = 0 end
        round.hands_left, round.discards_left = turn_hands, turn_discards
        if not ok then error(err, 0) end
    end

    local needle_text = Blind.get_loc_debuff_text
    function Blind:get_loc_debuff_text()
        if self.name ~= 'The Needle' or not needle_live() then return needle_text(self) end
        local m = needle_multiplier()
        return 'Play only 1 hand. Each turn you hold it: x2, x3, x4 Mult' .. (m > 1 and (' [now x' .. m .. ']') or '')
    end
    dealer.preview_hook = function(out, key)
        local m = needle_multiplier()
        if m > 1 and type(out.mult) == 'number' then
            out.mult = out.mult * m
            out.charge = m
        end
        return out
    end

    -- Their words follow: "while in Overtime" for "final hand of round".
    local OVERTIME_WORDS = { j_dusk = true, j_acrobat = true }
    cards.words = function(key, text)
        if not (OVERTIME_WORDS[key] and dealer.jimbo_mode()) then return text end
        return (text:gsub('[io]n %[orange%]final%[/orange%]%s+%[orange%]hand%[/orange%] of round', 'while in [orange]Overtime[/orange]'))
    end

    -- A new turn tops Jimbo's hand up to its size. The Serpent's "always draw 3" belongs to
    -- Balatro's own draws after a hand or a discard; on a turn's refill it would overfill the hand.
    local draw_from_deck_to_hand = G.FUNCS.draw_from_deck_to_hand
    G.FUNCS.draw_from_deck_to_hand = function(e)
        local f = fight()
        if not (f and f.turn_refill) then return draw_from_deck_to_hand(e) end
        f.turn_refill = nil
        local round = G.GAME.current_round
        local played, used = round.hands_played, round.discards_used
        round.hands_played, round.discards_used = 0, 0 -- The Serpent only counts from the round's first hand or discard
        local ok, err = pcall(draw_from_deck_to_hand, e)
        round.hands_played, round.discards_used = played, used
        if not ok then error(err, 0) end
    end

    -- The Eye in Jimbo's fights: a hand can't be a type played in the last three turns (with a
    -- hand a turn, Balatro's four hands needing four different types), so a long fight never runs
    -- out of hands: a turn with nothing to play lets the oldest type back in. Its rule on the
    -- blind names the types it's holding back.
    local EYE_TURNS = 3
    local function eye_held(f)
        local now, held, seen = f.turn or 1, {}, {}
        for _, entry in ipairs(f.eye_recent or {}) do
            if now - entry.turn <= EYE_TURNS and not seen[entry.name] then
                seen[entry.name] = true
                held[#held + 1] = entry.name
            end
        end
        return held, seen
    end
    local debuff_hand = Blind.debuff_hand
    function Blind:debuff_hand(cards_in, hand, handname, check)
        local f = fight()
        if self.name ~= 'The Eye' or self.disabled or not (f and f.blind_hands) then
            return debuff_hand(self, cards_in, hand, handname, check)
        end
        local _, held = eye_held(f)
        self.triggered = held[handname] or false
        if held[handname] then return true end
        if not check then
            local kept = {}
            for _, entry in ipairs(f.eye_recent or {}) do
                if (f.turn or 1) - entry.turn <= EYE_TURNS then kept[#kept + 1] = entry end
            end
            kept[#kept + 1] = { name = handname, turn = f.turn or 1 }
            f.eye_recent = kept
        end
    end
    local get_loc_debuff_text = Blind.get_loc_debuff_text
    function Blind:get_loc_debuff_text()
        local f = fight()
        if self.name ~= 'The Eye' or self.disabled or not (f and f.blind_hands) then return get_loc_debuff_text(self) end
        local names = {}
        for _, name in ipairs((eye_held(f))) do names[#names + 1] = cards.loc(name, 'poker_hands') or name end
        return 'No repeat of a hand type from your last ' .. EYE_TURNS .. ' turns' .. (#names > 0 and (' [' .. table.concat(names, ', ') .. ']') or '')
    end

    local calculate_joker = Card.calculate_joker
    function Card:calculate_joker(context)
        -- A turn's refill isn't the start of a round: round-start jokers (Certificate) act once a fight.
        if context.first_hand_drawn and fight() and fight().first_drawn then return nil end
        return dud_blind(self, calculate_joker, context)
    end
    local update = Card.update
    function Card:update(dt)
        return dud_blind(self, update, dt)
    end
    local ability_table = Card.generate_UIBox_ability_table
    function Card:generate_UIBox_ability_table()
        return dud_blind(self, ability_table)
    end

    -- A Boss Blind disabled mid-fight (Luchador sold, say) gives back what it took: The Needle's
    -- hands and The Water's discards join the fight's totals (and so its turns), and StS2 sees the
    -- blind's new chips (The Wall, Violet Vessel) and shrinks the enemies to match.
    local disable = Blind.disable
    function Blind:disable(...)
        local out = disable(self, ...)
        local f = fight()
        if f and f.per_turn and f.blind_hands then
            if self.name == 'The Needle' then f.blind_hands = f.blind_hands + (self.hands_sub or 0) end
            if self.name == 'The Water' then f.blind_discards = f.blind_discards + (self.discards_sub or 0) end
            f.per_turn = share_of(f.blind_hands, f.blind_discards)
        end
        return out
    end

    -- The Wager: a raised Boss Blind's chips go up as it's set (and are saved with it).
    local set_blind = Blind.set_blind
    function Blind:set_blind(blind, reset, silent)
        local out = set_blind(self, blind, reset, silent)
        if not reset and blind and blind.boss and dealer.raised() then
            self.chips = self.chips * dealer.RAISE
            self.chip_text = number_format(self.chips)
        end
        return out
    end

    local status_text = card_eval_status_text
    card_eval_status_text = function(card, eval_type, amt, percent, dir, extra)
        if cashing and card and G.STATE == G.STATES.NEW_ROUND then
            local ok, err = pcall(note_round_end, card, eval_type, amt, extra)
            if not ok then log('end-of-round note failed: ' .. tostring(err)) end
        end
        return status_text(card, eval_type, amt, percent, dir, extra)
    end

    -- The pack is closing: the cards a tarot just changed are about to go back to the deck.
    local end_consumeable = G.FUNCS.end_consumeable
    G.FUNCS.end_consumeable = function(e, delayfac)
        if pack_effect and pack_effect.before and not pack_effect.after then
            pack_effect.after = hand_infos(pack_effect.used)
        end
        return end_consumeable(e, delayfac)
    end

    -- Duds stay debuffed whatever the blind recalculates.
    local set_debuff = Card.set_debuff
    function Card:set_debuff(should_debuff)
        if is_dud(self) or self.ability.jimbo_afflicted then should_debuff = true end
        return set_debuff(self, should_debuff)
    end

    local draw = G.FUNCS.draw_from_deck_to_hand
    G.FUNCS.draw_from_deck_to_hand = function(e)
        if in_combat() and not e and G.hand then
            local space = G.hand.config.card_limit - #G.hand.cards
            if #G.deck.cards < space then reshuffle() end
        end
        return draw(e)
    end

    -- The cash-out's lines, as Balatro adds them to its round summary.
    local add_row = add_round_eval_row
    add_round_eval_row = function(config)
        if cashing and config then
            if config.name == 'bottom' then
                cashing.total = config.dollars or 0
            else
                local label
                if config.name == 'blind1' and config.saved then
                    label = cards.loc('ph_mr_bones') or 'Saved by Mr. Bones'
                elseif config.name == 'blind1' then
                    label = cards.loc({ type = 'name_text', set = 'Blind', key = G.GAME.blind.config.blind.key }) or 'Blind'
                elseif config.name == 'hands' then
                    label = tostring(config.disp) .. ' ' .. (cards.loc('k_hands_left') or 'hands left')
                elseif config.name == 'discards' then
                    label = tostring(config.disp) .. ' ' .. (cards.loc('k_discards_left') or 'discards left')
                elseif config.name == 'interest' then
                    label = cards.loc({ type = 'variable', key = 'interest', vars = { G.GAME.interest_amount, 5, G.GAME.interest_amount * G.GAME.interest_cap / 5 } }) or 'Interest'
                elseif config.card then
                    label = cards.name(config.card.config.center)
                elseif config.tag then
                    label = config.tag.name
                end
                cashing.rows[#cashing.rows + 1] = { name = config.name, label = label or config.name, dollars = config.dollars or 0 }
            end
        end
        return add_row(config)
    end
end

-- Install and update -----------------------------------------------------------------------

function jimbo.install(bridge)
    send = bridge.send
    log = bridge.log
    local h = bridge.handlers
    h.jimbo_link = on_link
    h.jimbo_state = on_state
    h.jimbo_new_run = on_new_run
    h.jimbo_blind = on_blind
    h.jimbo_turn = on_turn
    h.jimbo_win = on_win
    h.jimbo_cash_out = on_cash_out
    h.jimbo_buy = on_buy
    h.jimbo_sell = on_sell
    h.jimbo_use = on_use
    h.jimbo_pick = on_pick
    h.jimbo_skip_pack = on_skip_pack
    h.jimbo_highlight = on_highlight
    h.jimbo_move_joker = on_move_joker
    h.jimbo_dollars = on_dollars
    h.jimbo_deck = on_deck
    h.jimbo_stats = on_stats
    h.jimbo_catalog = on_catalog
    h.jimbo_offers = on_offers
    h.jimbo_grant = on_grant
    h.jimbo_skip = on_skip
    h.jimbo_raise = on_raise
    h.jimbo_open_shop = on_open_shop
    h.jimbo_duds = on_duds
    dealer.extend_snapshot = extend_snapshot
    dealer.jimbo_update = jimbo.update
    dealer.jimbo_waiting = function() return #requests > 0 end
    install_rules()
end

function jimbo.reset()
    requests = {}
end

-- Runs the oldest waiting request's step, and times out ones that never get there.
function jimbo.update()
    local r = requests[1]
    if not r then return end
    local ok, kind, data = pcall(r.step)
    if not ok then
        table.remove(requests, 1)
        log('jimbo ' .. r.label .. ' failed: ' .. tostring(kind))
        send('error', { message = tostring(kind) }, r.id)
    elseif kind then
        table.remove(requests, 1)
        send(kind, data, r.id)
    elseif now() - r.since > r.timeout then
        table.remove(requests, 1)
        dealer.describe_situation(r.id, r.since)
        dealer.rescue_stalled_link(r.id)
        send('error', { message = 'Balatro never got to the ' .. r.label }, r.id)
    end
end

return jimbo
