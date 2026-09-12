Test.name("Native-resolution motion-filter detail comparison")
local q=Test.mod.extension("ReduxBetterAA.Motion")
local arms={
    {"detail-baseline-dlaa","NVIDIA DLAA","none"},
    {"detail-ema-dlaa","NVIDIA DLAA","motion-ema"},
    {"detail-baseline-taa","TAA","none"},
    {"detail-ema-taa","TAA","motion-ema"},
    {"detail-baseline-fsr","FSR 2 Native AA","none"},
    {"detail-ema-fsr","FSR 2 Native AA","motion-ema"}
}
-- Warm the flight renderer before reloading the first measured arm.
Test.game.load_save("local/motion-day-descent-280")
Test.game.wait_for_state("Flight",60)
Test.game.pause()
q.set("NVIDIA DLAA")
Test.render.wait_stable(120)
for _,arm in ipairs(arms) do
    q.candidate("none")
    Test.game.load_save("local/motion-day-descent-280")
    Test.game.wait_for_state("Flight",60)
    Test.flight.start("Fly Safe-2")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    Test.camera.orbit {distance=45,yaw=20,pitch=60,fov=55}
    Test.flight.set_throttle(0)
    q.fps(120)
    q.set(arm[2])
    q.candidate(arm[3])
    Test.render.wait_stable(120)
    local expected=arm[2]=="TAA" and "Custom TAA" or (arm[2]=="FSR 2 Native AA" and "FSR2 Native AA" or arm[2])
    Test.assert.equal(q.selected(),expected,"Requested backend active")
    Test.game.unpause()
    Test.wait.frames(30)
    q.start(arm[1],240,true,true)
    Test.wait["until"](function() return q.ready() end,90)
end
q.candidate("none")
Test.game.pause()
Test.report.note("Same-frame 1024x256 central source/output strips preserve native pixel scale. They supplement the small scene captures; this is not a supersampled ground-truth comparison.")
