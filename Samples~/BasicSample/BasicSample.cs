using UnityEngine;

namespace Virnect.MyPackage.Samples
{
    public class BasicSample : MonoBehaviour
    {
        private void Start()
        {
            var example = gameObject.AddComponent<Example>();
            example.DoSomething();
        }
    }
}
