Test.name("Better AA: mode lifecycle and resource recovery")
local aa = Test.mod.extension("ReduxBetterAA")
local beta = Test.mod.extension("ReduxBetterAA.Beta")
local original = beta.settings()
local modes = {
    {"Off", "Off"}, {"FXAA Low", "FXAA Low"}, {"FXAA High", "FXAA High"},
    {"SMAA", "SMAA"}, {"TAA", "Custom TAA"},
    {"NVIDIA DLAA", "NVIDIA DLAA"}, {"FSR 2 Native AA", "FSR2 Native AA"},
    {"Supersampling", "Supersampling"}
}
local function verify(mode, label)
    local state = beta.snapshot()
    Test.assert.equal(state.selected, mode[2], "Selected backend: " .. label)
    Test.assert.equal(state.active, mode[1] ~= "Off", "Active state: " .. label)
    Test.assert.equal(state.lost_targets, 0, "No lost targets: " .. label)
    Test.assert.equal(state.msaa, 0, "No stacked MSAA: " .. label)
    if mode[1] == "Off" then
        Test.assert.equal(state.owned_targets, 0, "Off releases temporal targets")
        Test.assert.equal(state.temporal_hooks, 0, "Off detaches temporal hooks")
        Test.assert.false_(state.foliage_enabled, "Off releases foliage override")
        Test.assert.equal(state.scene_width, state.screen_width, "Off is native resolution")
    end
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
        if index == 1 or (index >= 5 and index <= 7) then report(label, index >= 5) end
        if index >= 5 and index <= 7 then
            local targets = beta.snapshot().owned_targets
            Test.assert.greater(beta.release_owned_targets(), 0, "Release active targets")
            Test.render.wait_stable(120)
            verify(mode, label .. "-recovered")
            Test.assert.equal(beta.snapshot().owned_targets, targets, "Recreated the full resource set")
            Test.capture.screenshot(label .. "-recovered")
        end
    end
    local stock_scale = beta.snapshot().stock_scale
    for _, scale in ipairs({125, 150, 175, 200}) do
        beta.set_settings({_modeEntry="Supersampling", _supersamplingEntry=scale})
        Test.render.wait_stable(90)
        local state = beta.snapshot()
        verify(modes[8], "supersampling-" .. scale)
        Test.assert.equal(state.render_scale, scale, "Selected supersampling scale")
        Test.assert.equal(state.scene_width, math.ceil(state.screen_width * scale / 100), "Actual supersampled target")
        Test.assert.equal(state.stock_scale, stock_scale, "Stock saved scale preserved")
        Test.assert.equal(state.owned_targets, 0, "Supersampling has no temporal histories")
        Test.assert.false_(state.foliage_enabled, "Supersampling does not install foliage repair")
        Test.capture.screenshot("supersampling-" .. scale)
        beta.set_settings({_modeEntry="Off"})
        Test.render.wait_stable(15)
        verify(modes[1], "off-after-supersampling-" .. scale)
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
    beta.set_settings({_modeEntry="Supersampling", _supersamplingEntry=150})
    Test.wait["until"](function() return string.find(beta.snapshot().status, "fallback") ~= nil end, 10)
    verify(modes[1], "map-supersampling-fallback")
    Test.assert.equal(beta.settings()._modeEntry, "Supersampling", "Unsupported map retains flight supersampling")
    beta.set_settings({_mapViewAaEntry=false})
    Test.render.wait_stable(30)
    verify(modes[1], "map-supersampling-off")
    Test.assert.equal(beta.settings()._modeEntry, "Supersampling", "Map Off retains supersampling selection")
end)
beta.set_settings(original)
if not ok then error(err) end
Test.report.note("All public modes, three switch cycles, forced loss of owned textures in TAA/DLAA/FSR2, map override and four production issue reports. Requires both vendor runtimes. Settings restored. Inspect images and validate ZIP hashes separately; no performance claim.")
