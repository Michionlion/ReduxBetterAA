Test.name("User Test hills same-scene terrain isolation")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/terrain-test-launchpad4")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.fps(120)
local arms={
    {"Off","none"},
    {"NVIDIA DLAA","none"},
    {"NVIDIA DLAA","transparent-jitter"},
    {"NVIDIA DLAA","zero-jitter"},
    {"NVIDIA DLAA","stable-culling"},
    {"NVIDIA DLAA","shadows-off"},
    {"TAA","none"},
    {"TAA","transparent-jitter"},
    {"FSR 2 Native AA","none"},
    {"FSR 2 Native AA","transparent-jitter"},
    {"NVIDIA DLAA","none"}
}
for i,arm in ipairs(arms) do
    q.terrain_candidate(arm[2])
    q.set(arm[1])
    Test.render.wait_stable(120)
    local label=string.format("%02d",i).."-"..string.gsub(arm[1],"[^%w]","").."-"..arm[2]
    q.terrain_start(label,96,768,896)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
q.terrain_candidate("none")
