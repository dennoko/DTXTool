using NUnit.Framework;
using UnityEngine;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>同梱シェーダーが見つかり、この環境でコンパイルできること。</summary>
    public sealed class ShaderTests
    {
        [TestCase("Hidden/DTXTool/Compose", 10)]
        [TestCase("Hidden/DTXTool/LayerMask", 11)]
        [TestCase("Hidden/DTXTool/UVRaster", 4)]
        [TestCase("Hidden/DTXTool/ImageOps", 6)]
        [TestCase("Hidden/DTXTool/Overlay", 2)]
        [TestCase("Hidden/DTXTool/Line", 2)]
        public void ShaderIsAvailable(string name, int passCount)
        {
            var shader = Shader.Find(name);
            Assert.IsNotNull(shader, name + " not found");
            Assert.IsTrue(shader.isSupported, name + " is not supported");
            Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(shader), name + " has compile errors");
            Assert.AreEqual(passCount, shader.passCount, name + " pass count");
        }
    }
}
