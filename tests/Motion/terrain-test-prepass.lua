Test.name("User Test hills terrain prepass projection isolation")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    for _,candidate in ipairs({"none","pqs-prepass","pqs-all-buffers"}) do
        q.terrain_candidate(candidate)
        q.set(mode)
        Test.render.wait_stable(120)
        local label=string.gsub(mode,"[^%w]","").."-"..candidate
        q.terrain_start(label,96,768,896)
        Test.wait["until"](function() return q.terrain_ready() end,90)
    end
end
q.terrain_candidate("none")
