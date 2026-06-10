-- clone-demo.lua: clone a voice from a reference wav, then speak with it.
--
-- Put a clean 5-15 second wav of one person speaking into the output directory
-- (data/output/my-voice.wav) and set the transcript below to exactly what they say.
-- Tip: you can also clone from the Speakers tab in the web UI.

local reference_wav = "my-voice.wav"           -- relative to the output dir
local transcript = "Replace this with the exact words spoken in the reference recording."

function run()
    local ok, err = pcall(function()
        local voice = speakers.clone{
            audio = reference_wav,
            transcript = transcript,
            name = "lua-cloned-voice",
        }
        log.info("cloned speaker '" .. voice.name .. "' covering " .. voice.words .. " words")

        local result = tts.generate{
            text = "This is my cloned voice, speaking from a Lua plugin.",
            speaker = voice,
        }
        log.info("spoke with cloned voice: " .. result.name)
    end)

    if not ok then
        log.error("clone failed: " .. tostring(err))
        log.info("hint: drop a short wav at data/output/" .. reference_wav .. " and edit the transcript in this script")
    end
end
