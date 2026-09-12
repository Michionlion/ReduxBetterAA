Test.name("Production hills depth alignment and cost validation")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.assert.true_(q.terrain_production(),"Production projection scope is installed")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
for _,mode in ipairs({"Off","NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    q.terrain_set(mode)
    Test.render.wait_stable(120)
    q.terrain_start(string.gsub(mode,"[^%w]","").."-production",128,768,896)
    Test.wait["until"](function() return q.terrain_ready() end,90)
    Test.capture.screenshot(string.gsub(mode,"[^%w]","").."-production",{hideUI=true,waitFrames=0})
end
-- Alternate the same shipped DLL with its new scope bypassed. No image or
-- depth readbacks run in these timing windows; the game's draw count is equal.
q.fps(-1)
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    q.terrain_set(mode)
    for i,candidate in ipairs({"production-bypass","none","production-bypass","none"}) do
        q.terrain_candidate(candidate)
        Test.render.wait_stable(120)
        q.terrain_profile_start(string.gsub(mode,"[^%w]","").."-"..i.."-"..candidate,360)
        Test.wait["until"](function() return q.terrain_profile_ready() end,90)
    end
end
q.terrain_candidate("none")
q.fps(120)
-- Repeated switching checks stale projection ownership and reacquisition.
for i=1,2 do
    for _,mode in ipairs({"Off","NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
        q.terrain_set(mode)
        Test.render.wait_stable(40)
    end
end
q.terrain_set("Off")
Test.render.wait_stable(60)
q.terrain_start("Off-after-switches",32,768,896)
Test.wait["until"](function() return q.terrain_ready() end,90)
