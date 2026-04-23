using UnityEngine;

namespace Virnect.MyPackage
{
    public class Example : MonoBehaviour
    {
        [SerializeField] private string message = "Hello from MyPackage!";

        private void Start()
        {
            Debug.Log(message);
        }

        public void DoSomething()
        {
            Debug.Log($"[MyPackage] {message}");
        }
    }
}
