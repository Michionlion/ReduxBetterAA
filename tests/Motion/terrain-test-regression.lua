Test.name("Saved northwest hills regression, quality and cost")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.assert.true_(q.terrain_production(),"Production projection scope is installed")
local function settle(mode,candidate)
    q.terrain_set(mode)
    q.terrain_candidate(candidate)
    Test.render.wait_stable(128)
end
local function capture(label,count)
    q.terrain_start(label,count or 128,768,896)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
settle("Off","none")
capture("Off-before")
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    local label=string.gsub(mode,"[^%w]","")
    settle(mode,"production-bypass")
    capture(label.."-before")
    settle(mode,"none")
    capture(label.."-after")
    Test.capture.screenshot(label.."-after",{hideUI=true,waitFrames=0})
end
settle("Off","none")
capture("Off-after")
-- Paired timing windows exclude all image/depth readback and reflection audit.
q.fps(-1)
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    for i,candidate in ipairs({"production-bypass","none","production-bypass","none"}) do
        settle(mode,candidate)
        q.terrain_profile_start(string.gsub(mode,"[^%w]","").."-"..i.."-"..candidate,360)
        Test.wait["until"](function() return q.terrain_profile_ready() end,90)
    end
end
q.terrain_candidate("none")
q.fps(120)
-- A repeatable slow sweep exercises projection restoration while changing the
-- camera. The stationary flicker threshold is deliberately inapplicable here.
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    settle(mode,"none")
    q.terrain_start(string.gsub(mode,"[^%w]","").."-pan",128,768,896)
    for i=1,140 do
        Test.camera.orbit {distance=728.08800791755539,yaw=280.20928236709426+4*math.sin(i*math.pi/140),pitch=22.533484576735646,fov=60}
        Test.wait.frames(1)
    end
    Test.camera.release()
    Test.wait["until"](function() return q.terrain_ready() end,90)
    Test.render.wait_stable(100)
end
-- Fresh load, normal control inputs and native camera during actual ascent.
-- No transform, physics-rate, interpolation or velocity overrides.
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    Test.game.load_save("local/terrain-test-launchpad4")
    Test.game.wait_for_state("Flight",60)
    Test.game.pause()
    settle(mode,"none")
    Test.game.unpause()
    Test.flight.set_throttle(1)
    Test.flight.stage()
    Test.wait.seconds(8)
    capture(string.gsub(mode,"[^%w]","").."-launch")
    Test.capture.screenshot(string.gsub(mode,"[^%w]","").."-launch",{hideUI=true,waitFrames=0})
end
settle("Off","none")
