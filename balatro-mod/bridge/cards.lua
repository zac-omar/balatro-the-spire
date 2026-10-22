-- Describes Balatro's cards for Slay the Spire 2: which sprites to draw, and the card's text in
-- StS2's rich-text markup. Shared by the dealer (bridge/dealer.lua) and Jimbo (bridge/jimbo.lua).

local cards = {}

-- localize() with a fallback, since a missing key returns 'ERROR' and odd keys can throw.
function cards.loc(...)
    local ok, text = pcall(localize, ...)
    if ok and type(text) == 'string' and text ~= 'ERROR' and text ~= '' then return text end
    return nil
end
local loc = cards.loc

function cards.hex(colour)
    if type(colour) ~= 'table' or not colour[3] then return nil end
    local function byte(v) return math.max(0, math.min(255, math.floor(v * 255 + 0.5))) end
    return string.format('%02X%02X%02X', byte(colour[1]), byte(colour[2]), byte(colour[3]))
end
local hex = cards.hex

-- Sprites ------------------------------------------------------------------------------------

-- A sprite cell in an atlas; `used` collects the atlases a snapshot needs StS2 to know about.
function cards.sprite(atlas, pos, used)
    if not atlas or not pos then return nil end
    used[atlas] = true
    return { atlas = atlas, x = pos.x, y = pos.y }
end
local sprite = cards.sprite

function cards.center_atlas(center)
    return (G.ASSET_ATLAS[center.atlas or center.set] or G.ASSET_ATLAS.centers).name
end

-- Every atlas in `used`, as { name = { path, px, py } }.
function cards.atlases(used)
    local out = {}
    -- Blind chips live with Balatro's animated atlases; their first frame is what StS2 draws.
    for _, list in ipairs({ G.asset_atli or {}, G.animation_atli or {} }) do
        for _, def in ipairs(list) do
            if used[def.name] then out[def.name] = { path = def.path, px = def.px, py = def.py } end
        end
    end
    return out
end

function cards.edition_of(card)
    if not card.edition then return nil end
    for _, e in ipairs({ 'foil', 'holo', 'polychrome', 'negative' }) do
        if card.edition[e] then return e end
    end
    return nil
end

-- Text ---------------------------------------------------------------------------------------

