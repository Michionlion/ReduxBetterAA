Test.name("Production DLAA remains enabled through stock cloud TUS")
local aa = Test.mod.extension("ReduxBetterAA")
local beta = Test.mod.extension("ReduxBetterAA.Beta")
local original = beta.settings()
Test.report.value("original-settings", original)
local function capture(label)
    local before = beta.snapshot()
    Test.assert.false_(before.test_cloud_guard_suppressed, "No test-only guard override")
    Test.report.value(label .. "-before", before)
    Test.report.value(label .. "-cloud", aa.dlaa_cloud_transition_snapshot())
    Test.report.value(label .. "-stock-cloud", aa.cloud_renderer_snapshot())
    if before.selected == "NVIDIA DLAA" then
        Test.assert.true_(before.active, "Production DLAA active")
        Test.assert.false_(aa.dlaa_cloud_transition_snapshot().compatibilityBypassActive, "Production guard is disabled")
        Test.assert.equal(aa.dlaa_cloud_transition_snapshot().settleFramesRemaining, 0, "No suspension countdown")
    end
    Test.assert.true_(beta.report(), "Capture accepted: " .. label)
    Test.wait["until"](function() return not beta.snapshot().report_busy end, 180)
    Test.report.value("buffers-" .. label, beta.snapshot().report_zip)
    Test.report.value(label .. "-after", beta.snapshot())
    if before.selected == "NVIDIA DLAA" then
        Test.assert.greater(beta.snapshot().true_dlaa_input_captures, before.true_dlaa_input_captures,
            "Actual DLAA input captured at backend entry")
    end
end
local ok, err = pcall(function()
    beta.set_settings({_modeEntry="Off", _sharpnessEntry=0, _dlaaPresetEntry="M"})
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight", 60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
    Test.render.wait_stable(120)
    aa.select_camera("FlightCameraPhysics_Main")
    capture("flight-Off")
    beta.set_settings({_modeEntry="NVIDIA DLAA"})
    Test.render.wait_stable(240)
    Test.assert.equal(beta.snapshot().selected, "NVIDIA DLAA", "Requested DLAA did not fall back")
    capture("flight-DLAA")
    Test.game.unpause()
    for frame=1,60 do
        Test.camera.orbit {distance=45, yaw=frame*0.5, pitch=35, fov=55}
        Test.wait.frames(1)
    end
    Test.render.wait_stable(120)
    capture("flight-DLAA-after-pan")
end)
beta.set_settings(original)
if not ok then error(err) end
Test.report.note("Production payload, no guard override. Pair the runtime assertions with external numeric comparison of same-frame EXR input/output. This test verifies continued AA execution, not resolution of the original cloud-disappearance defect.")
