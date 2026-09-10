Test.name("Live AA comparison, movement, supersampling and restoration")
local q = Test.mod.extension("ReduxBetterAA.Quality")
local original = q.settings()
local function ready()
    Test.wait["until"](function()
        local s=q.compare_status()
        if not s.busy then error(s.status) end
        return s.running and s.frames >= 16
    end, 90)
end
local ok, err = pcall(function()
    Test.game.load_save("local/launchpad-fly-safe-15")
    Test.game.wait_for_state("Flight", 60)
    Test.flight.start("Fly Safe-15")
    Test.game.pause()
    Test.camera.mode("Flight")
    Test.camera.target_vessel()
    Test.camera.orbit {distance=45, yaw=0, pitch=35, fov=55}
    Test.render.set("supersampling", 1.0)
    q.set({_modeEntry="NVIDIA DLAA"})
    Test.render.wait_stable(60)
    local normal = q.selected()
    Test.assert.true_(q.panel(true), "F10 leaves the game EventSystem enabled")
    Test.capture.screenshot("f10-simplified-menu", {hideUI=false, waitFrames=2})
    q.panel_page(0,true)
    Test.capture.screenshot("f10-mode-dropdown", {hideUI=false,waitFrames=2})
    q.panel_page(0,false)
    q.panel(false)
    for _, pair in ipairs({{5,6,100,100,"taa-dlaa"}, {5,7,100,100,"taa-fsr"},
        {0,8,100,200,"none-ss200"}, {0,8,100,300,"none-ss300"},
        {6,8,100,300,"dlaa-ss300"}, {0,8,100,400,"none-ss400"}, {5,5,100,100,"taa-taa"}, {6,6,100,100,"dlaa-dlaa"}, {7,7,100,100,"fsr-fsr"}, {4,4,100,100,"ppv2-ppv2"}, {1,3,100,100,"fxaa-smaa"}, {4,0,100,100,"ppv2-none"}}) do
        q.compare(pair[1],pair[2],pair[3],pair[4])
        ready()
        if pair[5]=="none-ss300" then
            q.panel_page(1)
            Test.capture.screenshot("f10-compare-menu", {hideUI=false,waitFrames=2})
            q.panel(false)
        end
        local start=q.compare_status()
        Test.report.value(pair[5].."-status",start)
        if pair[1]>=5 and pair[1]<=7 then Test.assert.true_(start.left_history_valid,"Left history accumulates") end
        if pair[2]>=5 and pair[2]<=7 then Test.assert.true_(start.right_history_valid,"Right history accumulates") end
        if pair[1]==5 then Test.assert.true_(start.left_history_used,"Left TAA resolve consumes history") end
        if pair[2]==5 then Test.assert.true_(start.right_history_used,"Right TAA resolve consumes history") end
        Test.assert.true_(start.independent,"Separate backend and image ownership")
        if pair[2]==6 or pair[2]==7 then Test.assert.true_(start.right_context,"Vendor context executes") end
        if pair[2]==8 then
            Test.assert.equal(start.right_width,start.left_width*pair[4]/100,"Actual supersampling width")
            Test.assert.equal(start.right_height,start.left_height*pair[4]/100,"Actual supersampling height")
        end
        for step=1,3 do
            Test.camera.orbit {distance=45,yaw=step*4,pitch=35,fov=55}
            Test.render.wait_stable(1)
            local image=q.compare_capture(pair[5].."-pan-"..step)
            Test.report.value(pair[5].."-pan-"..step,image)
            if pair[1]==5 and pair[2]==5 then
                Test.assert.true_(math.abs(image.left_motion_pixels-image.right_motion_pixels)<0.01,"Same-mode passes retain coherent camera/object motion")
            end
            Test.assert.equal(image.left_invalid+image.right_invalid,0,"Both live outputs finite")
            Test.assert.true_(image.left_variance>0.00001 and image.right_variance>0.00001,"Both outputs contain scene detail")
        end
        Test.assert.equal(q.compare_status().resets,start.resets,"Ordinary camera movement preserves histories")
        Test.assert.true_(q.compare_status().frames>start.frames,"Both arms continue rendering while camera moves")
        Test.capture.screenshot(pair[5],{hideUI=false,waitFrames=1})
        q.compare_stop()
        Test.render.wait_stable(8)
        Test.assert.equal(q.selected(),normal,"Normal AA restored after stop")
    end
    q.set({_modeEntry="Supersampling", _supersamplingEntry=175})
    Test.render.wait_stable(30)
    Test.assert.equal(q.selected(), "Supersampling", "Normal supersampling selected")
    q.compare(5,6)
    ready()
    q.compare_stop()
    Test.render.wait_stable(30)
    Test.assert.equal(q.selected(), "Supersampling", "Supersampling restored after comparison")
    q.set({_modeEntry="Off"})
    Test.render.wait_stable(15)
    q.compare(5,6)
    ready()
    Test.game.unpause()
    Test.render.wait_stable(60)
    Test.assert.true_(q.compare_status().running,"Comparison continues with simulation running")
    Test.game.pause()
    Test.mod.extension("ReduxBetterAA").open_map_view()
    Test.game.wait_for_state("Map3DView",30)
    Test.render.wait_stable(8)
    Test.assert.true_(not q.compare_status().busy,"Scene change stops comparison")
end)
q.compare_stop()
q.panel(false)
q.set(original)
if not ok then error(err) end

