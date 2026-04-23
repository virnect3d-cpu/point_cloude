using UnityEditor;
using UnityEngine;

namespace Virnect.MyPackage.Editor
{
    [CustomEditor(typeof(Example))]
    public class ExampleEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var example = (Example)target;
            if (GUILayout.Button("Do Something"))
            {
                example.DoSomething();
            }
        }
    }
}
