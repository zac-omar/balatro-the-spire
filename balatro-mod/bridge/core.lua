-- Balatro the Spire's bridge, Balatro side.
--
-- Connects to the BalatroBridge mod running inside Slay the Spire 2 over localhost TCP and
-- speaks newline-delimited JSON (see protocol/PROTOCOL.md). Everything runs on the game
-- thread from love.update using non-blocking sockets, so a missing or slow host never
-- stalls Balatro.
--
-- Injected into main.lua by tools/build_balatro.py as: require('bridge.core').install()

local socket = require('socket')
local json = require('bridge.json')
local state = require('bridge.state')
local dealer = require('bridge.dealer')
local jimbo = require('bridge.jimbo')
local engine = require('bridge.engine')
local window = require('bridge.window')

local Bridge = {
    VERSION = '0.3.0',
    PROTOCOL = 2,
    HOST = '127.0.0.1',
    DEFAULT_PORT = 47291,
    RETRY_SECONDS = 1,
    CONNECT_TIMEOUT = 1,
    STATE_INTERVAL = 0.25,
    LOG_FILE = 'bridge.log',
    LOG_HISTORY = 10, -- earlier sessions' logs kept as bridge-<date>-<time>.log
    -- A hidden engine quits when its host is gone, so it can't linger invisibly.
    ORPHAN_SECONDS = 20,
    FIRST_CONTACT_SECONDS = 120,
}

local sock             -- connected socket, or nil
local pending          -- socket with a connect in progress, or nil
local pending_since = 0
local retry_at = 0
local inbuf, outbuf = '', ''
local next_id = 1
local last_state_json
local last_state_sent = 0
local last_error
local started_at
local last_contact_at  -- last time we were connected, or nil if never

-- Request handlers by message type: function(data, msg). Other bridge modules add their own.
Bridge.handlers = {}
local handlers = Bridge.handlers

local function now()
    return love.timer.getTime()
end

local function log(msg)
    local line = string.format('[%s] %s\n', os.date('%H:%M:%S'), msg)
    pcall(love.filesystem.append, Bridge.LOG_FILE, line)
    print('[bridge] ' .. msg)
end
Bridge.log = log

