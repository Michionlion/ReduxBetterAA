Test.name("Native AA pixel comparison and separate performance windows")
local q = Test.mod.extension("ReduxBetterAA.Quality")
local original = q.settings()
local ok, err = pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight", 60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    q.set({_modeEntry="Off", _sharpnessEntry=0})
    Test.render.set("supersampling", 2.0)
    Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
    Test.render.wait_stable(120)
    q.reference()
    q.capture("reference-stationary", 4)
    Test.wait["until"](function() return q.ready() end, 120)
    Test.render.set("supersampling", 1.0)
    for _, mode in ipairs({"TAA", "FSR 2 Native AA", "NVIDIA DLAA"}) do
        q.set({_modeEntry=mode, _sharpnessEntry=0, _taaStabilityEntry=0.99, _dlaaPresetEntry="M"})
        Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
        Test.render.wait_stable(120)
        local selected = mode == "TAA" and "Custom TAA" or (mode == "FSR 2 Native AA" and "FSR2 Native AA" or mode)
        Test.assert.equal(q.selected(), selected, "Backend executes without fallback")
        local label = mode == "TAA" and "custom" or (mode == "NVIDIA DLAA" and "dlaa" or "fsr")
        -- Capture consecutive actual rendered frames. GPU readback time is excluded from profiles.
        q.capture(label .. "-stationary", 16)
        Test.wait["until"](function() return q.ready() end, 120)
        for step=1,8 do
            Test.camera.orbit {distance=45, yaw=step*0.5, pitch=35, fov=55}
            q.capture(label .. "-pan-" .. string.format("%02d", step), 1)
            Test.wait["until"](function() return q.ready() end, 60)
        end
        q.capture(label .. "-settle", 16)
        Test.wait["until"](function() return q.ready() end, 120)
        Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
        Test.render.wait_stable(120)
        for repeatIndex=1,3 do
            q.profile_start()
            Test.wait["until"](function() return q.profile_ready() end, 120)
            local profile = q.profile()
            Test.assert.equal(profile.State, "Complete", "Timing completed without fallback")
            Test.report.value(label .. "-profile-" .. repeatIndex, profile)
        end
    end
end)
q.set(original)
if not ok then error(err) end
Test.report.note("Paused launchpad, 100% native scale required, sharpness zero, DLAA M. Consecutive stationary/settle sequences and sampled pan poses. Clouds remain stock and can evolve between runs. Pairwise differences are not ground-truth quality scores. Timing excludes capture readbacks; GPU values are whole-frame, CPU resolve is submission only.")
