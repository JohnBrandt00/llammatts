-- event-logger.lua: passive plugin that watches every generation in the app.
-- It defines event hooks instead of a run() entry point. The host calls
-- on_generate_start / on_generate_complete for ALL generations (web UI or plugins).

local count = 0
local total_seconds = 0

function on_generate_start(ev)
    log.info("generation started: \"" .. string.sub(ev.text or "", 1, 60) .. "\" (source: " .. (ev.source or "?") .. ")")
end

function on_generate_complete(ev)
    count = count + 1
    total_seconds = total_seconds + (ev.seconds or 0)
    log.info(string.format("generation #%d complete: %s (%.1fs audio, %.1fs total this session)",
        count, ev.name or "?", ev.seconds or 0, total_seconds))
end

function run()
    log.info(string.format("session stats: %d generations, %.1fs of audio", count, total_seconds))
end
