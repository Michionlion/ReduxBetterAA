Test.name("Better AA: compact DLAA flight capture")
local aa = Test.mod.extension("ReduxBetterAA")
local beta = Test.mod.extension("ReduxBetterAA.Beta")
Test.assert.not_equal(beta, nil, "Install the test-only visual adapter")
local original = beta.settings()
local function record(label)
    local state = beta.snapshot()
    Test.report.value(label, state)
    Test.assert.equal(state.selected, "NVIDIA DLAA", "DLAA selected: " .. label)
    Test.assert.true_(state.active, "DLAA active: " .. label)
    Test.capture.screenshot(label, {hideUI = false, waitFrames = 1})
end
local ok, err = pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight", 60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    beta.set_settings({_modeEntry = "NVIDIA DLAA", _dlaaPresetEntry = "M", _sharpnessEntry = 0.15, _mapViewAaEntry = true})
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    for frame = 1, 6 do
        Test.camera.orbit {distance = 45, yaw = (frame - 1) * 6, pitch = 35, fov = 55}
        Test.render.wait_stable(90)
        record(string.format("flight-dlaa-%02d", frame))
    end
    Test.camera.orbit {distance = 45, yaw = 0, pitch = 35, fov = 55}
    Test.render.wait_stable(90)
    Test.report.value("video-start", beta.snapshot())
    beta.video_start()
    for frame = 1, 180 do
        Test.camera.orbit {distance = 45, yaw = (frame - 1) * 30 / 179, pitch = 35, fov = 55}
        beta.video_frame()
        Test.wait["until"](function() return beta.video_ready() end, 30)
        local state = beta.snapshot()
        if state.selected ~= "NVIDIA DLAA" or not state.active then error("DLAA stopped during video") end
    end
    Test.report.value("video", beta.video_finish())
    Test.report.value("video-end", beta.snapshot())
    Test.report.value("video-format", "180 sampled presented frames, 30 FPS, 6 seconds, native resolution, H.264 CRF 16 medium preset; no frame-pacing claim")
    beta.video_dispose()
    aa.open_map_view()
    Test.game.wait_for_state("Map3DView", 30)
    Test.render.wait_stable(90)
    record("map-dlaa-icons")
end)
beta.video_dispose()
beta.set_settings(original)
if not ok then error(err) end
Test.report.note("Six settled DLAA M launchpad views, a six-second sampled camera-pan MP4, and one map view. The video is encoded directly without retaining a PNG sequence. It is not a real-time performance recording. Original settings restored.")
