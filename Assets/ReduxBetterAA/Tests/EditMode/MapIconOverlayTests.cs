using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class MapIconOverlayTests
    {
        [Test]
        public void ShutdownRestoresOnlyClaimedSpritesAndRemovesItsCommandBuffer()
        {
            var go = new GameObject("MapCamera");
            var icon = new GameObject("icon");
            var hidden = new GameObject("externally hidden");
            MapIconOverlay owner = null;
            try
            {
                var camera = go.AddComponent<Camera>();
                var sprite = icon.AddComponent<SpriteRenderer>();
                var other = hidden.AddComponent<SpriteRenderer>();
                other.forceRenderingOff = true;
                owner = MapIconOverlay.Attach(camera);
                var claims = (List<SpriteRenderer>)typeof(MapIconOverlay).GetField("_claimed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
                claims.Add(sprite);
                sprite.forceRenderingOff = true;
                Assert.That(camera.GetCommandBuffers(CameraEvent.AfterImageEffects).Length, Is.EqualTo(1));
                owner.Shutdown();
                owner.Shutdown();
                Assert.That(sprite.forceRenderingOff, Is.False);
                Assert.That(other.forceRenderingOff, Is.True);
                Assert.That(camera.GetCommandBuffers(CameraEvent.AfterImageEffects), Is.Empty);
                Assert.That(MapIconOverlay.Current, Is.Null);
            }
            finally
            {
                if (owner != null) Object.DestroyImmediate(owner);
                Object.DestroyImmediate(icon); Object.DestroyImmediate(hidden); Object.DestroyImmediate(go);
            }
        }
    }
}
