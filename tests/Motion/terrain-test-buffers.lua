Test.name("User Test hills isolate each auxiliary terrain buffer")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
for _,candidate in ipairs({"none","pqs-depth","pqs-decal","pqs-depth-decal","pqs-all-buffers"}) do
    q.terrain_candidate(candidate)
    q.set("NVIDIA DLAA")
    Test.render.wait_stable(120)
    q.terrain_start("DLAA-"..candidate,96,768,896)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
for _,mode in ipairs({"TAA","FSR 2 Native AA"}) do
    q.terrain_candidate("pqs-decal")
    q.set(mode)
    Test.render.wait_stable(120)
    q.terrain_start(string.gsub(mode,"[^%w]","").."-pqs-decal",96,768,896)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
q.terrain_candidate("none")
