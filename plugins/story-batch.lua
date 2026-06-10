-- story-batch.lua: generate several lines with different sampler settings
-- and join them into a single wav with a short pause between lines.

local lines = {
    { text = "Chapter one. The fog rolled in from the harbor just after midnight." },
    { text = "Nobody saw the small boat slip its mooring and drift out with the tide.", temperature = 0.3 },
    { text = "By morning, the only trace left behind was a single wet rope on the dock.", temperature = 0.5 },
}

function run()
    local parts = {}
    local pause = audio.silence(0.4, "story-pause.wav")

    for i, line in ipairs(lines) do
        log.info("line " .. i .. " of " .. #lines)
        local result = tts.generate{
            text = line.text,
            speaker = "default",
            temperature = line.temperature, -- nil falls back to the default 0.4
        }
        table.insert(parts, result.file)
        if i < #lines then
            table.insert(parts, pause)
        end
    end

    local out = audio.concat(parts, "story.wav")
    log.info("story assembled: " .. out)
end
