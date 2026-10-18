-- Reads Balatro's global game state (G) into the plain-table snapshot the bridge reports to
-- Slay the Spire 2, and derives coarse game events from state transitions.
-- Field meanings are documented in protocol/PROTOCOL.md.

local state = {}

local function key_of(map, value)
    if map == nil or value == nil then return nil end
    for k, v in pairs(map) do
        if v == value then return k end
    end
    return nil
end

local function blind_info(blind)
    if not blind or not blind.name or blind.name == '' then return nil end
    return {
        name = blind.name,
        chips = blind.chips,
        dollars = blind.dollars,
        boss = blind.boss and true or false,
    }
end

local function in_run()
    return G and G.STAGES and G.STAGE == G.STAGES.RUN and G.GAME ~= nil
end

function state.snapshot()
    if not (G and G.STATES and G.STAGES) then return { stage = 'BOOTING' } end
    local snap = {
        stage = key_of(G.STAGES, G.STAGE),
        state = key_of(G.STATES, G.STATE),
    }
    if in_run() then
        local game = G.GAME
        local round = game.current_round or {}
        local resets = game.round_resets or {}
        snap.run = {
            seed = game.pseudorandom and game.pseudorandom.seed,
            seeded = game.seeded and true or false,
            stake = game.stake,
            deck = game.selected_back and game.selected_back.name,
            ante = resets.ante,
            round = game.round,
            dollars = game.dollars,
            chips = game.chips,
            hands_left = round.hands_left,
            discards_left = round.discards_left,
            won = game.won and true or false,
            blind = blind_info(game.blind),
        }
    end
    return snap
end

-- Event detection ----------------------------------------------------------------------------

local last_state, last_won, last_blind

-- Returns the list of events that happened since the previous call. Call once per frame.
function state.poll_events()
    local events = {}
    if not (G and G.STATES) then return events end

    local current = G.STATE
    local running = in_run()
    local game = running and G.GAME or nil

    -- The blind is captured while it is live because Balatro clears it once the round ends.
    if game then last_blind = blind_info(game.blind) or last_blind end

    if current ~= last_state then
        local from, to = key_of(G.STATES, last_state), key_of(G.STATES, current)
        last_state = current
        if game then
            local resets = game.round_resets or {}
            if from == 'BLIND_SELECT' and (to == 'DRAW_TO_HAND' or to == 'SELECTING_HAND') then
                events[#events + 1] = { name = 'blind_selected', blind = blind_info(game.blind) }
            elseif to == 'ROUND_EVAL' then
                events[#events + 1] = {
                    name = 'blind_defeated', blind = last_blind, chips = game.chips,
                    ante = resets.ante, round = game.round,
                }
            elseif to == 'SHOP' then
                events[#events + 1] = { name = 'shop_entered', ante = resets.ante, dollars = game.dollars }
            elseif to == 'GAME_OVER' then
                events[#events + 1] = {
                    name = 'game_over', ante = resets.ante, round = game.round, chips = game.chips,
                }
            end
        end
    end

    local won = game and game.won and true or false
    if game and won and not last_won then
        events[#events + 1] = { name = 'run_won', ante = (game.round_resets or {}).ante, round = game.round }
    end
    last_won = won

    return events
end

return state
