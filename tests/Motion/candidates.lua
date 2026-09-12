Test.name("Controlled descending-vessel motion candidates; physics solver and timestep unchanged")
local q=Test.mod.extension("ReduxBetterAA.Motion")
local arms={
    {"baseline-dlaa","NVIDIA DLAA","none"},
    {"zero-jitter-dlaa","NVIDIA DLAA","zero-jitter"},
    {"half-jitter-dlaa","NVIDIA DLAA","half-jitter"},
    {"motion-ema-dlaa","NVIDIA DLAA","motion-ema"},
    {"camera-interpolate-dlaa","NVIDIA DLAA","camera-interpolate"},
    {"baseline-taa","TAA","none"},
    {"motion-ema-taa","TAA","motion-ema"},
    {"camera-interpolate-taa","TAA","camera-interpolate"},
    {"baseline-fsr","FSR 2 Native AA","none"},
    {"motion-ema-fsr","FSR 2 Native AA","motion-ema"},
    {"camera-interpolate-fsr","FSR 2 Native AA","camera-interpolate"}
}
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
    local selected=arm[2]=="TAA" and "Custom TAA" or (arm[2]=="FSR 2 Native AA" and "FSR2 Native AA" or arm[2])
    Test.assert.equal(q.selected(),selected,"Requested backend active")
    Test.assert.greater(q.speed(),200,"High speed fixture loaded")
    Test.assert.less(q.speed(),350,"No speed explosion")
    Test.game.unpause()
    Test.wait.frames(30)
    q.start(arm[1],480)
    Test.wait["until"](function() return q.ready() end,90)
    Test.report.metric(arm[1].."-speed",q.speed())
end
q.candidate("none")
Test.game.pause()
Test.report.note("Each arm reloads one SHA-256 identified initial condition. Test-only render candidates; no change to gravity, forces, physics timestep, Rigidbody interpolation, or vessel transforms during the run. Real render timing is measured, not forced by a video clock.")
