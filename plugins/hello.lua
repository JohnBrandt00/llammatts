-- hello.lua: simplest possible plugin.
-- Press "Run" in the web UI (or POST /api/plugins/hello/run) to execute run().

function run()
    log.info("hello plugin starting")

    local result = tts.speak("Hello! This speech was produced by a Lua script running inside the Llama T T S host.")

    log.info("wrote " .. result.name .. " (" .. string.format("%.1f", result.seconds) .. "s)")
end
