Test.name("User Test hills depth-only fix across native AA and saved quality")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
q.terrain_set("Off")
Test.render.wait_stable(120)
q.terrain_start("Off-paused",128,768,896)
Test.wait["until"](function() return q.terrain_ready() end,90)
for _,mode in ipairs({"NVIDIA DLAA","TAA","FSR 2 Native AA"}) do
    for _,paused in ipairs({true,false}) do
        if paused then Test.game.pause() else Test.game.unpause() end
        for _,candidate in ipairs({"none","pqs-depth"}) do
            q.terrain_candidate(candidate)
            q.terrain_set(mode)
            Test.render.wait_stable(120)
            local label=string.gsub(mode,"[^%w]","").."-"..(paused and "paused" or "live").."-"..candidate
            q.terrain_start(label,128,768,896)
            Test.wait["until"](function() return q.terrain_ready() end,90)
        end
    end
end
q.terrain_candidate("none")
