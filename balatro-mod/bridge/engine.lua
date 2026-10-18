-- Runs Balatro as Slay the Spire 2's invisible card engine.
--
-- When StS2 launches Balatro with BALATRO_BRIDGE_HIDDEN=1, the window is hidden, it is
-- silent, the game runs at 16x speed without drawing, the splash screen and tutorial are
-- skipped, and Clubs face cards use the Slay the Spire collab art. StS2 draws the table itself
-- from the data the dealer sends (see bridge/dealer.lua).

local window = require('bridge.window')

local engine = {
    hidden = os.getenv('BALATRO_BRIDGE_HIDDEN') == '1',
    host_hwnd = tonumber(os.getenv('BALATRO_BRIDGE_HOST_HWND') or ''),
}

local HIDDEN_WIDTH, HIDDEN_HEIGHT = 320, 180
local FPS_CAP = 60

local sdl
do
    local ok, ffi = pcall(require, 'ffi')
    if ok then
        pcall(ffi.cdef, [[
            typedef struct SDL_Window SDL_Window;
            SDL_Window *SDL_GL_GetCurrentWindow(void);
            void SDL_HideWindow(SDL_Window *window);
        ]])
        local loaded, lib = pcall(ffi.load, 'SDL2')
        if loaded then sdl = lib end
    end
end

local function hide_window()
    if not sdl then return end
    pcall(function()
        local w = sdl.SDL_GL_GetCurrentWindow()
        if w ~= nil then sdl.SDL_HideWindow(w) end
    end)
    -- Showing the window may have pulled focus away from Slay the Spire 2; hand it back.
    if engine.host_hwnd then window.focus(engine.host_hwnd) end
end

local function apply_settings()
    local settings = G.SETTINGS
    settings.SOUND.volume = 0
    -- Balatro's fastest menu option is 4. Nobody watches a hidden engine, and its event timers
    -- scale with this while card movement stays real-time, so it can go much faster.
    settings.GAMESPEED = 16
    settings.skip_splash = 'Yes'
    settings.tutorial_complete = true
    settings.tutorial_progress = nil
    if settings.CUSTOM_DECK and settings.CUSTOM_DECK.Collabs then
        settings.CUSTOM_DECK.Collabs.Clubs = 'collab_STS'
    end
    G.FPS_CAP = FPS_CAP
end

function engine.install()
    if not engine.hidden then return end

    hide_window()

    -- Balatro applies its saved window mode (possibly fullscreen) during start-up; keep it
    -- small, windowed and hidden whatever it asks for.
    for _, name in ipairs({ 'setMode', 'updateMode' }) do
        local original = love.window[name]
        love.window[name] = function(_, _, flags)
            flags = flags or {}
            flags.fullscreen = false
            flags.borderless = true
            flags.resizable = false
            local results = { original(HIDDEN_WIDTH, HIDDEN_HEIGHT, flags) }
            hide_window()
            return unpack(results)
        end
    end

    -- Settings are loaded from disk at the start of Game:start_up, just before the splash.
    local splash_screen = G.splash_screen
    G.splash_screen = function(self, ...)
        apply_settings()
        return splash_screen(self, ...)
    end

    -- Nobody can see the window, so skip rendering entirely.
    love.draw = function() end

    -- The engine is silent: StS2 plays Balatro's music and card sounds itself, through its own
    -- audio output. Gameplay sounds never start, and the music plays at zero volume.
    play_sound = function() end
end

return engine
