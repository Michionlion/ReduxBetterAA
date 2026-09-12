Test.name("User Test save northwest hills reproduction")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
-- Keep the save's camera and active vessel. No orbit override or staging.
Test.game.pause()
q.fps(120)
for _,mode in ipairs({"NVIDIA DLAA","Off","TAA","FSR 2 Native AA"}) do
    q.set(mode)
    Test.render.wait_stable(120)
    local label=string.gsub(mode,"[^%w]","")
    Test.capture.screenshot("saved-view-"..label,{scale=1,hideUI=true,waitFrames=0})
    q.terrain_start(label,60,896,650)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
