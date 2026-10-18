-- Win32 window focus through LuaJIT's FFI, so Balatro can hand focus back to Slay the Spire 2.
-- Every function is a harmless no-op when the FFI or Windows isn't available.

local window = {}

local SW_RESTORE = 9

local ok, ffi = pcall(require, 'ffi')
local C
if ok and ffi.os == 'Windows' then
    -- ffi.C covers kernel32 and user32 on Windows. pcall in case another mod declared these first.
    pcall(ffi.cdef, [[
        int SetForegroundWindow(void *hwnd);
        int ShowWindow(void *hwnd, int cmd);
        int IsIconic(void *hwnd);
        unsigned long GetCurrentProcessId(void);
    ]])
    C = ffi.C
end

-- This process's id, so the host can find and focus our window. nil when unavailable.
function window.pid()
    if not C then return nil end
    local got, pid = pcall(function() return tonumber(C.GetCurrentProcessId()) end)
    return got and pid or nil
end

-- Brings the window with the given handle to the front. Windows only allows this while our
-- process owns the foreground, which it does right after the player played a hand here.
function window.focus(hwnd)
    if not C or not hwnd then return false end
    local done, focused = pcall(function()
        local h = ffi.cast('void *', hwnd)
        if C.IsIconic(h) ~= 0 then C.ShowWindow(h, SW_RESTORE) end
        return C.SetForegroundWindow(h) ~= 0
    end)
    return done and focused
end

return window
