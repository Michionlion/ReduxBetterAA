Test.name("Better AA main menu controlled jitter isolation")
local q = Test.mod.extension("ReduxBetterAA.Quality")
local original = q.settings()
local function run()
    Test.game.wait_for_state("MainMenu", 45)
    Test.render.wait_stable(60)
    q.menu_freeze(true)
    Test.report.value("renderers", q.menu_renderers())
    for _, arm in ipairs({{"zero",false,false},{"opaque",true,false},{"coherent",true,true}}) do
        for _, mode in ipairs({"TAA", "NVIDIA DLAA", "FSR 2 Native AA"}) do
            q.set({_modeEntry="Off"})
            Test.wait.frames(8)
            q.menu_jitter(arm[2],arm[3])
            q.set({_modeEntry=mode,_sharpnessEntry=0})
            Test.render.wait_stable(120)
            local label = arm[1] .. "-" .. string.gsub(mode, "[^%w]", "")
            Test.report.value(label, q.menu_snapshot())
            q.capture(label, 8)
            Test.wait["until"](function() return q.ready() end, 25)
            Test.capture.screenshot(label, {hideUI=true,waitFrames=2})
        end
    end
end
local ok, err = pcall(run)
q.menu_freeze(false)
q.menu_jitter(false,false)
q.set(original)
if not ok then error(err) end
