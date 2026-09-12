using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Tests
{
    internal static class Ppv2TestLayer
    {
        public static PostProcessLayer Create(GameObject owner)
        {
            // Ownership tests need a real layer's AA state, not PPv2's renderer.
            // Keep its player-only OnEnable/OnDisable out of EditMode. The camera
            // stays active, so backend support checks still run normally.
            var child = new GameObject("PPv2 state");
            child.SetActive(false);
            child.transform.SetParent(owner.transform, false);
            var layer = child.AddComponent<PostProcessLayer>();
            layer.Init(null);

            // An empty effect stack is sufficient; ResetHistory still executes
            // the real temporal-AA reset. Fail explicitly if Redux changes this field.
            var bundles = typeof(PostProcessLayer).GetField("m_Bundles",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(bundles, Is.Not.Null, "PPv2 effect-stack layout changed");
            bundles.SetValue(layer, new Dictionary<Type, PostProcessBundle>());
            return layer;
        }
    }
}
