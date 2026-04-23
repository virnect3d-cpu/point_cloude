using NUnit.Framework;
using UnityEngine;

namespace Virnect.MyPackage.Tests
{
    public class ExampleTests
    {
        [Test]
        public void Example_Instantiates()
        {
            var go = new GameObject("ExampleTest");
            var example = go.AddComponent<Example>();
            Assert.IsNotNull(example);
            Object.DestroyImmediate(go);
        }
    }
}
