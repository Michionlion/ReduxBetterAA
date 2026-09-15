using NUnit.Framework;
using ReduxBetterAA.Rendering;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationPlayerLoopTests
    {
        private sealed class Foreign { }
        [Test]
        public void BoundariesBracketSimulationAndUninstallPreservesLaterForeignChange()
        {
            int order = 0;
            PlayerLoopSystem.UpdateFunction begin = () => Assert.That(++order, Is.EqualTo(1));
            PlayerLoopSystem.UpdateFunction early = () => Assert.That(++order, Is.EqualTo(2));
            PlayerLoopSystem.UpdateFunction late = () => Assert.That(++order, Is.EqualTo(3));
            PlayerLoopSystem.UpdateFunction render = () => Assert.That(++order, Is.EqualTo(4));
            var loop = new PlayerLoopSystem { subSystemList = new[] {
                new PlayerLoopSystem { type = typeof(EarlyUpdate), subSystemList = new[] { new PlayerLoopSystem { updateDelegate = early } } },
                new PlayerLoopSystem { type = typeof(PreLateUpdate), subSystemList = new[] { new PlayerLoopSystem { updateDelegate = late } } }
            }};
            Assert.That(FrameGenerationPlayerLoop.TryInsert(ref loop, begin, render), Is.True);
            Run(loop);
            Assert.That(order, Is.EqualTo(4));
            Assert.That(FrameGenerationPlayerLoop.TryInsert(ref loop, begin, render), Is.False);
            var foreign = new PlayerLoopSystem { type = typeof(Foreign), updateDelegate = () => { } };
            var edited = loop.subSystemList[0].subSystemList;
            System.Array.Resize(ref edited, edited.Length + 1);
            edited[edited.Length - 1] = foreign;
            loop.subSystemList[0].subSystemList = edited;
            FrameGenerationPlayerLoop.RemoveOwned(ref loop, begin, render);
            Assert.That(loop.subSystemList[0].subSystemList.Length, Is.EqualTo(2));
            Assert.That(loop.subSystemList[0].subSystemList[1].type, Is.EqualTo(typeof(Foreign)));
            Assert.That(loop.subSystemList[1].subSystemList.Length, Is.EqualTo(1));
        }

        [Test]
        public void MissingOrDuplicateBoundaryRejectsWithoutPartialMutation()
        {
            PlayerLoopSystem.UpdateFunction callback = () => { };
            var children = new[] { new PlayerLoopSystem { type = typeof(EarlyUpdate) } };
            var loop = new PlayerLoopSystem { subSystemList = children };
            Assert.That(FrameGenerationPlayerLoop.TryInsert(ref loop, callback, callback), Is.False);
            Assert.That(loop.subSystemList, Is.SameAs(children));
            loop.subSystemList = new[] { children[0], children[0], new PlayerLoopSystem { type = typeof(PreLateUpdate) } };
            Assert.That(FrameGenerationPlayerLoop.TryInsert(ref loop, callback, callback), Is.False);
        }

        private static void Run(PlayerLoopSystem node)
        {
            node.updateDelegate?.Invoke();
            if (node.subSystemList != null) foreach (var child in node.subSystemList) Run(child);
        }
    }
}
