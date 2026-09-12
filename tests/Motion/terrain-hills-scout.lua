Test.name("Northwest hills framing during ascent")
local q=Test.mod.extension("ReduxBetterAA.Motion")
Test.game.load_save("local/launchpad-fly-safe-15")
Test.game.wait_for_state("Flight",60)
Test.flight.start("Fly Safe-15")
Test.camera.mode("Flight")
Test.camera.orbit {distance=60,yaw=45,pitch=12,fov=55}
Test.camera.release()
q.fps(120)
q.set("NVIDIA DLAA")
Test.render.wait_stable(90)
Test.game.unpause()
Test.flight.set_sas(true)
Test.flight.set_throttle(1)
Test.flight.stage()
Test.wait["until"](function() return q.altitude()>850 end,90)
Test.game.pause()
for _,yaw in ipairs({45,135,225,315}) do
    Test.camera.orbit {distance=60,yaw=yaw,pitch=12,fov=55}
    Test.camera.release()
    Test.render.wait_stable(90)
    Test.capture.screenshot("hills-yaw-"..yaw,{scale=1,hideUI=true,waitFrames=0})
end
-- Small proof that the terrain recorder receives native pixels and Off too.
for _,mode in ipairs({"NVIDIA DLAA","Off"}) do
    q.set(mode)
    Test.render.wait_stable(90)
    q.terrain_start(mode=="Off" and "scout-off" or "scout-dlaa",24,1280,650)
    Test.wait["until"](function() return q.terrain_ready() end,90)
end
