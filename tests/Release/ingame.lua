-- The pipeline supplies separate NVIDIA and modern AMD runtime expectations.
Test.name("Better AA release candidate")
Test.assert.true_(type(release_fixture) == "string", "A save fixture is configured")
Test.assert.true_(type(release_native) == "boolean", "The runtime phase is configured")
Test.assert.true_(type(release_amd_runtime) == "boolean", "The modern AMD runtime expectation is configured")
Test.assert.true_(Test.mod.is_loaded("ReduxBetterAA"), "Better AA loaded from the candidate ZIP")
local aa = Test.mod.extension("ReduxBetterAA")
Test.assert.not_equal(aa, nil, "The external harness supplies the Better AA adapter")
local initial = aa.release_status()
local capabilities = { dlaa = initial.dlaa, fsr2 = initial.fsr2, fsrProvider = initial.fsrProvider or "runtime-selected" }
Test.report.value("native", release_native)
Test.report.value("amdRuntimeInstalled", release_amd_runtime)
Test.report.value("capabilities", capabilities)
if not release_native then
    Test.assert.false_(initial.dlaa, "DLAA is unavailable without native libraries")
else
    if not initial.dlaa then Test.report.note("DLAA unavailable on this hardware; fallback is tested.") end
end
Test.assert.equal(initial.fsr2, release_amd_runtime, "AMD availability requires the separate modern FSR bundle")
if release_amd_runtime then
    if initial.fsrProvider then
        Test.assert.true_(initial.fsrProvider == "FSR 3.1" or initial.fsrProvider == "FSR 4.1", "AMD reports the actual modern provider")
    else
        Test.report.note("This harness does not expose the provider name; the production diagnostic validator requires FSR 3.1 or FSR 4.1 on every AMD capture.")
    end
elseif release_native then
    Test.report.note("NVIDIA-only runtime phase: AMD-unavailable behavior is covered, but modern AMD execution is not tested.")
end

local captures = {}
local modes = {
    {"Off", "Off"}, {"FxaaLow", "FXAA Low"}, {"FxaaHigh", "FXAA High"},
    {"Smaa", "SMAA"}, {"CustomTaa", "Custom TAA"},
    {"NvidiaDlaa", initial.dlaa and "NVIDIA DLAA" or "Off"},
    {"AmdFsr2", initial.fsr2 and (initial.fsrProvider and (initial.fsrProvider .. " Native AA") or "AMD Native AA") or "Off"},
    {"Supersampling", "Supersampling"}
}
local function record_capture(scene, requested, expected, label)
    Test.wait.seconds(expected == "Off" and requested ~= "Off" and 7 or 1.5)
    Test.render.wait_stable(60)
    Test.assert.true_(aa.request_capture(), "Capture " .. scene .. "/" .. (label or requested))
    Test.wait.frames(8)
    Test.wait.until_(function() return not aa.release_status().captureBusy end, 30)
    captures[#captures + 1] = { scene = scene, requested = requested, expected = expected,
        mapEnabled = aa.release_status().mapEnabled, label = label or requested }
    Test.report.value("captures", captures)
end
local function capture(scene, requested, expected, label)
    aa.set_backend(requested)
    record_capture(scene, requested, expected, label)
end
local function cycle(scene)
    for _, mode in ipairs(modes) do
        local expected = mode[2]
        if scene ~= "FlightView" and mode[1] == "Supersampling" then expected = "Off" end
        capture(scene, mode[1], expected)
    end
    capture(scene, "Off", "Off", "cleanup")
end
local function flight()
    aa.set_backend("Off")
    Test.game.load_save(release_fixture)
    Test.game.wait_for_state("Flight", 90)
    Test.game.pause()
    Test.assert.equal(aa.select_camera("FlightCameraPhysics_Main"), "FlightCameraPhysics_Main", "Flight camera discovered")
end

local ok, failure = pcall(function()
    aa.set_map_enabled(true)
    Test.game.wait_for_state("MainMenu", 45)
    Test.assert.equal(aa.select_camera("Camera.Scaled"), "Camera.Scaled", "Menu camera discovered")
    if not release_native then
        record_capture("MainMenu", "CustomTaa", "Custom TAA", "new-install")
    end
    cycle("MainMenu")
    flight()
    cycle("FlightView")
    capture("FlightView", "4", "Custom TAA", "legacy-mode")
    capture("FlightView", "999", "Off", "unknown-mode")

    aa.open_map_view()
    Test.game.wait_for_state("Map3DView", 45)
    Test.assert.equal(aa.select_camera("MapCamera"), "MapCamera", "Map camera discovered")
    cycle("Map3DView")
    aa.set_map_enabled(false)
    capture("Map3DView", "CustomTaa", "Off", "map-disabled")
    aa.set_map_enabled(true)
    capture("Map3DView", "CustomTaa", "Custom TAA", "map-restored")

    flight()
    capture("FlightView", "CustomTaa", "Custom TAA", "reload")
    Test.game.unpause()
    Test.wait.seconds(3)
    capture("FlightView", "CustomTaa", "Custom TAA", "unpaused")
    Test.assert.true_(aa.request_issue_report(), "The production Issue ZIP capture starts")
    Test.wait.until_(function() return not aa.release_status().issueBusy end, 90)
    local issue = aa.release_status().issueZip
    Test.assert.true_(type(issue) == "string" and #issue > 0, "Issue ZIP completes")
    Test.report.value("issueZip", Test.report.attach(issue))
    Test.capture.screenshot("flight-ui", { hideUI = false })
    capture("FlightView", "Off", "Off", "final-cleanup")
end)
aa.set_backend("Off")
aa.set_map_enabled(initial.mapEnabled)
if not ok then error(failure) end
Test.report.note("Automated state and capture checks do not establish moving image quality, UI crispness, GPU performance or long-session stability; review the release checklist separately.")