-- Balatro's text colours as StS2's markup tags (the rest become [color=#hex]).
local TAGS = {
    mult = 'red', red = 'red', chips = 'blue', blue = 'blue', money = 'gold', gold = 'gold',
    attention = 'orange', green = 'green', purple = 'purple', tarot = 'purple', planet = 'aqua',
    spectral = 'blue', edition = 'aqua', dark_edition = 'purple', legendary = 'pink', enhanced = 'orange',
}

local function coloured(text, tag, colour)
    if tag then return '[' .. tag .. ']' .. text .. '[/' .. tag .. ']' end
    if colour then return '[color=#' .. colour .. ']' .. text .. '[/color]' end
    return text
end

-- One parsed line of a Balatro description (loc_parse_string's output) in StS2 markup.
local function line_text(parsed, vars)
    local out = {}
    for _, part in ipairs(parsed) do
        local text = ''
        for _, sub in ipairs(part.strings) do
            if type(sub) == 'string' then
                text = text .. sub
            else
                local v = vars and vars[tonumber(sub[1])]
                text = text .. (v ~= nil and tostring(v) or '?')
            end
        end
        -- Square brackets in the text itself (Yorick's "[23]") would read as markup tags.
        text = text:gsub('[%[%]]', { ['['] = '[lb]', [']'] = '[rb]' })
        local control = part.control or {}
        local tag, colour
        if control.V and vars and vars.colours then
            colour = hex(vars.colours[tonumber(control.V)])
        elseif control.X then
            -- Boxed text, like X2 Mult on its red tab: coloured like its box.
            tag = TAGS[control.X]
            if not tag then colour = hex(loc_colour(control.X)) end
        elseif control.C then
            tag = TAGS[control.C]
            if not tag and control.C ~= 'white' then colour = hex(loc_colour(control.C)) end
        end
        out[#out + 1] = coloured(text, tag, colour)
    end
    return table.concat(out)
end

-- A parsed part as it reads with its vars as placeholders ('+#1#' reads '+9').
local function shape(part)
    local out = {}
    for _, sub in ipairs(part.strings) do out[#out + 1] = type(sub) == 'string' and sub or '9' end
    return table.concat(out)
end

-- A coloured number, like '+15' or a boxed 'X3'.
local function is_value(part)
    local control = part.control or {}
    return (control.C or control.X) and shape(part):match('^%s*[%+%-X%$]?%s*%d') ~= nil
end

-- Balatro breaks its text into short lines for its little tooltips and StS2 wraps text itself,
-- so lines join with spaces, except where Balatro puts something new on a line of its own: a
-- status line ("(Currently +7 Mult)"), small print, an animated warning (Mr. Bones' "self
-- destructs"), or a second effect after a whole one ("+15 Mult", then "1 in 6 chance...").
local function starts_new(parsed, previous)
    local first = parsed[1]
    if not first then return false end
    if shape(first):match('^%s*%(') then return true end
    local small = true
    for _, part in ipairs(parsed) do
        local scale = part.control and tonumber(part.control.s)
        if not (scale and scale < 1) then small = false break end
    end
    if small then return true end
    if #parsed == 1 and first.control and tostring(first.control.E) == '2' then return true end
    if previous and #previous == 2 and is_value(previous[1]) and is_value(first) then
        local unit = previous[2].control or {}
        local word = shape(previous[2]):gsub(',%s*$', '')
        return not (unit.C or unit.X) and not word:match('^%s*%S+%s+%S')
    end
    return false
end

-- A parsed part as it reads with its vars filled in, without markup.
local function rendered(part, vars)
    local out = {}
    for _, sub in ipairs(part.strings) do
        if type(sub) == 'string' then
            out[#out + 1] = sub
        else
            local v = vars[tonumber(sub[1])]
            out[#out + 1] = v ~= nil and tostring(v) or '?'
        end
    end
    return table.concat(out)
end

-- Jokers that wear down rather than build up: their text has no "(Currently ...)" line, and
-- the number it leads with is the one that changes (the var it's in).
local WEARS_DOWN = { j_ice_cream = 1, j_popcorn = 1, j_ramen = 1, j_selzer = 1, j_turtle_bean = 1 }

-- The number a joker that changes shows right now, as its text writes it ("+7", "X2.2", "$4",
-- "0/2"): its "(Currently ...)" line's value, or the number a wearing-down joker leads with.
-- Nil for a joker whose numbers never change. Read from the text's structure, so it works in
-- any language.
function cards.live_value(key, vars)
    local group = G.localization.descriptions.Joker
    local entry = group and group[key]
    if not (entry and entry.text_parsed and vars) then return nil end
    local want = WEARS_DOWN[key]
    for _, parsed in ipairs(entry.text_parsed) do
        local status = not want and parsed[1] and shape(parsed[1]):match('^%s*%(') ~= nil
        for i, part in ipairs(parsed) do
            local index
            for _, sub in ipairs(part.strings) do
                if type(sub) ~= 'string' then index = tonumber(sub[1]) break end
            end
            if index and (status or index == want) then
                local text = rendered(part, vars)
                if status then text = text:gsub('^%s*%(%D*', '') end -- "(Currently +7" reads "+7"
                -- "(Currently 0/2)": a count out of a total reads as one value.
                local after = parsed[i + 1] and rendered(parsed[i + 1], vars)
                if after and after:match('^/') then text = text .. after:gsub('[%)%s]+$', '') end
                text = text:gsub('^%s+', ''):gsub('[%s%)]+$', '')
                return text ~= '' and text or nil
            end
        end
    end
    return nil
end

-- A description in StS2 markup: Balatro's localized text for `set`/`key`, with `vars` filled in.
function cards.text(set, key, vars)
    local group = G.localization.descriptions[set]
    local entry = group and group[key]
    if not (entry and entry.text_parsed) then return nil end
    local out, previous = {}, nil
    for _, parsed in ipairs(entry.text_parsed) do
        if previous then
            if starts_new(parsed, previous) then
                out[#out] = out[#out]:gsub(',%s*$', '') -- "+250 Chips," ends its clause here
                out[#out + 1] = '\n'
            else
                out[#out + 1] = ' '
            end
        end
        out[#out + 1] = line_text(parsed, vars or {})
        previous = parsed
    end
    return (table.concat(out):gsub('  +', ' '):gsub(' *\n *', '\n'))
end

-- Runs `generate` (something that builds a card's tooltip) and returns the vars Balatro
-- localizes `key`'s description with. Balatro works these out in one long chain per card, so
-- watching localize() is the reliable way to get them, current values included.
local function captured_vars(key, generate)
    local original = localize
    local vars
    localize = function(args, misc)
        if vars == nil and type(args) == 'table' and (args.type == 'descriptions' or args.type == 'other') and args.key == key then
            vars = args.vars or {}
        end
        return original(args, misc)
    end
    local ok, err = pcall(generate)
    localize = original
    if not ok then return nil, err end
    return vars
end

-- A tag's text with Balatro's own values (a Speed Tag's payout so far, an Orbital Tag's hand),
-- built the way the blind select builds it: for the blind it's skipped from, or for the
-- collection with no blind. Building a Tag bumps two counters that only number tags; they're
-- put back.
function cards.tag_text(key, blind_type)
    local tally, id = G.GAME.tag_tally, G.tagid
    local ok, tag = pcall(Tag, key, blind_type == nil, blind_type)
    local vars = ok and tag and captured_vars(key, function() tag:get_uibox_table({}) end) or nil
    G.GAME.tag_tally, G.tagid = tally, id
    return cards.text('Tag', key, vars or {})
end

local EDITION_KEYS = { foil = 'e_foil', holo = 'e_holo', polychrome = 'e_polychrome', negative = 'e_negative' }

-- A card's own text right now: a joker's current mult, a planet's level and so on. Debuffs and
-- discovery don't hide it (StS2 shows those itself). Edition and stickers go on extra lines.
function cards.describe(card)
    local center = card.config and card.config.center
    if not center then return nil end
    -- Packs share one description per kind and size, kept with Balatro's 'Other' texts.
    local set, key = center.set, center.key
    if set == 'Booster' then set, key = 'Other', key:gsub('_%d+$', '') end
    local debuff, bypass_ui, bypass_lock = card.debuff, card.bypass_discovery_ui, card.bypass_lock
    card.debuff, card.bypass_discovery_ui, card.bypass_lock = false, true, true
    local vars = captured_vars(key, function() card:generate_UIBox_ability_table() end)
    card.debuff, card.bypass_discovery_ui, card.bypass_lock = debuff, bypass_ui, bypass_lock

    local text = cards.text(set, key, vars or center.vars or {})
    local extras = {}
    local edition = cards.edition_of(card)
    if edition then
        local e = G.P_CENTERS[EDITION_KEYS[edition]]
        local name = loc({ type = 'name_text', set = 'Edition', key = EDITION_KEYS[edition] }) or edition
        local evars = edition == 'foil' and { e.config.extra } or edition == 'holo' and { e.config.extra }
            or edition == 'polychrome' and { e.config.extra } or { 1 }
        extras[#extras + 1] = '[aqua]' .. name .. ':[/aqua] ' .. (cards.text('Edition', EDITION_KEYS[edition], evars) or ''):gsub('\n', ' ')
    end
    for _, sticker in ipairs({ 'eternal', 'perishable', 'rental' }) do
        if card.ability[sticker] then
            local name = loc({ type = 'name_text', set = 'Other', key = sticker }) or sticker
            local svars = sticker == 'perishable' and { card.ability.perishable_rounds or 5, card.ability.perish_tally or 5 }
                or sticker == 'rental' and { G.GAME.rental_rate or 3 } or {}
            extras[#extras + 1] = '[orange]' .. name .. ':[/orange] ' .. (cards.text('Other', sticker, svars) or ''):gsub('\n', ' ')
        end
    end
    if #extras > 0 then text = (text and text .. '\n' or '') .. table.concat(extras, '\n') end
    return text, vars
end

-- Whether a consumable can be used now. Balatro's own check also says no while anything sits
-- in the play area (an opened booster pack does, for a moment) or input is locked; the bridge
-- only asks once Balatro has settled, so those are skipped. `any_state` as in Balatro (packs,
-- the shop).
function cards.usable(card, any_state)
    return card:can_use_consumeable(any_state, true) and true or false
end

-- A card's name, as Balatro shows it.
function cards.name(center)
    if center.set == 'Booster' then
        local entry = G.localization.descriptions.Other[center.key:gsub('_%d+$', '')]
        return entry and entry.name or center.name
    end
    return loc({ type = 'name_text', set = center.set, key = center.key }) or center.name
end

-- Snapshots ----------------------------------------------------------------------------------

-- A card as StS2 draws it: its sprites, what it is, and (with `describe`) its text.
function cards.card_info(card, used, describe)
    local center = card.config.center
    local info = {
        id = card.sort_id,
        facing = card.facing,
        debuff = card.debuff and true or false,
        edition = cards.edition_of(card),
        center = sprite(cards.center_atlas(center), center.pos, used),
        set = card.ability.set,
    }
    if card.ability.set == 'Default' or card.ability.set == 'Enhanced' then
        local front_atlas, front_pos = get_front_spriteinfo(card.config.card)
        info.front = sprite(front_atlas and front_atlas.name, front_pos, used)
        info.rank = card.base.value
        info.suit = card.base.suit
        info.chips = card.base.nominal
        info.stone = card.ability.effect == 'Stone Card'
        -- The suits it counts as (Wild Cards, Smeared Joker), debuffed or not.
        info.suits = {}
        for _, suit in ipairs({ 'Spades', 'Hearts', 'Clubs', 'Diamonds' }) do
            if card:is_suit(suit, true) then info.suits[#info.suits + 1] = suit end
        end
        info.name = (loc(card.base.value, 'ranks') or card.base.value) .. ' of ' ..
            (loc(card.base.suit, 'suits_plural') or card.base.suit)
        if center.key ~= 'c_base' then
            info.enhancement = loc({ type = 'name_text', set = 'Enhanced', key = center.key }) or center.name
            info.key = center.key
        end
        if card.seal then
            local seal = G.shared_seals[card.seal]
            info.seal = card.seal
            info.seal_sprite = seal and sprite('centers', seal.sprite_pos, used)
        end
        if describe then info.text = cards.playing_card_text(card) end
        local dud = card.ability.jimbo_dud
        if dud then
            -- A status card from the fight (see jimbo.lua): StS2's name and words for it.
            info.dud = dud.ref
            info.name = dud.name
            info.text = dud.text
        end
    else
        info.key = center.key
        info.name = cards.name(center)
        if center.soul_pos then info.soul = sprite(info.center.atlas, center.soul_pos, used) end
        if describe then
            local vars
            info.text, vars = cards.describe(card)
            if card.ability.set == 'Joker' then info.live = cards.live_value(center.key, vars) end
        end
        info.sell = card.sell_cost
        info.eternal = card.ability.eternal or nil
        info.perishable = card.ability.perishable and (card.ability.perish_tally or 0) or nil
        info.rental = card.ability.rental or nil
    end
    return info
end

-- A playing card's chips and what its enhancement and seal do.
function cards.playing_card_text(card)
    local lines = {}
    local chips = card:get_chip_bonus()
    if card.ability.effect ~= 'Stone Card' then
        lines[#lines + 1] = '[blue]+' .. tostring(chips) .. '[/blue] chips'
    end
    if card.config.center.key ~= 'c_base' then
        local vars = captured_vars(card.config.center.key, function() card:generate_UIBox_ability_table() end)
        local text = cards.text('Enhanced', card.config.center.key, vars or {})
        if text then lines[#lines + 1] = text end
    end
    if card.seal then
        local key = string.lower(card.seal) .. '_seal'
        local text = cards.text('Other', key, {})
        if text then lines[#lines + 1] = text end
    end
    return table.concat(lines, '\n')
end

-- Catalog ------------------------------------------------------------------------------------

local CATALOG_SETS = { Joker = true, Voucher = true, Back = true, Tarot = true, Planet = true, Spectral = true }

-- Every item's name and text with its starting values, for StS2's relic, potion and event text
-- before (or without) a run: the jokers, vouchers, decks, consumables, stakes, tags and blinds.
function cards.catalog()
    local items = {}
    for key, center in pairs(G.P_CENTERS) do
        if CATALOG_SETS[center.set] and not center.omit then
            local vars
            local unlocked, discovered = center.unlocked, center.discovered
            center.unlocked, center.discovered = true, true
            if center.set == 'Back' then
                vars = captured_vars(key, function() Back(center):generate_UI() end)
            else
                local card = Card(0, 0, G.CARD_W, G.CARD_H, nil, center,
                    { bypass_discovery_center = true, bypass_discovery_ui = true, bypass_back = G.GAME.selected_back.pos })
                card.bypass_lock = true
                vars = captured_vars(key, function() card:generate_UIBox_ability_table() end)
                card:remove()
            end
            center.unlocked, center.discovered = unlocked, discovered
            items[key] = { set = center.set, name = cards.name(center), text = cards.text(center.set, key, vars or {}) }
        end
    end
    for key, stake in pairs(G.P_STAKES) do
        items[key] = { set = 'Stake', name = loc({ type = 'name_text', set = 'Stake', key = key }) or stake.name,
            text = cards.text('Stake', key, {}) }
    end
    for key, tag in pairs(G.P_TAGS) do
        items[key] = { set = 'Tag', name = loc({ type = 'name_text', set = 'Tag', key = key }) or tag.name,
            text = cards.tag_text(key) }
    end
    for key, blind in pairs(G.P_BLINDS) do
        items[key] = { set = 'Blind', name = loc({ type = 'name_text', set = 'Blind', key = key }) or blind.name,
            text = cards.text('Blind', key, blind.vars or {}) }
    end
    return items
end

return cards
