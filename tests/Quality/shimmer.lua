Test.name("Consecutive native TAA shimmer and response measurements")
local q=Test.mod.extension("ReduxBetterAA.Quality")
local original=q.settings()
local ok,err=pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight",60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    q.panel(false)
    for _,arm in ipairs({
        {"reference","Off",2}, {"custom","TAA",1},
        {"motion","TAA",1,.90}, {"edges","TAA",1,.10,0},
        {"reactive","TAA",1,.10,.75,0},
        {"response4","TAA",1,.10,.75,2,4}, {"response2","TAA",1,.10,.75,2,2},
        {"response1","TAA",1,.10,.75,2,1}, {"response05","TAA",1,.10,.75,2,.5},
        {"fsr","FSR 2 Native AA",1}, {"dlaa","NVIDIA DLAA",1}
    }) do
      if #arm==3 or q.shimmer_sweep() then
        Test.render.set("supersampling",arm[3])
        q.set({_modeEntry=arm[2],_sharpnessEntry=.24,_taaStabilityEntry=.99,_dlaaPresetEntry="M"})
        if arm[2]=="TAA" then q.shimmer_config(table.unpack(arm,4)) end
        Test.camera.orbit {distance=35,yaw=0,pitch=30,fov=55}
        Test.render.wait_stable(120)
        local selected=arm[2]=="TAA" and "Custom TAA" or (arm[1]=="fsr" and "FSR2 Native AA" or arm[2])
        Test.assert.equal(q.selected(),selected,"Selected backend executes")
        if arm[1]=="reference" then q.reference() end
        q.shimmer_start(arm[1])
        Test.wait["until"](function() return q.shimmer_ready() end,180)
        q.shimmer_stop()
        Test.render.wait_stable(2)
      end
    end
    if q.shimmer_jitter_sweep() then
        for _,arm in ipairs({{"jitter-100-8",1,8},{"jitter-075-16",.75,16},{"jitter-100-16",1,16},
                            {"jitter-075-32",.75,32},{"jitter-100-32",1,32}}) do
            Test.render.set("supersampling",1)
            q.set({_modeEntry="TAA",_sharpnessEntry=.24,_taaStabilityEntry=.99})
            q.shimmer_config(.1,.75,2,8,arm[2],arm[3])
            Test.camera.orbit {distance=35,yaw=0,pitch=30,fov=55}
            Test.render.wait_stable(120)
            Test.assert.equal(q.selected(),"Custom TAA","Custom jitter arm executes")
            q.shimmer_start(arm[1])
            Test.wait["until"](function() return q.shimmer_ready() end,180)
            q.shimmer_stop()
            Test.render.wait_stable(2)
        end
    end
    -- Readbacks and test render targets are gone before measuring normal TAA.
    Test.render.set("supersampling",1)
    q.set({_modeEntry="TAA"})
    q.shimmer_config()
    Test.render.wait_stable(120)
    for i=1,3 do
        q.profile_start()
        Test.wait["until"](function() return q.profile_ready() end,120)
        local profile=q.profile()
        Test.assert.equal(profile.State,"Complete","Timing completed without fallback")
        Test.assert.equal(profile.ResolveManagedBytesTotal,0,"Resolve allocates no managed memory after warmup")
        Test.report.value("custom-profile-"..i,profile)
    end
end)
q.shimmer_stop()
Test.render.set("supersampling",1)
q.set(original)
if not ok then error(err) end
Test.report.note("128 consecutive frames per arm: 32 still, 64 moving, 32 settle; 1/60 capture delta; 120 warmup. Path: "..q.shimmer_path()..". Pan is .1 degrees/frame yaw. Reverse-diagonal uses .2 yaw/.1 pitch degrees/frame for 32 frames, then reverses for 32. Paused physics, normal renderer, sharpness .24, DLAA M. Native linear RGBA16F bottom-row-first crops: structure (1024,512,512,512), terrain (256,896,256,256). SSAA 200% is a spatial reference approximation. Debug 4 rejection, 5 reactive, 6 history weight, 9 depth edges and raw UV motion sampled every 16 frames. Three separate 240-frame profiles follow capture with no readback or file writes.")
