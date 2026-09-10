Test.name("Four continuous live AA comparison videos")
local q = Test.mod.extension("ReduxBetterAA.Quality")
local original = q.settings()
local ok,err=pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight",60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    Test.render.set("supersampling",1)
    q.panel(false)
    Test.report.value("settings",q.settings())
    for _,p in ipairs({{5,6,"taa-dlaa"},{5,0,"taa-none"},{5,8,"taa-supersampling"},{6,8,"dlaa-supersampling"}}) do
        Test.camera.orbit {distance=35,yaw=-10,pitch=30,fov=55}
        q.compare(p[1],p[2],100,200)
        Test.wait["until"](function()
            local s=q.compare_status()
            if not s.busy then error(s.status) end
            return s.running and s.frames>=120
        end,90)
        local s=q.compare_status()
        Test.assert.true_(s.independent,"Independent comparison outputs")
        if p[1]==5 then Test.assert.true_(s.left_history_used,"Custom TAA uses history") end
        if p[1]==6 then Test.assert.true_(s.left_context,"Left DLAA context active") end
        if p[2]==6 then Test.assert.true_(s.right_context,"Right DLAA context active") end
        Test.report.value(p[3].."-start",s)
        q.video_start(p[3])
        Test.wait["until"](function() return q.video_ready() end,600)
        Test.report.value(p[3].."-file",q.video_finish())
        Test.assert.equal(q.compare_status().resets,s.resets,"No history resets during clip")
        Test.report.value(p[3].."-end",q.compare_status())
        q.compare_stop()
    end
end)
q.video_dispose()
q.compare_stop()
q.set(original)
if not ok then error(err) end
Test.report.note("Four 840-frame 2560x1440 lossless RGB clips, presented at 60 FPS. Consecutive real comparison frames, fixed render delta 1/60, identical rig path, paused physics. No UI in captured scene. Two seconds still, five-second pan, five-second reverse, two-second settle. Original AA settings preserved. Not performance footage.")
