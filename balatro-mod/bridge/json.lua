-- Minimal JSON encoder/decoder for the Balatro the Spire bridge protocol.
--
-- Encoding: tables whose keys are exactly 1..n encode as arrays; every other table (including
-- empty ones) encodes as an object. Non-finite numbers encode as the strings "inf", "-inf"
-- and "nan" because JSON has no representation for them. Use json.null for an explicit null.
--
-- Decoding: JSON null becomes Lua nil, so null object members are simply absent.

local json = {}

json.null = setmetatable({}, { __tostring = function() return 'null' end })

local ESCAPES = {
    ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f',
    ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t',
}

local UNESCAPES = {
    ['"'] = '"', ['\\'] = '\\', ['/'] = '/', b = '\b', f = '\f', n = '\n', r = '\r', t = '\t',
}

local function encode_string(s)
    return '"' .. s:gsub('[%c"\\]', function(c)
        return ESCAPES[c] or string.format('\\u%04x', c:byte())
    end) .. '"'
end

local function encode_number(n)
    if n ~= n then return '"nan"' end
    if n == math.huge then return '"inf"' end
    if n == -math.huge then return '"-inf"' end
    if n % 1 == 0 and math.abs(n) < 2^53 then return string.format('%d', n) end
    return string.format('%.17g', n)
end

local function array_length(t)
    local n = 0
    for k in pairs(t) do
        if type(k) ~= 'number' or k < 1 or k % 1 ~= 0 then return nil end
        n = n + 1
    end
    if n == 0 then return nil end
    for i = 1, n do
        if t[i] == nil then return nil end
    end
    return n
end

local encode_value

local function encode_table(t, seen)
    if seen[t] then error('json: cannot encode a cyclic table', 0) end
    seen[t] = true
    local out = {}
    local n = array_length(t)
    if n then
        for i = 1, n do out[i] = encode_value(t[i], seen) end
        seen[t] = nil
        return '[' .. table.concat(out, ',') .. ']'
    end
    for k, v in pairs(t) do
        local tv = type(v)
        if tv ~= 'function' and tv ~= 'userdata' and tv ~= 'thread' then
            out[#out + 1] = encode_string(tostring(k)) .. ':' .. encode_value(v, seen)
        end
    end
    seen[t] = nil
    return '{' .. table.concat(out, ',') .. '}'
end

encode_value = function(v, seen)
    if v == nil or v == json.null then return 'null' end
    local tv = type(v)
    if tv == 'boolean' then return v and 'true' or 'false' end
    if tv == 'number' then return encode_number(v) end
    if tv == 'string' then return encode_string(v) end
    if tv == 'table' then return encode_table(v, seen) end
    error('json: cannot encode a value of type ' .. tv, 0)
end

function json.encode(v)
    return encode_value(v, {})
end

local function decode_error(i, msg)
    error(string.format('json: %s at position %d', msg, i), 0)
end

local function skip_whitespace(s, i)
    return s:find('[^ \t\r\n]', i) or #s + 1
end

local function utf8_char(cp)
    local floor = math.floor
    if cp < 0x80 then return string.char(cp) end
    if cp < 0x800 then
        return string.char(0xC0 + floor(cp / 0x40), 0x80 + cp % 0x40)
    end
    if cp < 0x10000 then
        return string.char(0xE0 + floor(cp / 0x1000), 0x80 + floor(cp / 0x40) % 0x40, 0x80 + cp % 0x40)
    end
    return string.char(0xF0 + floor(cp / 0x40000), 0x80 + floor(cp / 0x1000) % 0x40,
        0x80 + floor(cp / 0x40) % 0x40, 0x80 + cp % 0x40)
end

local function decode_string(s, start)
    local parts, i = {}, start + 1
    while true do
        local c = s:sub(i, i)
        if c == '' then decode_error(start, 'unterminated string') end
        if c == '"' then return table.concat(parts), i + 1 end
        if c == '\\' then
            local e = s:sub(i + 1, i + 1)
            if UNESCAPES[e] then
                parts[#parts + 1] = UNESCAPES[e]
                i = i + 2
            elseif e == 'u' then
                local cp = tonumber(s:sub(i + 2, i + 5), 16) or decode_error(i, 'bad unicode escape')
                i = i + 6
                if cp >= 0xD800 and cp <= 0xDBFF and s:sub(i, i + 1) == '\\u' then
                    local low = tonumber(s:sub(i + 2, i + 5), 16)
                    if low and low >= 0xDC00 and low <= 0xDFFF then
                        cp = 0x10000 + (cp - 0xD800) * 0x400 + (low - 0xDC00)
                        i = i + 6
                    end
                end
                parts[#parts + 1] = utf8_char(cp)
            else
                decode_error(i, 'bad escape sequence')
            end
        else
            local stop = s:find('["\\]', i) or #s + 1
            parts[#parts + 1] = s:sub(i, stop - 1)
            i = stop
        end
    end
end

local function decode_number(s, i)
    local text = s:match('^-?%d+%.?%d*[eE]?[-+]?%d*', i)
    local n = text and tonumber(text)
    if not n then decode_error(i, 'bad number') end
    return n, i + #text
end

local decode_value

local function decode_object(s, i)
    local obj = {}
    i = skip_whitespace(s, i + 1)
    if s:sub(i, i) == '}' then return obj, i + 1 end
    while true do
        if s:sub(i, i) ~= '"' then decode_error(i, 'expected a string key') end
        local key
        key, i = decode_string(s, i)
        i = skip_whitespace(s, i)
        if s:sub(i, i) ~= ':' then decode_error(i, "expected ':'") end
        obj[key], i = decode_value(s, i + 1)
        i = skip_whitespace(s, i)
        local c = s:sub(i, i)
        if c == '}' then return obj, i + 1 end
        if c ~= ',' then decode_error(i, "expected ',' or '}'") end
        i = skip_whitespace(s, i + 1)
    end
end

local function decode_array(s, i)
    local arr, n = {}, 0
    i = skip_whitespace(s, i + 1)
    if s:sub(i, i) == ']' then return arr, i + 1 end
    while true do
        n = n + 1
        arr[n], i = decode_value(s, i)
        i = skip_whitespace(s, i)
        local c = s:sub(i, i)
        if c == ']' then return arr, i + 1 end
        if c ~= ',' then decode_error(i, "expected ',' or ']'") end
        i = i + 1
    end
end

decode_value = function(s, i)
    i = skip_whitespace(s, i)
    local c = s:sub(i, i)
    if c == '{' then return decode_object(s, i) end
    if c == '[' then return decode_array(s, i) end
    if c == '"' then return decode_string(s, i) end
    if c == '-' or c:match('%d') then return decode_number(s, i) end
    if s:sub(i, i + 3) == 'true' then return true, i + 4 end
    if s:sub(i, i + 4) == 'false' then return false, i + 5 end
    if s:sub(i, i + 3) == 'null' then return nil, i + 4 end
    decode_error(i, 'unexpected character')
end

function json.decode(s)
    local v, i = decode_value(s, 1)
    i = skip_whitespace(s, i)
    if i <= #s then decode_error(i, 'unexpected trailing data') end
    return v
end

return json
