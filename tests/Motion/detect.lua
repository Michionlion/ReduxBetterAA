Test.name("Motion cadence detector without video or color readback")
local q=Test.mod.extension("ReduxBetterAA.Motion")
local modes={{"dlaa","NVIDIA DLAA"},{"taa","TAA"},{"fsr","FSR 2 Native AA"}}
for _,mode in ipairs(modes) do
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
    q.set(mode[2])
    Test.render.wait_stable(120)
    local expected=mode[1]=="taa" and "Custom TAA" or (mode[1]=="fsr" and "FSR2 Native AA" or "NVIDIA DLAA")
    Test.assert.equal(q.selected(),expected,"Requested backend active")
    if mode[1]=="dlaa" then
        q.start("paused-paired-dlaa",120,true)
        Test.wait["until"](function() return q.ready() end,90)
    end
    Test.game.unpause()
    Test.wait.frames(30)
    local start=q.clock()
    Test.wait.frames(240)
    Test.report.metric(mode[1].."-render-fps-without-readback",240/(q.clock()-start))
    q.start("vectors-"..mode[1],480,false)
    Test.wait["until"](function() return q.ready() end,90)
end
Test.game.pause()
Test.report.note("The detector reads three 64x36 float textures asynchronously per rendered frame. It does not redirect scene output or copy color. Reported frame IDs and measured timing, not the target FPS setting, determine sample validity.")