-- Moves the last session's log to bridge-<date>-<time>.log and drops all but the newest few.
local function keep_old_logs()
    local fs = love.filesystem
    local info = fs.getInfo(Bridge.LOG_FILE)
    if info then
        local stamp = os.date('%Y%m%d-%H%M%S', info.modtime or os.time())
        fs.write('bridge-' .. stamp .. '.log', fs.read(Bridge.LOG_FILE) or '')
    end
    local old = {}
    for _, name in ipairs(fs.getDirectoryItems('')) do
        if name:match('^bridge%-%d+%-%d+%.log$') then old[#old + 1] = name end
    end
    table.sort(old)
    for i = 1, #old - Bridge.LOG_HISTORY do fs.remove(old[i]) end
    fs.remove('bridge.prev.log') -- the old single backup
end

-- Balatro's save thread may not get to write before the process exits, so the last stable save
-- (Balatro only saves at stable points) is written here directly.
local function save_before_quit()
    -- save_run stores a copy of the game; it must be this run's (a new run has none at first).
    local snapshot = G and G.ARGS and G.ARGS.save_run
    if not (snapshot and snapshot.GAME and G.STAGE == G.STAGES.RUN and G.GAME and compress_and_save) then return end
    if snapshot.GAME.bolatro_run ~= G.GAME.bolatro_run then return end
    compress_and_save(G.SETTINGS.profile .. '/save.jkr', snapshot)
    log('saved the run before quitting')
end

local function disconnect(reason)
    if sock then
        sock:close()
        log('disconnected: ' .. tostring(reason))
    end
    sock = nil
    inbuf, outbuf = '', ''
    last_state_json = nil
    retry_at = now() + Bridge.RETRY_SECONDS
    dealer.reset()
    jimbo.reset()
end

local function flush()
    while sock and #outbuf > 0 do
        local last, err, partial = sock:send(outbuf)
        local sent = last or partial or 0
        if sent > 0 then outbuf = outbuf:sub(sent + 1) end
        if err == 'timeout' then return end -- OS buffer full; the rest goes out next frame
        if err then
            disconnect('send failed: ' .. err)
            return
        end
    end
end

-- Queues a message for the host. Returns its id, or nil when not connected.
function Bridge.send(msg_type, data, re)
    if not sock then return nil end
    local id = next_id
    next_id = next_id + 1
    local ok, line = pcall(json.encode, { type = msg_type, id = id, re = re, data = data or {} })
    if not ok then
        log('could not encode ' .. msg_type .. ': ' .. tostring(line))
        return nil
    end
    outbuf = outbuf .. line .. '\n'
    flush()
    return id
end

function Bridge.is_connected()
    return sock ~= nil
end

-- Message handlers (host -> Balatro) ---------------------------------------------------------

handlers.hello = function(data)
    log(string.format('handshake: %s %s, bridge %s, protocol %s', tostring(data.game),
        tostring(data.game_version), tostring(data.bridge_version), tostring(data.protocol)))
    if data.protocol ~= Bridge.PROTOCOL then
        log('WARNING: host speaks protocol ' .. tostring(data.protocol) .. ', we speak ' .. Bridge.PROTOCOL)
    end
    last_state_json = nil -- push a fresh snapshot right away
end

handlers.ping = function(data, msg)
    Bridge.send('pong', data, msg.id)
end

handlers.get_state = function(_, msg)
    Bridge.send('state', state.snapshot(), msg.id)
end

handlers.start_run = function(data, msg)
    if not (G.STAGE == G.STAGES.MAIN_MENU or G.STAGE == G.STAGES.RUN) then
        error('cannot start a run while Balatro is still loading', 0)
    end
    local stake = tonumber(data.stake) or 1
    if stake < 1 or stake > 8 or stake % 1 ~= 0 then error('stake must be a whole number from 1 to 8', 0) end
    local seed = data.seed and tostring(data.seed):upper() or nil
    -- Same steps as the main menu's Play button (G.FUNCS.start_setup_run).
    if G.OVERLAY_MENU then G.FUNCS.exit_overlay_menu() end
    G.FUNCS.start_run(nil, { stake = stake, seed = seed })
    Bridge.send('ack', { stake = stake, seed = seed }, msg.id)
end

handlers.quit = function(_, msg)
    Bridge.send('ack', {}, msg.id)
    flush()
    local saved, err = pcall(save_before_quit)
    if not saved then log('saving before quitting failed: ' .. tostring(err)) end
    love.event.quit()
end

handlers.error = function(data)
    log('host reported an error: ' .. tostring(data.message))
end

local function dispatch(line)
    local ok, msg = pcall(json.decode, line)
    if not ok or type(msg) ~= 'table' or type(msg.type) ~= 'string' then
        log('ignored malformed message: ' .. line:sub(1, 200))
        return
    end
    local handler = handlers[msg.type]
    if not handler then
        if msg.id then Bridge.send('error', { message = 'unknown message type: ' .. msg.type }, msg.id) end
        return
    end
    local data = type(msg.data) == 'table' and msg.data or {}
    if msg.type ~= 'ping' then log('<- ' .. msg.type .. ' #' .. tostring(msg.id)) end
    local handled, err = pcall(handler, data, msg)
    if not handled then
        log(msg.type .. ' failed: ' .. tostring(err))
        if msg.id then Bridge.send('error', { message = tostring(err) }, msg.id) end
    end
end

-- Connection management ----------------------------------------------------------------------

local function on_connected(s)
    sock = s
    sock:setoption('tcp-nodelay', true)
    inbuf, outbuf = '', ''
    log('connected to host at ' .. Bridge.HOST .. ':' .. Bridge.port)
    Bridge.send('hello', {
        game = 'balatro',
        game_version = G and G.VERSION,
        bridge_version = Bridge.VERSION,
        protocol = Bridge.PROTOCOL,
        pid = window.pid(),
    })
end

local function start_connect()
    local s = socket.tcp()
    s:settimeout(0)
    local ok, err = s:connect(Bridge.HOST, Bridge.port)
    if ok then
        on_connected(s)
    elseif err == 'timeout' or err == 'Operation already in progress' then
        pending, pending_since = s, now()
    else
        s:close()
        retry_at = now() + Bridge.RETRY_SECONDS
    end
end

-- Windows reports a refused non-blocking connect only through select()'s error set, which
-- LuaSocket doesn't expose, so an attempt that hasn't become writable in time counts as failed.
local function poll_pending()
    local _, writable = socket.select(nil, { pending }, 0)
    if writable and writable[pending] and pending:getpeername() then
        local s = pending
        pending = nil
        on_connected(s)
        return
    end
    if now() - pending_since < Bridge.CONNECT_TIMEOUT then return end
    pending:close()
    pending = nil
    retry_at = now() + Bridge.RETRY_SECONDS
end

local function receive()
    while sock do
        local line, err, partial = sock:receive('*l', inbuf)
        if line then
            inbuf = ''
            if #line > 0 then dispatch(line) end
        elseif err == 'timeout' then
            inbuf = partial or ''
            return
        else
            disconnect('receive failed: ' .. tostring(err))
            return
        end
    end
end

local function push_state()
    if now() - last_state_sent < Bridge.STATE_INTERVAL then return end
    last_state_sent = now()
    local snap = state.snapshot()
    local encoded = json.encode(snap)
    if encoded ~= last_state_json then
        last_state_json = encoded
        Bridge.send('state', snap)
    end
end

local function quit_if_orphaned()
    if not engine.hidden then return end
    local alone_for = now() - (last_contact_at or started_at)
    local limit = last_contact_at and Bridge.ORPHAN_SECONDS or Bridge.FIRST_CONTACT_SECONDS
    if alone_for > limit then
        log('no Slay the Spire 2 host for ' .. math.floor(alone_for) .. 's; quitting')
        love.event.quit()
    end
end

function Bridge.update()
    local events = state.poll_events() -- runs while disconnected too, so stale events never replay
    if sock then
        last_contact_at = now()
        receive()
        for _, ev in ipairs(events) do Bridge.send('event', ev) end
    end
    -- Isolated so a dealer problem can't stop messages flowing.
    local dealt, err = pcall(dealer.update)
    if not dealt and err ~= last_error then
        last_error = err
        log('dealer error: ' .. tostring(err))
    end
    if sock then
        push_state()
        flush()
    elseif pending then
        poll_pending()
    else
        if now() >= retry_at then start_connect() end
        quit_if_orphaned()
    end
end

-- Wraps love.update and love.quit. Call once, after main.lua has defined them.
function Bridge.install()
    Bridge.port = tonumber(os.getenv('BALATRO_BRIDGE_PORT') or '') or Bridge.DEFAULT_PORT
    -- Keep earlier sessions' logs (one may hold a crash or a stall) before starting a fresh one.
    pcall(keep_old_logs)
    pcall(love.filesystem.write, Bridge.LOG_FILE, '')
    log('Balatro the Spire bridge ' .. Bridge.VERSION .. ' installed; host expected on port ' .. Bridge.port ..
        (engine.hidden and ' (hidden engine mode)' or ''))
    started_at = now()
    engine.install()
    dealer.install(Bridge, engine.hidden)
    jimbo.install(Bridge)

    local update = love.update
    love.update = function(dt)
        update(dt)
        local ok, err = pcall(Bridge.update)
        if not ok and err ~= last_error then
            last_error = err
            log('update error: ' .. tostring(err))
        end
    end

    -- A crash in a modded build must never reach Balatro's developer as a crash report. And a
    -- hidden engine's crash screen would sit invisibly forever, so log the error, tell the host
    -- and exit; the host relaunches it on the next hand.
    G.F_CRASH_REPORTS = false
    local errhand = love.errhand
    love.errhand = function(msg)
        G.F_CRASH_REPORTS = false
        msg = tostring(msg)
        log('CRASH: ' .. msg .. '\n' .. debug.traceback('', 2))
        if sock then
            Bridge.send('event', { name = 'engine_crashed', message = msg })
            flush()
        end
        if engine.hidden then return end
        return errhand(msg)
    end

    local quit = love.quit
    love.quit = function(...)
        local abort = quit and quit(...)
        if not abort and sock then
            Bridge.send('event', { name = 'balatro_quitting' })
            flush()
            sock:close()
            sock = nil
        end
        return abort
    end

    return Bridge
end

return Bridge
