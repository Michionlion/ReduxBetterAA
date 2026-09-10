Test.name("Better AA: mode lifecycle and resource recovery")
local aa = Test.mod.extension("ReduxBetterAA")
local beta = Test.mod.extension("ReduxBetterAA.Beta")
local original = beta.settings()
local modes = {
    {"Off", "Off"}, {"FXAA Low", "FXAA Low"}, {"FXAA High", "FXAA High"},
    {"SMAA", "SMAA"}, {"TAA", "Custom TAA"},
    {"NVIDIA DLAA", "NVIDIA DLAA"}, {"FSR 2 Native AA", "FSR2 Native AA"}
}
local function verify(mode, label)
    local state = beta.snapshot()
    Test.assert.equal(state.selected, mode[2], "Selected backend: " .. label)
    Test.assert.equal(state.active, mode[1] ~= "Off", "Active state: " .. label)
    Test.assert.equal(state.lost_targets, 0, "No lost targets: " .. label)
    Test.report.value(label, state)
end
local function report(label, temporal)
    local count = beta.snapshot().temporal_input_captures
    Test.assert.true_(beta.report(), "Report accepted: " .. label)
    Test.wait["until"](function() return not beta.snapshot().report_busy end, 180)
    Test.report.value("report-" .. label, beta.snapshot().report_zip)
    if temporal then
        Test.assert.equal(beta.snapshot().temporal_input_captures, count + 1, "One production input capture")
    end
end
local ok, err = pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight", 60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
    beta.set_settings({_dlaaPresetEntry="M", _sharpnessEntry=0.15, _mapViewAaEntry=true})
    for index, mode in ipairs(modes) do
        beta.set_settings({_modeEntry=mode[1]})
        Test.render.wait_stable(120)
        local label = "mode-" .. index
        verify(mode, label)
        Test.capture.screenshot(label)
        if index == 1 or index >= 5 then report(label, index >= 5) end
        if index >= 5 then
            local targets = beta.snapshot().owned_targets
            Test.assert.greater(beta.release_owned_targets(), 0, "Release active targets")
            Test.render.wait_stable(120)
            verify(mode, label .. "-recovered")
            Test.assert.equal(beta.snapshot().owned_targets, targets, "Recreated the full resource set")
            Test.capture.screenshot(label .. "-recovered")
        end
    end
    for cycle = 1, 3 do
        for _, mode in ipairs(modes) do
            beta.set_settings({_modeEntry=mode[1]})
            Test.render.wait_stable(15)
            verify(mode, "cycle-" .. cycle .. "-" .. mode[1])
        end
    end
    beta.set_settings({_modeEntry="NVIDIA DLAA", _mapViewAaEntry=false})
    aa.open_map_view()
    Test.game.wait_for_state("Map3DView", 30)
    Test.render.wait_stable(90)
    verify(modes[1], "map-off-override")
    Test.assert.equal(beta.settings()._modeEntry, "NVIDIA DLAA", "Flight selection retained")
    beta.set_settings({_mapViewAaEntry=true})
    Test.render.wait_stable(90)
    verify(modes[6], "map-dlaa-restored")
    Test.capture.screenshot("map-dlaa-restored")
end)
beta.set_settings(original)
if not ok then error(err) end
Test.report.note("All public modes, three switch cycles, forced loss of owned textures in TAA/DLAA/FSR2, map override and four production issue reports. Requires both vendor runtimes. Settings restored. Inspect images and validate ZIP hashes separately; no performance claim.")
